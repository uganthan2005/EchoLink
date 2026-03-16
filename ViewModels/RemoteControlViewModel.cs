using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class RemoteControlViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;

    [ObservableProperty] private Device? _selectedTarget;
    public ObservableCollection<Device> OnlineDevices { get; } = new();

    // Trackpad state
    [ObservableProperty] private double _pointerX;
    [ObservableProperty] private double _pointerY;
    [ObservableProperty] private string _trackpadStatus = "Trackpad ready";

    private double _lastX;
    private double _lastY;
    private bool   _isDragging;
    private DateTime _lastMoveTime = DateTime.MinValue;
    private DateTime _pressTime;
    private bool _hasMovedSincePress;
    private int _activePointers = 0;

    public RemoteControlViewModel()
    {
        _ = LoadDevicesAsync();
    }

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        try
        {
            var (_, devices) = await TailscaleService.Instance.GetNetworkStatusAsync();
            OnlineDevices.Clear();
            foreach (var device in devices)
            {
                if (device.IsOnline && !device.IsSelf)
                {
                    OnlineDevices.Add(device);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[RemoteControl] Failed to load devices: {ex.Message}");
        }
    }

    partial void OnSelectedTargetChanged(Device? value)
    {
        _ = ConnectToTargetAsync(value);
    }

    private async Task ConnectToTargetAsync(Device? target)
    {
        if (target == null)
        {
            RemoteControlService.Instance.Disconnect();
            TrackpadStatus = "Disconnected";
            return;
        }

        TrackpadStatus = "Connecting...";
        string pkeyPath = new SshPairingService(TailscaleService.Instance).PrivateKeyPath;
        bool success = await RemoteControlService.Instance.ConnectToTargetAsync(target, pkeyPath, CancellationToken.None);
        
        TrackpadStatus = success ? "Connected" : "Failed to connect";
    }

    // ── Quick Actions ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LockScreenAsync() => await SendCommandAsync("Lock");

    [RelayCommand]
    private async Task RestartAsync() => await SendCommandAsync("Restart");

    [RelayCommand]
    private async Task ShutdownAsync() => await SendCommandAsync("Shutdown");

    private async Task SendCommandAsync(string action)
    {
        _log.Info($"Sending RC command: {action}");
        if (SelectedTarget != null)
        {
            await RemoteControlService.Instance.SendCommandAsync(action);
        }
    }

    // ── Trackpad ──────────────────────────────────────────────────────────────

    public void OnPointerPressed(double x, double y, int pointerId)
    {
        _activePointers++;
        if (_activePointers == 1)
        {
            _isDragging  = true;
            _hasMovedSincePress = false;
            _pressTime = DateTime.UtcNow;
            _lastX       = x;
            _lastY       = y;
            TrackpadStatus = "Pointer pressed";
        }
    }

    public void OnPointerMoved(double x, double y, int pointerId)
    {
        if (!_isDragging) return;

        double deltaX = x - _lastX;
        double deltaY = y - _lastY;
        
        if (Math.Abs(deltaX) > 1 || Math.Abs(deltaY) > 1)
        {
            _hasMovedSincePress = true;
        }

        _lastX = x;
        _lastY = y;

        PointerX = x;
        PointerY = y;

        TrackpadStatus = $"Δ({deltaX:+0.0;-0.0}, {deltaY:+0.0;-0.0})";

        if (SelectedTarget != null)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastMoveTime).TotalMilliseconds >= 15)
            {
                _lastMoveTime = now;
                _ = RemoteControlService.Instance.SendMoveAsync(deltaX, deltaY);
            }
        }
    }

    public void OnPointerReleased(int pointerId)
    {
        if (_isDragging && !_hasMovedSincePress && (DateTime.UtcNow - _pressTime).TotalMilliseconds < 300)
        {
            // It's a tap
            if (_activePointers == 1)
            {
                // Left click
                _ = SendClickAsync(0);
            }
            else if (_activePointers == 2)
            {
                // Right click
                _ = SendClickAsync(1);
            }
        }

        _activePointers--;
        if (_activePointers <= 0)
        {
            _activePointers = 0;
            _isDragging    = false;
            TrackpadStatus = SelectedTarget != null ? "Connected" : "Disconnected";
        }
    }

    private async Task SendClickAsync(int button)
    {
        if (SelectedTarget != null)
        {
            await RemoteControlService.Instance.SendClickAsync(button, 1); // Press
            await Task.Delay(20);
            await RemoteControlService.Instance.SendClickAsync(button, 0); // Release
        }
    }
}
