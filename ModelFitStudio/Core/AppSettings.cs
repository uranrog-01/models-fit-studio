using System.IO;
using System.Text.Json;

namespace ModelFitStudio.Core;

/// <summary>What the user chose on the Settings tab.</summary>
public sealed class AppSettingsData
{
    public bool MinimizeToTrayOnClose { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public string? HfToken { get; set; }
    /// <summary>The look of the app: "glass" (the default), "default" (shown as Classic), "ember" or "solar".</summary>
    public string Theme { get; set; } = "glass";
    /// <summary>The language of the interface: "en", or the code of a file in Web/lang.</summary>
    public string Language { get; set; } = "en";
    /// <summary>The server for AI agents: the model last served, its conversation length and port, so the address stays the same.</summary>
    public string? AgentRepo { get; set; }
    public string? AgentFile { get; set; }
    /// <summary>0 is Auto (the model's longest that fits). A new name, so everyone starts on Auto.</summary>
    public int AgentLength { get; set; } = 0;
    public int AgentPort { get; set; } = AgentPlan.DefaultPort;
    /// <summary>A request longer than the model's memory is made to fit (tools it does not use and old turns left out) instead of refused.</summary>
    public bool AgentFit { get; set; } = true;
    /// <summary>Let a thinking model reason before every answer. Off by default: answers start at once.</summary>
    public bool AgentThinking { get; set; } = false;
}

public static class AppSettings
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModelFitStudio");
    private static readonly string SettingsPath = Path.Combine(Folder, "app-settings.json");
    // Earlier versions kept the same three values in this file, next to settings that no longer exist.
    private static readonly string LegacyPath = Path.Combine(Folder, "explorer-settings.json");

    private static AppSettingsData _settings = Load();

    public static AppSettingsData Get() => _settings;

    public static void Save(AppSettingsData s)
    {
        _settings = s;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
            SyncStartWithWindows(s.StartWithWindows);
        }
        catch { }
    }

    public static void SyncStartWithWindows(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            var exePath = Environment.ProcessPath;
            if (enable && !string.IsNullOrWhiteSpace(exePath))
            {
                key.SetValue("ModelFitStudio", $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue("ModelFitStudio", false);
            }
        }
        catch { }
    }

    private static AppSettingsData Load()
    {
        foreach (var path in new[] { SettingsPath, LegacyPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var s = JsonSerializer.Deserialize<AppSettingsData>(File.ReadAllText(path));
                if (s is null) continue;
                if (path == LegacyPath)
                {
                    // carry the token and the tray choices over once, then drop the old file
                    File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
                    File.Delete(LegacyPath);
                }
                return s;
            }
            catch { }
        }
        return new AppSettingsData();
    }
}
