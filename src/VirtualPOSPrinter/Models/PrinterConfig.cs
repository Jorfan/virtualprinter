using VirtualPOSPrinter.EscPos;

namespace VirtualPOSPrinter.Models;

public enum IpMode
{
    /// <summary>Bind to an IP that already exists on this machine.</summary>
    ExistingIp,
    /// <summary>Add the IP as a secondary address on a network adapter (netsh, admin).</summary>
    SecondaryIp
}

/// <summary>Persisted configuration of one virtual printer.</summary>
public sealed class PrinterConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Printer";
    public string Label { get; set; } = "Receipt";      // Receipt / Kitchen
    public IpMode IpMode { get; set; } = IpMode.ExistingIp;
    public string ListenIp { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 9100;
    public int PaperWidthMm { get; set; } = 80;         // 80 or 58
    public bool AutoOpen { get; set; } = true;
    public bool Enabled { get; set; } = true;

    // Simulation (Phase 3)
    /// <summary>Condition reported to status queries. Jobs are still accepted in every state.</summary>
    public PrinterState State { get; set; } = PrinterState.Online;
    /// <summary>Wait this long after a connection is accepted before reading from it or replying.</summary>
    public int ConnectionDelayMs { get; set; }
    /// <summary>Close the port so clients get "connection refused".</summary>
    public bool RefuseConnections { get; set; }

    // Secondary-IP mode
    public string AdapterName { get; set; } = "";
    public string SubnetMask { get; set; } = "255.255.255.0";
    /// <summary>True while this app has added ListenIp to the adapter (so it must be removed later).</summary>
    public bool SecondaryIpAddedByApp { get; set; }

    /// <summary>Folder name below /jobs, fixed at creation so renaming keeps the history.</summary>
    public string JobFolder { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string Address => $"{ListenIp}:{Port}";

    public PrinterConfig Clone() => (PrinterConfig)MemberwiseClone();

    public void CopyFrom(PrinterConfig o)
    {
        Name = o.Name; Label = o.Label; IpMode = o.IpMode; ListenIp = o.ListenIp; Port = o.Port;
        PaperWidthMm = o.PaperWidthMm; AutoOpen = o.AutoOpen; Enabled = o.Enabled;
        AdapterName = o.AdapterName; SubnetMask = o.SubnetMask;
        State = o.State; ConnectionDelayMs = o.ConnectionDelayMs; RefuseConnections = o.RefuseConnections;
    }
}
