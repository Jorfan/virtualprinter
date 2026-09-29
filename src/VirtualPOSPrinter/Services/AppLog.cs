namespace VirtualPOSPrinter.Services;

public sealed record LogEntry(DateTime Time, string Level, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss}  {Level,-5} {Message}";
}

/// <summary>Tiny thread-safe logger: in-memory event for the UI plus a log file.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    public static event Action<LogEntry>? Logged;

    public static string LogFile => Path.Combine(AppPaths.DataDir, "logs", "app.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        var entry = new LogEntry(DateTime.Now, level, msg);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                File.AppendAllText(LogFile, $"{entry.Time:yyyy-MM-dd} {entry}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
        Logged?.Invoke(entry);
    }
}
