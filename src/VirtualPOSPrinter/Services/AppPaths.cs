namespace VirtualPOSPrinter.Services;

public static class AppPaths
{
    /// <summary>Config, jobs and logs live next to the executable (portable).</summary>
    public static string DataDir { get; } = AppContext.BaseDirectory;
    public static string ConfigFile => Path.Combine(DataDir, "printers.json");
    public static string JobsDir => Path.Combine(DataDir, "jobs");
}
