using System.Net;
using System.Net.Sockets;
using VirtualPOSPrinter.EscPos;
using VirtualPOSPrinter.Models;

namespace VirtualPOSPrinter.Services;

/// <summary>
/// One raw-TCP listener for one virtual printer. A job is every byte received on a connection
/// until the client closes it. Events are raised on thread-pool threads.
/// </summary>
public sealed class PrinterListener
{
    private const int MaxJobBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public PrinterListener(PrinterConfig config) => Config = config;

    public PrinterConfig Config { get; }
    public ListenerState State { get; private set; } = ListenerState.Stopped;
    public string StatusMessage { get; private set; } = "Stopped";

    /// <summary>Jobs for this printer, oldest first. Only touched on the UI thread.</summary>
    public List<PrintJob> Jobs { get; set; } = new();

    public event Action<PrinterListener>? StatusChanged;
    public event Action<PrinterListener, PrintJob>? JobSaved;

    public async Task StartAsync()
    {
        Stop();
        if (!Config.Enabled) { SetStatus(ListenerState.Stopped, "Disabled"); return; }
        if (Config.RefuseConnections)
        {
            // Not binding at all: the OS answers connection attempts with "refused".
            SetStatus(ListenerState.Refusing, $"Refusing connections on {Config.Address} (simulated)");
            return;
        }

        if (!IPAddress.TryParse(Config.ListenIp, out var ip))
        {
            SetStatus(ListenerState.Error, $"Invalid IP address '{Config.ListenIp}'");
            return;
        }

        // A freshly added secondary IP can take a moment before it can be bound.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                _listener = new TcpListener(ip, Config.Port);
                _listener.Start();
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressNotAvailable && attempt < 8)
            {
                await Task.Delay(500);
            }
            catch (SocketException ex)
            {
                _listener = null;
                var msg = ex.SocketErrorCode switch
                {
                    SocketError.AddressAlreadyInUse => $"Port {Config.Port} is already in use on {Config.ListenIp}",
                    SocketError.AddressNotAvailable => $"IP {Config.ListenIp} is not available on this machine",
                    SocketError.AccessDenied => $"Access denied binding {Config.Address}",
                    _ => $"Cannot listen on {Config.Address}: {ex.Message}"
                };
                SetStatus(ListenerState.Error, msg);
                return;
            }
        }

        _cts = new CancellationTokenSource();
        var listener = _listener!;
        var ct = _cts.Token;
        SetStatus(ListenerState.Listening, $"Listening on {Config.Address}");
        _ = Task.Run(() => AcceptLoopAsync(listener, ct));
    }

    /// <summary>Marks the printer as failed (e.g. secondary IP could not be added).</summary>
    public void Fail(string message) => SetStatus(ListenerState.Error, message);

    public void Stop()
    {
        var cts = _cts; _cts = null;
        var l = _listener; _listener = null;
        try { cts?.Cancel(); } catch { }
        try { l?.Stop(); } catch { }
        if (State != ListenerState.Stopped) SetStatus(ListenerState.Stopped, "Stopped");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                AppLog.Error($"[{Config.Name}] accept failed: {ex.Message}");
                SetStatus(ListenerState.Error, $"Accept failed: {ex.Message}");
                return;
            }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        string source = "?";
        var ms = new MemoryStream();
        var responder = new StatusResponder();
        try
        {
            using (client)
            {
                source = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
                var stream = client.GetStream();
                if (Config.ConnectionDelayMs > 0) await Task.Delay(Config.ConnectionDelayMs, ct);   // simulated slow printer
                var buffer = new byte[16 * 1024];
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                while (true)
                {
                    idle.CancelAfter(IdleTimeout);
                    int n = await stream.ReadAsync(buffer, idle.Token);
                    if (n == 0) break;                       // client closed: job complete
                    ms.Write(buffer, 0, n);

                    var reply = responder.Feed(buffer.AsSpan(0, n), Config.State);
                    if (reply.Length > 0)
                    {
                        try { await stream.WriteAsync(reply, idle.Token); }
                        catch (Exception ex) when (ex is IOException or SocketException) { /* client already gone */ }
                    }
                    if (ms.Length > MaxJobBytes)
                    {
                        AppLog.Warn($"[{Config.Name}] job from {source} exceeded {MaxJobBytes / 1024 / 1024} MB, truncated");
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested)
                AppLog.Warn($"[{Config.Name}] connection from {source} idle for {IdleTimeout.TotalSeconds:0}s, saving what was received");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[{Config.Name}] connection from {source} failed: {ex.Message}");
        }

        if (ms.Length == 0)
        {
            AppLog.Info($"[{Config.Name}] connection from {source} closed without data (ignored)");
            return;
        }

        var data = ms.ToArray();
        if (responder.QueriesAnswered > 0)
        {
            AppLog.Info($"[{Config.Name}] answered {responder.QueriesAnswered} status queries from {source} ({Config.State})");
            if (EscPosParser.Parse(data).Receipts.Count == 0) return;   // status poll only: not a print job
        }

        try
        {
            var job = JobStore.Save(Config, data, source, DateTime.Now);
            AppLog.Info($"[{Config.Name}] job received from {source}: {job.SizeBytes} bytes");
            JobSaved?.Invoke(this, job);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[{Config.Name}] could not save job: {ex.Message}");
        }
    }

    private void SetStatus(ListenerState state, string message)
    {
        State = state;
        StatusMessage = message;
        if (state == ListenerState.Error) AppLog.Error($"[{Config.Name}] {message}");
        else AppLog.Info($"[{Config.Name}] {message}");
        StatusChanged?.Invoke(this);
    }
}
