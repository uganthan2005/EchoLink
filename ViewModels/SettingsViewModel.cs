using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;

    // ── EchoBoard™ — Clipboard Sync Engine ──────────────────────────────
    [ObservableProperty] private bool _mirrorClipEnabled = true;
    [ObservableProperty] private bool _ghostPasteEnabled = true;
    [ObservableProperty] private bool _snapShareEnabled = true;
    [ObservableProperty] private int _clipboardHistoryLimit = 50;
    [ObservableProperty] private bool _isLoadingClipboardDevices;

    private bool _updatingClipboardSelection;

    // ── General ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _launchOnStartup;
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private bool _showNotifications = true;

    // ── Hotkeys ─────────────────────────────────────────────────────────
    public ObservableCollection<HotkeyBinding> Hotkeys { get; } = [];
    public ObservableCollection<ClipboardShareDevice> ClipboardShareDevices { get; } = [];

    // ── Status ──────────────────────────────────────────────────────────
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _showSaved;

    /// <summary>
    /// Fired when settings change so other ViewModels can react.
    /// </summary>
    public event Action? SettingsChanged;

    public SettingsViewModel()
    {
        LoadSettings();
        _ = RefreshClipboardDevicesAsync();
    }

    private void LoadSettings()
    {
        var data = _settings.Load();

        MirrorClipEnabled = data.MirrorClipEnabled;
        GhostPasteEnabled = data.GhostPasteEnabled;
        SnapShareEnabled = data.SnapShareEnabled;
        ClipboardHistoryLimit = data.ClipboardHistoryLimit;

        LaunchOnStartup = data.LaunchOnStartup;
        MinimizeToTray = data.MinimizeToTray;
        ShowNotifications = data.ShowNotifications;

        // Build hotkey list from saved data, falling back to defaults
        Hotkeys.Clear();
        var defaults = GetDefaultHotkeys();
        foreach (var def in defaults)
        {
            var saved = data.Hotkeys.Find(h => h.ActionName == def.ActionName);
            if (saved is not null)
            {
                def.KeyGesture = saved.KeyGesture;
                def.IsEnabled = saved.IsEnabled;
            }
            Hotkeys.Add(def);
        }

        _log.Debug("Settings loaded.");

        ApplyClipboardSelectionFromData(data);
    }

    private static List<HotkeyBinding> GetDefaultHotkeys() =>
    [
        new("EchoShot",      "EchoShot — Push Clipboard",         "Ctrl+Shift+C"),
        new("SnapPull",      "SnapPull — Pull Latest Clip",       "Ctrl+Shift+V"),
        new("SyncToggle",    "Toggle MirrorClip Sync",            "Ctrl+Shift+S"),
        new("QuickTransfer", "QuickTransfer — Send File",         "Ctrl+Shift+F"),
        new("CommandDeck",   "CommandDeck — Dashboard",           "Ctrl+Shift+D"),
        new("WipeBoard",     "WipeBoard — Clear Clip History",    "Ctrl+Shift+X"),
        new("GhostLock",     "GhostLock — Lock Remote Screen",    "Ctrl+Shift+L"),
        new("PulsePing",     "PulsePing — Refresh Network",       "Ctrl+Shift+R"),
    ];

    [RelayCommand]
    private void SaveSettings()
    {
        var selectedTargets = ClipboardShareDevices
            .Where(d => d.IsSelected)
            .Select(d => d.IpAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var data = new SettingsData
        {
            MirrorClipEnabled = MirrorClipEnabled,
            GhostPasteEnabled = GhostPasteEnabled,
            SnapShareEnabled = SnapShareEnabled,
            ClipboardHistoryLimit = ClipboardHistoryLimit,
            ClipboardUseTargetSelection = true,
            ClipboardShareTargets = selectedTargets,

            LaunchOnStartup = LaunchOnStartup,
            MinimizeToTray = MinimizeToTray,
            ShowNotifications = ShowNotifications,

            Hotkeys = Hotkeys.Select(h => new HotkeyData
            {
                ActionName = h.ActionName,
                KeyGesture = h.KeyGesture,
                IsEnabled = h.IsEnabled
            }).ToList()
        };

        _settings.Save(data);
        _ = ClipboardSyncService.Instance.UpdateClipboardShareTargetsAsync(selectedTargets);
        StatusText = "Settings saved";
        ShowSaved = true;
        SettingsChanged?.Invoke();
        _log.Info("Settings saved successfully.");

        _ = HideSavedBadgeAsync();
    }

    private async Task HideSavedBadgeAsync()
    {
        await Task.Delay(2000);
        ShowSaved = false;
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        MirrorClipEnabled = true;
        GhostPasteEnabled = true;
        SnapShareEnabled = true;
        ClipboardHistoryLimit = 50;

        _updatingClipboardSelection = true;
        foreach (var device in ClipboardShareDevices)
            device.IsSelected = true;
        _updatingClipboardSelection = false;

        LaunchOnStartup = false;
        MinimizeToTray = true;
        ShowNotifications = true;

        Hotkeys.Clear();
        foreach (var h in GetDefaultHotkeys())
            Hotkeys.Add(h);

        StatusText = "Defaults restored — click Save to apply";
        _log.Info("Settings reset to defaults.");
    }

    [RelayCommand]
    private async Task RefreshClipboardDevicesAsync()
    {
        if (IsLoadingClipboardDevices)
            return;

        IsLoadingClipboardDevices = true;

        try
        {
            var data = _settings.Load();
            var selected = data.ClipboardShareTargets
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool useTargetSelection = data.ClipboardUseTargetSelection;

            foreach (var item in ClipboardShareDevices)
                item.PropertyChanged -= OnClipboardShareDevicePropertyChanged;

            ClipboardShareDevices.Clear();

            var (_, devices) = await TailscaleService.Instance.GetNetworkStatusAsync();
            foreach (var d in devices.Where(d => !d.IsSelf && !string.IsNullOrWhiteSpace(d.IpAddress)))
            {
                var item = new ClipboardShareDevice
                {
                    Name = d.Name,
                    IpAddress = d.IpAddress,
                    IsOnline = d.IsOnline,
                    IsSelf = d.IsSelf,
                    IsSelected = !useTargetSelection || selected.Contains(d.IpAddress)
                };

                item.PropertyChanged += OnClipboardShareDevicePropertyChanged;
                ClipboardShareDevices.Add(item);
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to refresh clipboard target devices: {ex.Message}");
        }
        finally
        {
            IsLoadingClipboardDevices = false;
        }
    }

    [RelayCommand]
    private void SelectAllClipboardDevices()
    {
        _updatingClipboardSelection = true;
        foreach (var device in ClipboardShareDevices)
            device.IsSelected = true;
        _updatingClipboardSelection = false;
    }

    [RelayCommand]
    private void SelectNoneClipboardDevices()
    {
        _updatingClipboardSelection = true;
        foreach (var device in ClipboardShareDevices)
            device.IsSelected = false;
        _updatingClipboardSelection = false;
    }

    private void ApplyClipboardSelectionFromData(SettingsData data)
    {
        if (ClipboardShareDevices.Count == 0)
            return;

        var selected = data.ClipboardShareTargets
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _updatingClipboardSelection = true;
        foreach (var device in ClipboardShareDevices)
            device.IsSelected = !data.ClipboardUseTargetSelection || selected.Contains(device.IpAddress);
        _updatingClipboardSelection = false;
    }

    private void OnClipboardShareDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updatingClipboardSelection)
            return;

        if (e.PropertyName == nameof(ClipboardShareDevice.IsSelected))
            StatusText = "Clipboard target selection changed — click Save to apply";
    }
}
