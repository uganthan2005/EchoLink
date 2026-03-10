using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EchoLink.Models;

namespace EchoLink.Services;

public class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    private readonly string _settingsPath;

    private SettingsService()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EchoLink");
        Directory.CreateDirectory(appData);
        _settingsPath = Path.Combine(appData, "settings.json");
    }

    public SettingsData Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Failed to load settings: {ex.Message}");
        }
        return new SettingsData();
    }

    public void Save(SettingsData data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_settingsPath, json);
            LoggingService.Instance.Debug("Settings saved.");
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Failed to save settings: {ex.Message}");
        }
    }
}

public class SettingsData
{
    // ── EchoBoard (Clipboard) ──
    public bool MirrorClipEnabled { get; set; } = true;
    public bool GhostPasteEnabled { get; set; } = true;
    public bool SnapShareEnabled { get; set; } = true;
    public int ClipboardHistoryLimit { get; set; } = 50;
    public bool ClipboardUseTargetSelection { get; set; }
    public List<string> ClipboardShareTargets { get; set; } = [];

    // ── General ──
    public bool LaunchOnStartup { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;

    // ── Hotkeys ──
    public List<HotkeyData> Hotkeys { get; set; } = [];
}

public class HotkeyData
{
    public string ActionName { get; set; } = "";
    public string KeyGesture { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
}
