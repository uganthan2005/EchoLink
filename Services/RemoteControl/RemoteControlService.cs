using System;
using System.Threading;
using System.Threading.Tasks;
using EchoLink.Models;
using EchoLink.ViewModels;
using EchoLink.Services.UnifiedProtocol;
using Renci.SshNet;
using EchoLink.Services.RemoteControl;

namespace EchoLink.Services;

public class RemoteControlService
{
    private static RemoteControlService? _instance;
    public static RemoteControlService Instance => _instance ??= new RemoteControlService();

    private readonly LoggingService _log = LoggingService.Instance;
    private DesktopKeyboardSink? _keyboardSink;

    public void StartServer()
    {
        // Legacy port listener removed. The server is now handled entirely by UnifiedProtocolService.
    }

    public void StopServer()
    {
        // Legacy port listener removed. 
    }

    // Client side
    public async Task<bool> ConnectToTargetAsync(Device targetDevice, string pkeyPath, CancellationToken ct)
    {
        if (UnifiedProtocolClient.Instance.IsConnected)
        {
            _log.Info($"RemoteControl using existing Unified connection to {targetDevice.IpAddress}");
            return true;
        }

        return await UnifiedProtocolClient.Instance.ConnectAsync(targetDevice.IpAddress, pkeyPath, ct);
    }

    public void Disconnect()
    {
        // Don't forcefully disconnect the unified client if other services might be using it.
    }

    public async Task SendMoveAsync(double dx, double dy)
    {
        if (!UnifiedProtocolClient.Instance.IsConnected)
        {
            // Auto-reconnect if we have a target
            var target = RemoteControlViewModel.Instance?.SelectedTarget;
            if (target != null)
            {
                string pkeyPath = new SshPairingService(TailscaleService.Instance).PrivateKeyPath;
                await ConnectToTargetAsync(target, pkeyPath, CancellationToken.None);
            }
        }

        if (UnifiedProtocolClient.Instance.IsConnected)
        {
            try
            {
                // Multiplier for sensitivity (reduced from 2.5 to 1.2 for better control)
                await UnifiedProtocolClient.Instance.SendMouseMoveAsync((short)(dx * 1.2), (short)(dy * 1.2), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Warning($"RemoteControl move failed: {ex.Message}");
                UnifiedProtocolClient.Instance.Disconnect();
            }
        }
    }

    public async Task SendClickAsync(byte button, byte state)
    {
        if (UnifiedProtocolClient.Instance.IsConnected)
        {
            try
            {
                await UnifiedProtocolClient.Instance.SendMouseClickAsync(button, state, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Warning($"RemoteControl click failed: {ex.Message}");
                UnifiedProtocolClient.Instance.Disconnect();
            }
        }
    }

    public async Task SendCommandAsync(Device target, string action)
    {
        // 1. Primary path: SSH (No app required on target side, but needs SSH server)
        try
        {
            string sshCommand = action switch
            {
                "Lock" => "loginctl lock-sessions",
                "Restart" => "sudo systemctl reboot",
                "Shutdown" => "sudo systemctl poweroff",
                _ => throw new ArgumentException($"Unknown action: {action}")
            };

            _log.Info($"[RemoteControl] Attempting SSH command for {action}...");
            await ExecuteSshCommandAsync(target, sshCommand);
            return; // Success
        }
        catch (Exception ex)
        {
            _log.Warning($"[RemoteControl] SSH command failed, falling back to TCP Bridge: {ex.Message}");
        }

        // 2. Fallback path: TCP Bridge (Requires EchoLink app running on target)
        if (UnifiedProtocolClient.Instance.IsConnected)
        {
            try
            {
                byte actionId = action switch
                {
                    "Lock" => 0,
                    "Restart" => 1,
                    "Shutdown" => 2,
                    _ => 255
                };

                if (actionId != 255)
                {
                    await UnifiedProtocolClient.Instance.SendSystemActionAsync(actionId, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"[RemoteControl] TCP Bridge send failed: {ex.Message}");
            }
        }
        else
        {
            _log.Error("[RemoteControl] Both SSH and TCP Bridge failed. Ensure target is online and paired.");
        }
    }

    public async Task ExecuteSshCommandAsync(Device target, string command)
    {
        await Task.Run(() =>
        {
            var settings = SettingsService.Instance.Load();
            if (!settings.PeerUsernames.TryGetValue(target.IpAddress, out string? username))
            {
                username = "echolink-mesh";
            }

            string pkeyPath = new SshPairingService(TailscaleService.Instance).PrivateKeyPath;
            var privateKeyFile = new PrivateKeyFile(pkeyPath);

            // Connect to peer via Tailscale SOCKS5 proxy
            // Note: Port 1055 is the internal Tailscale proxy port used by EchoLink
            var connectionInfo = new ConnectionInfo(
                target.IpAddress,
                22,
                username,
                ProxyTypes.Socks5,
                "127.0.0.1",
                1055, 
                "",
                "",
                new PrivateKeyAuthenticationMethod(username, privateKeyFile));

            using var client = new SshClient(connectionInfo);
            client.Connect();
            using var sshCmd = client.CreateCommand(command);
            sshCmd.Execute();
            _log.Info($"[RemoteControl] SSH Command '{command}' executed on {target.IpAddress}");
        });
    }

    // === Unified Protocol Integration ===

    /// <summary>
    /// Initialize unified protocol handlers.
    /// Call this once at application startup.
    /// </summary>
    public void InitializeUnifiedProtocol()
    {
        MouseControlService.Instance.Initialize();

        // Keyboard sink is only for desktop platforms
        if (!OperatingSystem.IsAndroid())
        {
            _keyboardSink = new DesktopKeyboardSink();
            UnifiedProtocolService.Instance.RegisterHandler(
                UnifiedMessageType.KeyboardEvent,
                async (payload, reply, ct) =>
                {
                    if (_keyboardSink != null)
                        await _keyboardSink.HandleKeyboardEventAsync(payload);
                });
        }

        UnifiedProtocolService.Instance.RegisterHandler(
            UnifiedMessageType.MouseMove,
            async (payload, reply, ct) => await MouseControlService.Instance.HandleMouseMoveAsync(payload, ct));
        
        UnifiedProtocolService.Instance.RegisterHandler(
            UnifiedMessageType.MouseClick,
            async (payload, reply, ct) => await MouseControlService.Instance.HandleMouseClickAsync(payload, ct));
        
        UnifiedProtocolService.Instance.RegisterHandler(
            UnifiedMessageType.SystemAction,
            async (payload, reply, ct) => await SystemControlService.Instance.HandleSystemActionAsync(payload, ct));
        
        _log.Info("[RemoteControl] Unified protocol handlers registered");
    }
}
