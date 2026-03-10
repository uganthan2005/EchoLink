using System.Collections.ObjectModel;
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

    public ObservableCollection<ClipboardEntry> History { get; } =
    [
        new ClipboardEntry("Hello from EchoLink!",           "Gautam-Desktop", DateTime.Now.AddMinutes(-2)),
        new ClipboardEntry("https://github.com/fosshack2026", "Gautam-Phone",   DateTime.Now.AddMinutes(-8)),
        new ClipboardEntry("Meeting at 3 PM — don't forget!", "Gautam-Laptop",  DateTime.Now.AddMinutes(-45)),
    ];

    public ClipboardViewModel()
    {
        RefreshFromSettings();
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
    }

    private void TrimHistory()
    {
        while (History.Count > HistoryLimit)
            History.RemoveAt(History.Count - 1);
    }

    partial void OnIsAutoSyncEnabledChanged(bool value)
    {
        StatusText = value ? "MirrorClip active — syncing" : "MirrorClip paused";
        _log.Info($"Clipboard MirrorClip {(value ? "enabled" : "disabled")}.");
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

        _log.Info("EchoShot — Pushing current clipboard to peers...");
        StatusText = "Broadcasting...";
        await _clipboardSync.PushCurrentClipboardAsync();
        StatusText = "SnapShare — Broadcast complete.";
        _log.Info("Clipboard pushed via SnapShare.");
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
