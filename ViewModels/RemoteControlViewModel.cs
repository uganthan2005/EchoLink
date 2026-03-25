using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;
using EchoLink.Services.UnifiedProtocol;

namespace EchoLink.ViewModels;

public partial class RemoteControlViewModel : ViewModelBase
{
    private static RemoteControlViewModel? _instance;
    public static RemoteControlViewModel Instance => _instance ??= new RemoteControlViewModel();

    private readonly LoggingService _log = LoggingService.Instance;

    [ObservableProperty] private Device? _selectedTarget;
    public ObservableCollection<Device> OnlineDevices { get; } = new();

    // Trackpad state
    [ObservableProperty] private double _pointerX;
    [ObservableProperty] private double _pointerY;
    [ObservableProperty] private string _trackpadStatus = "Trackpad ready";
    [ObservableProperty] private string _audioStatus = "Audio idle";
    [ObservableProperty] private bool _isAudioStreaming;

    [ObservableProperty] private bool _isLeftButtonPressed;
    [ObservableProperty] private bool _isRightButtonPressed;

    private double _lastX;
    private double _lastY;
    private bool   _isDragging;
    private bool   _hasMovedSignificant;
    private DateTime _lastMoveTime;
    private DateTime _lastClickTime;

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
        await AudioStreamingService.Instance.StopAllAsync();
        IsAudioStreaming = false;
        AudioStatus = "Audio idle";

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

    [RelayCommand]
    private async Task StartAudioAsync()
    {
        if (SelectedTarget == null)
        {
            AudioStatus = "Select a target device first";
            return;
        }

        try
        {
            await AudioStreamingService.Instance.StopAllAsync();

            string pkeyPath = new SshPairingService(TailscaleService.Instance).PrivateKeyPath;
            bool sendOk;

            if (OperatingSystem.IsAndroid())
            {
                // Send Android mic via low-latency SSH tunnel (bypasses Windows PC UDP limits)
                sendOk = await AudioStreamingService.Instance.StartMicrophoneSendAsync(SelectedTarget, pkeyPath);

                IsAudioStreaming = sendOk;
                AudioStatus = sendOk
                    ? "Mic + playback active"
                    : "Audio start failed";
            }
            else
            {
                // Desktop: audio receive is handled by TCP server (already running).
                // Just start sending system audio via SSH tunnel.
                sendOk = await AudioStreamingService.Instance.StartLoopbackSendAsync(SelectedTarget, pkeyPath);

                IsAudioStreaming = sendOk;
                AudioStatus = sendOk
                    ? "System audio streaming active"
                    : "Audio start failed";
            }
        }
        catch (Exception ex)
        {
            IsAudioStreaming = false;
            AudioStatus = $"Audio error: {ex.Message}";
            _log.Error($"[RemoteControl] Audio start failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StopAudioAsync()
    {
        await AudioStreamingService.Instance.StopAllAsync();
        IsAudioStreaming = false;
        AudioStatus = "Audio stopped";
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
            await RemoteControlService.Instance.SendCommandAsync(SelectedTarget, action);
        }
    }

    // ── Trackpad ──────────────────────────────────────────────────────────────

    public void OnPointerPressed(double x, double y)
    {
        _isDragging  = true;
        _lastX       = x;
        _lastY       = y;
        _hasMovedSignificant = false;
        TrackpadStatus = "Pointer pressed";
    }

    public void OnPointerMoved(double x, double y)
    {
        if (!_isDragging) return;

        double rawDeltaX = x - _lastX;
        double rawDeltaY = y - _lastY;
        
        // Simple smoothing (Low-pass filter) to reduce jitter
        // 70% current movement + 30% previous frame context
        double deltaX = (rawDeltaX * 0.7);
        double deltaY = (rawDeltaY * 0.7);

        if (Math.Abs(rawDeltaX) > 4 || Math.Abs(rawDeltaY) > 4)
        {
            _hasMovedSignificant = true;
        }

        _lastX = x;
        _lastY = y;

        if (SelectedTarget != null)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastMoveTime).TotalMilliseconds >= 25) // ~40Hz throttle for smoother network flow
            {
                _lastMoveTime = now;
                TrackpadStatus = "Moving...";
                _ = RemoteControlService.Instance.SendMoveAsync(deltaX, deltaY);
            }
        }
    }

    public async Task SetMouseButtonState(byte button, bool isDown)
    {
        if (button == 0) IsLeftButtonPressed = isDown;
        else if (button == 1) IsRightButtonPressed = isDown;

        if (SelectedTarget != null)
        {
            await RemoteControlService.Instance.SendClickAsync(button, (byte)(isDown ? 1 : 0));
        }
    }

    public void OnPointerReleased()
    {
        if (!_isDragging) return;
        _isDragging = false;
        
        if (!_hasMovedSignificant && SelectedTarget != null)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastClickTime).TotalMilliseconds < 450)
            {
                // Double Tap = Double Click
                _ = SendDoubleClickAsync(0);
                _lastClickTime = DateTime.MinValue; // Prevent triple click
            }
            else
            {
                // Simple tap = Left Click
                _ = SendClickAsync(0);
                _lastClickTime = now;
            }
        }

        TrackpadStatus = SelectedTarget != null ? "Connected" : "Disconnected";
    }

    private async Task SendDoubleClickAsync(byte button)
    {
        await SendClickAsync(button);
        await Task.Delay(50);
        await SendClickAsync(button);
    }

    private async Task SendClickAsync(byte button)
    {
        // button: 0=left, 1=right
        await RemoteControlService.Instance.SendClickAsync(button, 1); // Down
        await Task.Delay(20);
        await RemoteControlService.Instance.SendClickAsync(button, 0); // Up
    }
}
