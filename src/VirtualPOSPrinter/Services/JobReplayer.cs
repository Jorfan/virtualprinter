using System.Net.Sockets;

namespace VirtualPOSPrinter.Services;

public static class JobReplayer
{
    /// <summary>Sends a saved job to host:port over a fresh TCP connection, like a POS client would.</summary>
    public static async Task SendAsync(string file, string host, int port, CancellationToken ct = default)
    {
        var data = await File.ReadAllBytesAsync(file, ct);
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(host, port, timeout.Token);

        var stream = client.GetStream();
        await stream.WriteAsync(data, timeout.Token);
        await stream.FlushAsync(timeout.Token);
        client.Client.Shutdown(SocketShutdown.Send);   // end of job

        // Drain replies until the server closes, so it is never reset mid-read.
        var buf = new byte[256];
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(ct);
        drain.CancelAfter(TimeSpan.FromSeconds(2));
        try { while (await stream.ReadAsync(buf, drain.Token) > 0) { } }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }
}
