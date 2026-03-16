using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;
using Renci.SshNet;

namespace EchoLink.ViewModels;

public partial class RemoteControlViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;

    [ObservableProperty] private Device? _selectedTarget;
    [ObservableProperty] private bool _isBusy;
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
    private async Task LockScreenAsync() => await ExecuteActionAsync("Lock");

    [RelayCommand]
    private async Task RestartAsync() => await ExecuteActionAsync("Restart");

    [RelayCommand]
    private async Task ShutdownAsync() => await ExecuteActionAsync("Shutdown");

    private async Task ExecuteActionAsync(string action)
    {
        if (SelectedTarget == null || IsBusy) return;

        string targetIp = SelectedTarget.IpAddress;
        _log.Info($"[RC] Sending command: {action} to {targetIp}");

        // Determine the SSH command
        string sshCommand = action switch
        {
            "Lock"     => "loginctl lock-sessions",
            "Restart"  => "sudo systemctl reboot",
            "Shutdown" => "sudo systemctl poweroff",
            _          => throw new ArgumentException($"Invalid action: {action}")
        };

        IsBusy = true;
        try
        {
            // 1. Try SSH first (Best for system level actions)
            var settings = SettingsService.Instance.Load();
            if (settings.PeerUsernames.TryGetValue(targetIp, out var username))
            {
                try
                {
                    string pKeyPath = new SshPairingService(TailscaleService.Instance).PrivateKeyPath;
                    await ExecuteSshCommandAsync(targetIp, username, pKeyPath, sshCommand);
                    return; // Success
                }
                catch (Exception sshEx)
                {
                    _log.Warning($"[RC] SSH failed: {sshEx.Message}. Falling back to TCP bridge...");
                }
            }
            else
            {
                 _log.Debug($"[RC] Peer {targetIp} not paired for SSH. Using TCP fallback.");
            }

            // 2. Fallback to the custom TCP bridge (Requires app running on target)
            await RemoteControlService.Instance.SendCommandAsync(action);
        }
        catch (Exception ex)
        {
            _log.Error($"[RC] Command execution failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteSshCommandAsync(string ip, string username, string pKeyPath, string command)
    {
        await Task.Run(() =>
        {
            try
            {
                var privateKeyFile = new PrivateKeyFile(pKeyPath);
                
                ConnectionInfo connectionInfo;
                bool isAndroid = RuntimeInformation.IsOSPlatform(OSPlatform.Create("ANDROID"));

                if (isAndroid)
                {
                    // Use Tailscale userspace proxy (Port 1055)
                    connectionInfo = new ConnectionInfo(
                        ip, 22, username,
                        ProxyTypes.Socks5, "127.0.0.1", 1055, "", "",
                        new PrivateKeyAuthenticationMethod(username, privateKeyFile));
                }
                else
                {
                    connectionInfo = new ConnectionInfo(
                        ip, 22, username,
                        new PrivateKeyAuthenticationMethod(username, privateKeyFile));
                }

                using var client = new SshClient(connectionInfo);
                client.Connect();
                
                using var cmd = client.CreateCommand(command);
                cmd.Execute();
                _log.Info($"[RC] SSH Command '{command}' executed successfully.");
                
                client.Disconnect();
            }
            catch (Exception ex)
            {
                throw new Exception($"SSH.NET failure: {ex.Message}");
            }
        });
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
