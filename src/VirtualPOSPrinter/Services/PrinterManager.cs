using System.Net;
using VirtualPOSPrinter.EscPos;
using VirtualPOSPrinter.Models;

namespace VirtualPOSPrinter.Services;

/// <summary>
/// Owns all printers and their listeners, persists the config and manages secondary IPs.
/// Call InitializeAsync on the UI thread: its events are marshalled back to that thread.
/// </summary>
public sealed class PrinterManager
{
    private SynchronizationContext? _ui;
    private readonly List<PrinterListener> _printers = new();

    public IReadOnlyList<PrinterListener> Printers => _printers;

    /// <summary>Status or configuration of a printer changed.</summary>
    public event Action<PrinterListener>? PrinterChanged;
    public event Action<PrinterListener, PrintJob>? JobReceived;

    /// <summary>Must be called on the UI thread; captures it for event marshalling.</summary>
    public async Task InitializeAsync()
    {
        _ui = SynchronizationContext.Current;
        foreach (var cfg in ConfigStore.Load())
        {
            if (string.IsNullOrWhiteSpace(cfg.JobFolder))
                cfg.JobFolder = JobStore.MakeFolderName(cfg.Name, _printers.Select(p => p.Config.JobFolder));
            var l = Wrap(cfg);
            l.Jobs = JobStore.LoadAll(cfg);
            _printers.Add(l);
        }
        await Task.WhenAll(_printers.ToList().Select(StartPrinterAsync));
        Save();
    }

    public async Task<PrinterListener> AddAsync(PrinterConfig cfg)
    {
        cfg.JobFolder = JobStore.MakeFolderName(cfg.Name, _printers.Select(p => p.Config.JobFolder));
        var l = Wrap(cfg);
        _printers.Add(l);
        await StartPrinterAsync(l);
        Save();
        return l;
    }

    public async Task UpdateAsync(PrinterListener l, PrinterConfig edited)
    {
        l.Stop();
        var c = l.Config;

        // Release the old secondary IP if it is no longer wanted as configured.
        bool sameSecondary = edited.IpMode == IpMode.SecondaryIp && c.IpMode == IpMode.SecondaryIp &&
                             edited.ListenIp == c.ListenIp && edited.AdapterName == c.AdapterName;
        if (!sameSecondary) await ReleaseSecondaryIpAsync(c);

        c.CopyFrom(edited);
        Save();
        PrinterChanged?.Invoke(l);
        await StartPrinterAsync(l);
        Save();
    }

    public async Task RemoveAsync(PrinterListener l)
    {
        l.Stop();
        await ReleaseSecondaryIpAsync(l.Config);
        _printers.Remove(l);
        Save();
    }

    /// <summary>Changes the simulated state immediately; the listener reads it live.</summary>
    public void SetState(PrinterListener l, PrinterState state)
    {
        l.Config.State = state;
        Save();
        AppLog.Info($"[{l.Config.Name}] simulated state: {state}");
        PrinterChanged?.Invoke(l);
    }

    public async Task SetRefuseConnectionsAsync(PrinterListener l, bool refuse)
    {
        l.Config.RefuseConnections = refuse;
        Save();
        await StartPrinterAsync(l);
    }

    public void DeleteJobs(PrinterListener l, IEnumerable<PrintJob> jobs)
    {
        foreach (var job in jobs.ToList())
        {
            JobStore.Delete(job);
            l.Jobs.Remove(job);
        }
        PrinterChanged?.Invoke(l);
    }

    public void ClearJobs(PrinterListener l) => DeleteJobs(l, l.Jobs);

    /// <summary>Synchronous shutdown for app exit: stop listeners and remove secondary IPs we added.</summary>
    public void Shutdown()
    {
        foreach (var l in _printers)
        {
            l.Stop();
            ReleaseSecondaryIp(l.Config);
        }
        Save();
    }

    public void Save() => ConfigStore.Save(_printers.Select(p => p.Config));

    // ---------------------------------------------------------------------------------------

    private PrinterListener Wrap(PrinterConfig cfg)
    {
        var l = new PrinterListener(cfg);
        l.StatusChanged += p => Post(() => PrinterChanged?.Invoke(p));
        l.JobSaved += (p, job) => Post(() =>
        {
            p.Jobs.Add(job);
            JobReceived?.Invoke(p, job);
        });
        return l;
    }

    private void Post(Action a)
    {
        if (_ui != null) _ui.Post(_ => a(), null); else a();
    }

    private async Task StartPrinterAsync(PrinterListener l)
    {
        try
        {
            if (l.Config.Enabled && l.Config.IpMode == IpMode.SecondaryIp)
                await Task.Run(() => EnsureSecondaryIp(l.Config));
            await l.StartAsync();
        }
        catch (Exception ex)
        {
            l.Fail(ex.Message);
        }
    }

    private static void EnsureSecondaryIp(PrinterConfig c)
    {
        if (!IPAddress.TryParse(c.ListenIp, out var ip))
            throw new InvalidOperationException($"Invalid IP address '{c.ListenIp}'");

        if (NetworkService.IsLocalAddress(ip))
        {
            if (c.SecondaryIpAddedByApp) return;   // left over from a previous run; we still own it
            throw new InvalidOperationException(
                $"{c.ListenIp} is already assigned to this machine. Use the 'existing local IP' mode, or pick a free address.");
        }

        if (!NetworkService.IsElevated())
            throw new InvalidOperationException(
                $"Adding the secondary IP {c.ListenIp} requires administrator rights. Restart VirtualPOSPrinter as administrator.");

        NetworkService.AddSecondaryIp(c.AdapterName, c.ListenIp, c.SubnetMask);
        c.SecondaryIpAddedByApp = true;
        AppLog.Info($"[{c.Name}] added secondary IP {c.ListenIp} to '{c.AdapterName}'");
    }

    private Task ReleaseSecondaryIpAsync(PrinterConfig c) => Task.Run(() => ReleaseSecondaryIp(c));

    private static void ReleaseSecondaryIp(PrinterConfig c)
    {
        if (c.IpMode != IpMode.SecondaryIp || !c.SecondaryIpAddedByApp) return;
        try
        {
            if (NetworkService.IsLocalAddress(IPAddress.Parse(c.ListenIp)))
                NetworkService.RemoveSecondaryIp(c.AdapterName, c.ListenIp);
            AppLog.Info($"[{c.Name}] removed secondary IP {c.ListenIp} from '{c.AdapterName}'");
            c.SecondaryIpAddedByApp = false;
        }
        catch (Exception ex)
        {
            AppLog.Error($"[{c.Name}] could not remove secondary IP {c.ListenIp}: {ex.Message}");
        }
    }
}
