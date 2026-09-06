using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SimDeck.App;

/// <summary>
/// User preferences. Persisted as JSON in the data folder; the startup entry
/// is applied to the registry so Windows, not SimDeck, does the launching.
/// </summary>
public sealed class AppSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "SimDeck";

    /// <summary>Start with Windows, minimised to the notification area.</summary>
    public bool LaunchAtStartup { get; set; }

    /// <summary>
    /// Close hides to the tray and keeps the panels running (default). Off,
    /// the close button quits outright - for people who would rather not
    /// have a background process, and know their gauges stop when it goes.
    /// </summary>
    public bool CloseToTray { get; set; } = true;

    public static string Path => System.IO.Path.Combine(App.DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new();
        }
        catch { }
        var s = new AppSettings();
        // first run: reflect whatever the registry already says
        s.LaunchAtStartup = ReadStartupEntry();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataDir);
            File.WriteAllText(Path, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        ApplyStartupEntry();
    }

    private static bool ReadStartupEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(RunName) is string;
        }
        catch { return false; }
    }

    /// <summary>
    /// HKCU\...\Run is per-user, needs no elevation, and is what the Startup
    /// folder shortcut also resolves to. The path is quoted and carries
    /// --minimised so a Windows login does not pop the window up.
    /// </summary>
    private void ApplyStartupEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;

            if (LaunchAtStartup)
            {
                var exe = Environment.ProcessPath ?? "";
                if (exe.Length > 0) key.SetValue(RunName, $"\"{exe}\" --minimised");
            }
            else
            {
                key.DeleteValue(RunName, throwOnMissingValue: false);
            }
        }
        catch { /* registry refusal is not worth a dialog; the checkbox just will not stick */ }
    }
}
