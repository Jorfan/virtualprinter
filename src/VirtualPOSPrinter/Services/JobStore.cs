using System.Globalization;
using VirtualPOSPrinter.Models;

namespace VirtualPOSPrinter.Services;

/// <summary>Stores raw jobs as jobs/&lt;printer&gt;/&lt;timestamp&gt;.bin with a small .src sidecar holding the source IP.</summary>
public static class JobStore
{
    private const string TimeFormat = "yyyyMMdd-HHmmss-fff";
    private static readonly object Gate = new();

    public static string FolderFor(PrinterConfig p) => Path.Combine(AppPaths.JobsDir, p.JobFolder);

    public static string MakeFolderName(string printerName, IEnumerable<string> taken)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(printerName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length == 0) clean = "printer";
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var name = clean;
        for (int i = 2; set.Contains(name); i++) name = $"{clean}-{i}";
        return name;
    }

    public static PrintJob Save(PrinterConfig printer, byte[] data, string sourceIp, DateTime receivedAt)
    {
        lock (Gate)
        {
            var dir = FolderFor(printer);
            Directory.CreateDirectory(dir);
            var stem = receivedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
            var path = Path.Combine(dir, stem + ".bin");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{stem}-{i}.bin");
            File.WriteAllBytes(path, data);
            File.WriteAllText(Path.ChangeExtension(path, ".src"), sourceIp);
            return new PrintJob(receivedAt, data.LongLength, sourceIp, path);
        }
    }

    /// <summary>Deletes the job's .bin together with its .src sidecar and any exported .pdf.</summary>
    public static void Delete(PrintJob job)
    {
        foreach (var ext in new[] { ".bin", ".src", ".pdf" })
        {
            var f = Path.ChangeExtension(job.FilePath, ext);
            try { if (File.Exists(f)) File.Delete(f); }
            catch (Exception ex) { AppLog.Warn($"Could not delete {f}: {ex.Message}"); }
        }
    }

    public static List<PrintJob> LoadAll(PrinterConfig printer)
    {
        var jobs = new List<PrintJob>();
        var dir = FolderFor(printer);
        if (!Directory.Exists(dir)) return jobs;
        foreach (var f in Directory.EnumerateFiles(dir, "*.bin"))
        {
            try
            {
                var fi = new FileInfo(f);
                var src = Path.ChangeExtension(f, ".src");
                var ip = File.Exists(src) ? File.ReadAllText(src).Trim() : "?";
                jobs.Add(new PrintJob(ParseTime(f, fi.CreationTime), fi.Length, ip, f));
            }
            catch (Exception ex) { AppLog.Warn($"Skipping job file {f}: {ex.Message}"); }
        }
        return jobs.OrderBy(j => j.ReceivedAt).ToList();
    }

    private static DateTime ParseTime(string file, DateTime fallback)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        if (stem.Length >= TimeFormat.Length &&
            DateTime.TryParseExact(stem[..TimeFormat.Length], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return t;
        return fallback;
    }
}
