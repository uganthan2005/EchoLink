using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using EchoLink.Models;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class ClipboardViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly ClipboardSyncService _clipboardSync = ClipboardSyncService.Instance;

    [ObservableProperty] private bool _isAutoSyncEnabled;
    [ObservableProperty] private string _statusText = "Idle";
    [ObservableProperty] private bool _isMirrorClipActive;
    [ObservableProperty] private bool _isGhostPasteActive;
    [ObservableProperty] private bool _isSnapShareActive;
    [ObservableProperty] private int _historyLimit = 50;
    [ObservableProperty] private bool _isLoadingShareDevices;

    private bool _updatingDeviceSelection;

    public ObservableCollection<ClipboardEntry> History { get; } = new();
    public ObservableCollection<ClipboardShareDevice> ShareDevices { get; } = new();

    public ClipboardViewModel()
    {
        RefreshFromSettings();
        _ = RefreshShareDevicesAsync();
    }

    /// <summary>
    /// Reload EchoBoard settings so the clipboard view reflects current config.
    /// </summary>
    public void RefreshFromSettings()
    {
        var data = _settings.Load();
        IsMirrorClipActive = data.MirrorClipEnabled;
        IsGhostPasteActive = data.GhostPasteEnabled;
        IsSnapShareActive  = data.SnapShareEnabled;
        HistoryLimit       = data.ClipboardHistoryLimit;

        IsAutoSyncEnabled = IsMirrorClipActive;
        TrimHistory();
        ApplySelectionFromSettings(data);
    }

    private void TrimHistory()
    {
        while (History.Count > HistoryLimit)
            History.RemoveAt(History.Count - 1);
    }

    [RelayCommand]
    private async Task RefreshShareDevicesAsync()
    {
        if (IsLoadingShareDevices)
            return;

        IsLoadingShareDevices = true;

        try
        {
            var settings = _settings.Load();
            var (_, devices) = await TailscaleService.Instance.GetNetworkStatusAsync();

            foreach (var existing in ShareDevices)
                existing.PropertyChanged -= OnShareDevicePropertyChanged;

            ShareDevices.Clear();

            var selected = settings.ClipboardShareTargets
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool useTargetSelection = settings.ClipboardUseTargetSelection;

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

                item.PropertyChanged += OnShareDevicePropertyChanged;
                ShareDevices.Add(item);
            }

            if (ShareDevices.Count == 0)
                StatusText = "No peer devices available for clipboard share";
        }
        catch (Exception ex)
        {
            _log.Warning($"Clipboard share device refresh failed: {ex.Message}");
        }
        finally
        {
            IsLoadingShareDevices = false;
        }
    }

    [RelayCommand]
    private async Task SelectAllShareDevicesAsync()
    {
        _updatingDeviceSelection = true;
        foreach (var device in ShareDevices)
            device.IsSelected = true;
        _updatingDeviceSelection = false;

        await SaveSelectedTargetsAsync();
    }

    [RelayCommand]
    private async Task SelectNoneShareDevicesAsync()
    {
        _updatingDeviceSelection = true;
        foreach (var device in ShareDevices)
            device.IsSelected = false;
        _updatingDeviceSelection = false;

        await SaveSelectedTargetsAsync();
    }

    partial void OnIsAutoSyncEnabledChanged(bool value)
    {
        StatusText = value ? "MirrorClip active — syncing" : "MirrorClip paused";
        _log.Info($"Clipboard MirrorClip {(value ? "enabled" : "disabled")}.");

        // Persist the toggle so the background monitor loop picks it up
        var data = _settings.Load();
        data.MirrorClipEnabled = value;
        _settings.Save(data);
        IsMirrorClipActive = value;
    }

    [RelayCommand]
    private async Task PushClipboardAsync()
    {
        if (!IsSnapShareActive)
        {
            StatusText = "SnapShare is disabled — enable it in Settings";
            _log.Warning("Push blocked: SnapShare disabled.");
            return;
        }

        var app = Avalonia.Application.Current;
        if (app?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt
            && dt.MainWindow is { } window)
        {
            var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(window)?.Clipboard;
            if (clipboard is not null)
            {
                var text = await clipboard.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    StatusText = "Clipboard is empty";
                    return;
                }
            }
        }

        _log.Info("SnapShare — Pushing current clipboard to peers...");
        StatusText = "Broadcasting...";
        await _clipboardSync.PushCurrentClipboardAsync();
        StatusText = "SnapShare — Broadcast complete.";
        _log.Info("Clipboard pushed via SnapShare.");
    }

    private async void OnShareDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updatingDeviceSelection)
            return;

        if (e.PropertyName == nameof(ClipboardShareDevice.IsSelected))
            await SaveSelectedTargetsAsync();
    }

    private async Task SaveSelectedTargetsAsync()
    {
        var selectedIps = ShareDevices
            .Where(d => d.IsSelected)
            .Select(d => d.IpAddress)
            .ToList();

        await _clipboardSync.UpdateClipboardShareTargetsAsync(selectedIps);

        StatusText = selectedIps.Count == 0
            ? "Clipboard share targets cleared"
            : $"Clipboard sharing set to {selectedIps.Count} device(s)";
    }

    private void ApplySelectionFromSettings(SettingsData settings)
    {
        if (ShareDevices.Count == 0)
            return;

        var selected = settings.ClipboardShareTargets
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool useTargetSelection = settings.ClipboardUseTargetSelection;

        _updatingDeviceSelection = true;
        foreach (var device in ShareDevices)
            device.IsSelected = !useTargetSelection || selected.Contains(device.IpAddress);
        _updatingDeviceSelection = false;
    }

    [RelayCommand]
    private async Task CopyEntryAsync(ClipboardEntry? entry)
    {
        if (entry is null) return;

        var app = Avalonia.Application.Current;
        if (app?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt
            && dt.MainWindow is { } window)
        {
            var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(window)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(entry.Content);
        }

        StatusText = "Copied to clipboard!";
        _log.Info($"Copied entry from {entry.SourceDevice}.");
    }

    [RelayCommand]
    private void ClearHistory()
    {
        History.Clear();
        StatusText = "History cleared.";
        _log.Info("Clipboard history cleared.");
    }

    public void OnRemoteClipboardReceived(ClipboardEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            History.Insert(0, entry);
            TrimHistory();
            StatusText = $"MirrorClip received from {entry.SourceDevice}";
        });
    }
}
