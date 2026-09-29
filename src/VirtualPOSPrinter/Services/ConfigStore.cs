using System.Text.Json;
using System.Text.Json.Serialization;
using VirtualPOSPrinter.Models;

namespace VirtualPOSPrinter.Services;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static List<PrinterConfig> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ConfigFile)) return new();
            return JsonSerializer.Deserialize<List<PrinterConfig>>(File.ReadAllText(AppPaths.ConfigFile), Options) ?? new();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not read {AppPaths.ConfigFile}: {ex.Message}");
            return new();
        }
    }

    public static void Save(IEnumerable<PrinterConfig> printers)
    {
        try
        {
            var tmp = AppPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(printers.ToList(), Options));
            File.Move(tmp, AppPaths.ConfigFile, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not save configuration: {ex.Message}");
        }
    }
}
