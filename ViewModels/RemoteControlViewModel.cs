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
            "Restart"  => "systemctl reboot",
            "Shutdown" => "systemctl poweroff",
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

    public void OnKeyDown(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers)
    {
        if (SelectedTarget == null) return;

        ushort keyCode = MapToPlatformKey(key, SelectedTarget.Os);
        if (keyCode == 0)
        {
            _log.Debug($"[RC] Unmapped key: {key}");
            return;
        }

        _log.Debug($"[RC] Sending key: {key} (code={keyCode}) to {SelectedTarget.Name}");

        bool isShift = modifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);
        ushort shiftKey = MapToPlatformKey(Avalonia.Input.Key.LeftShift, SelectedTarget.Os);

        _ = Task.Run(async () =>
        {
            if (isShift && shiftKey != 0) await RemoteControlService.Instance.SendKeyAsync(shiftKey, 1);
            
            await RemoteControlService.Instance.SendKeyAsync(keyCode, 1); // Press
            await Task.Delay(20);
            await RemoteControlService.Instance.SendKeyAsync(keyCode, 0); // Release

            if (isShift && shiftKey != 0) await RemoteControlService.Instance.SendKeyAsync(shiftKey, 0);
        });
    }

    private ushort MapToPlatformKey(Avalonia.Input.Key key, string os)
    {
        bool isWindows = os?.Contains("Windows", StringComparison.OrdinalIgnoreCase) ?? false;

        if (isWindows)
        {
            return key switch
            {
                Avalonia.Input.Key.A => 0x41, Avalonia.Input.Key.B => 0x42, Avalonia.Input.Key.C => 0x43,
                Avalonia.Input.Key.D => 0x44, Avalonia.Input.Key.E => 0x45, Avalonia.Input.Key.F => 0x46,
                Avalonia.Input.Key.G => 0x47, Avalonia.Input.Key.H => 0x48, Avalonia.Input.Key.I => 0x49,
                Avalonia.Input.Key.J => 0x4A, Avalonia.Input.Key.K => 0x4B, Avalonia.Input.Key.L => 0x4C,
                Avalonia.Input.Key.M => 0x4D, Avalonia.Input.Key.N => 0x4E, Avalonia.Input.Key.O => 0x4F,
                Avalonia.Input.Key.P => 0x50, Avalonia.Input.Key.Q => 0x51, Avalonia.Input.Key.R => 0x52,
                Avalonia.Input.Key.S => 0x53, Avalonia.Input.Key.T => 0x54, Avalonia.Input.Key.U => 0x55,
                Avalonia.Input.Key.V => 0x56, Avalonia.Input.Key.W => 0x57, Avalonia.Input.Key.X => 0x58,
                Avalonia.Input.Key.Y => 0x59, Avalonia.Input.Key.Z => 0x5A,
                Avalonia.Input.Key.D0 => 0x30, Avalonia.Input.Key.D1 => 0x31, Avalonia.Input.Key.D2 => 0x32,
                Avalonia.Input.Key.D3 => 0x33, Avalonia.Input.Key.D4 => 0x34, Avalonia.Input.Key.D5 => 0x35,
                Avalonia.Input.Key.D6 => 0x36, Avalonia.Input.Key.D7 => 0x37, Avalonia.Input.Key.D8 => 0x38,
                Avalonia.Input.Key.D9 => 0x39,
                Avalonia.Input.Key.LeftShift => 0x10, Avalonia.Input.Key.RightShift => 0x10,
                Avalonia.Input.Key.LeftCtrl => 0x11, Avalonia.Input.Key.RightCtrl => 0x11,
                Avalonia.Input.Key.LeftAlt => 0x12, Avalonia.Input.Key.RightAlt => 0x12,
                Avalonia.Input.Key.Enter => 0x0D, Avalonia.Input.Key.Back => 0x08, Avalonia.Input.Key.Tab => 0x09,
                Avalonia.Input.Key.Space => 0x20, Avalonia.Input.Key.Escape => 0x1B,
                Avalonia.Input.Key.OemComma => 0xBC, Avalonia.Input.Key.OemPeriod => 0xBE,
                Avalonia.Input.Key.OemSemicolon => 0xBA, Avalonia.Input.Key.OemQuotes => 0xDE,
                Avalonia.Input.Key.OemOpenBrackets => 0xDB, Avalonia.Input.Key.OemCloseBrackets => 0xDD,
                Avalonia.Input.Key.OemPipe => 0xDC, Avalonia.Input.Key.OemMinus => 0xBD,
                Avalonia.Input.Key.OemPlus => 0xBB, Avalonia.Input.Key.OemQuestion => 0xBF,
                _ => 0
            };
        }
        else // Linux (uinput keycodes)
        {
            return key switch
            {
                Avalonia.Input.Key.A => 30, Avalonia.Input.Key.B => 48, Avalonia.Input.Key.C => 46,
                Avalonia.Input.Key.D => 32, Avalonia.Input.Key.E => 18, Avalonia.Input.Key.F => 33,
                Avalonia.Input.Key.G => 34, Avalonia.Input.Key.H => 35, Avalonia.Input.Key.I => 23,
                Avalonia.Input.Key.J => 36, Avalonia.Input.Key.K => 37, Avalonia.Input.Key.L => 38,
                Avalonia.Input.Key.M => 50, Avalonia.Input.Key.N => 49, Avalonia.Input.Key.O => 24,
                Avalonia.Input.Key.P => 25, Avalonia.Input.Key.Q => 16, Avalonia.Input.Key.R => 19,
                Avalonia.Input.Key.S => 31, Avalonia.Input.Key.T => 20, Avalonia.Input.Key.U => 22,
                Avalonia.Input.Key.V => 47, Avalonia.Input.Key.W => 17, Avalonia.Input.Key.X => 45,
                Avalonia.Input.Key.Y => 21, Avalonia.Input.Key.Z => 44,
                Avalonia.Input.Key.D0 => 11, Avalonia.Input.Key.D1 => 2, Avalonia.Input.Key.D2 => 3,
                Avalonia.Input.Key.D3 => 4, Avalonia.Input.Key.D4 => 5, Avalonia.Input.Key.D5 => 6,
                Avalonia.Input.Key.D6 => 7, Avalonia.Input.Key.D7 => 8, Avalonia.Input.Key.D8 => 9,
                Avalonia.Input.Key.D9 => 10,
                Avalonia.Input.Key.LeftShift => 42, Avalonia.Input.Key.RightShift => 54,
                Avalonia.Input.Key.LeftCtrl => 29, Avalonia.Input.Key.RightCtrl => 97,
                Avalonia.Input.Key.LeftAlt => 56, Avalonia.Input.Key.RightAlt => 100,
                Avalonia.Input.Key.Enter => 28, Avalonia.Input.Key.Back => 14, Avalonia.Input.Key.Tab => 15,
                Avalonia.Input.Key.Space => 57, Avalonia.Input.Key.Escape => 1,
                Avalonia.Input.Key.OemComma => 51, Avalonia.Input.Key.OemPeriod => 52,
                Avalonia.Input.Key.OemSemicolon => 39, Avalonia.Input.Key.OemQuotes => 40,
                Avalonia.Input.Key.OemOpenBrackets => 26, Avalonia.Input.Key.OemCloseBrackets => 27,
                Avalonia.Input.Key.OemPipe => 43, Avalonia.Input.Key.OemMinus => 12,
                Avalonia.Input.Key.OemPlus => 13, Avalonia.Input.Key.OemQuestion => 53,
                _ => 0
            };
        }
    }
}
