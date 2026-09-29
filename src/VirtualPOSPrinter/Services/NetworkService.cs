using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;

namespace VirtualPOSPrinter.Services;

public sealed record AdapterInfo(string Name, string Description, string[] IPv4)
{
    public override string ToString() => IPv4.Length == 0 ? Name : $"{Name}  ({string.Join(", ", IPv4)})";
}

public static class NetworkService
{
    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static List<AdapterInfo> GetAdapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            var ips = nic.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString()).ToArray();
            list.Add(new AdapterInfo(nic.Name, nic.Description, ips));
        }
        return list;
    }

    public static List<string> GetLocalIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .Distinct().ToList();

    public static bool IsLocalAddress(IPAddress ip) =>
        IPAddress.IsLoopback(ip) || GetLocalIPv4().Contains(ip.ToString());

    /// <summary>Adds a secondary IPv4 address to an adapter using netsh. Throws with netsh's message on failure.</summary>
    public static void AddSecondaryIp(string adapter, string ip, string mask) =>
        RunNetsh($"interface ip add address name=\"{adapter}\" addr={ip} mask={mask}");

    public static void RemoveSecondaryIp(string adapter, string ip) =>
        RunNetsh($"interface ip delete address name=\"{adapter}\" addr={ip}");

    private static void RunNetsh(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start netsh.");
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"netsh {args} failed ({p.ExitCode}): {output.Trim()}");
    }
}
