using System.Text.Json;
using Microsoft.Win32;

namespace VirtualPOSPrinter.Services;

/// <summary>Application-level options, stored in settings.json next to the executable.</summary>
public sealed class AppSettings
{
    /// <summary>Start hidden in the tray.</summary>
    public bool StartMinimized { get; set; }
    /// <summary>The window's close button hides it to the tray instead of exiting.</summary>
    public bool CloseToTray { get; set; }

    private static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { AppLog.Warn("Could not read settings.json: " + ex.Message); }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { AppLog.Warn("Could not save settings.json: " + ex.Message); }
    }
}

/// <summary>"Start with Windows" via the per-user Run registry key (no admin needed).</summary>
public static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VirtualPOSPrinter";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) != null;
    }

    /// <summary>Throws if the executable path cannot be registered (e.g. started through "dotnet x.dll").</summary>
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(KeyPath);
        if (!enabled) { key.DeleteValue(ValueName, throwOnMissingValue: false); return; }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Start with Windows needs the VirtualPOSPrinter.exe launcher; run the built .exe instead of 'dotnet run'.");
        key.SetValue(ValueName, $"\"{exe}\" --minimized");
    }
}
