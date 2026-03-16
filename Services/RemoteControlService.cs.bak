using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EchoLink.Models;
using Renci.SshNet;

namespace EchoLink.Services;

public class RemoteControlService
{
    private static RemoteControlService? _instance;
    public static RemoteControlService Instance => _instance ??= new RemoteControlService();

    private readonly LoggingService _log = LoggingService.Instance;
    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;

    private const int RcTunnelPort = 44556;

    // Desktop: Server listening on 127.0.0.1:44556
    public void StartServer()
    {
        if (_serverCts != null) return;
        _serverCts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, RcTunnelPort);
        
        try
        {
            _listener.Start();
            _log.Info($"RemoteControlService Server started on 127.0.0.1:{RcTunnelPort}");

            _ = Task.Run(async () =>
            {
                while (!_serverCts.IsCancellationRequested)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync(_serverCts.Token);
                        _ = HandleClientAsync(client, _serverCts.Token);
                    }
                    catch { }
                }
            });
        }
        catch (Exception ex)
        {
            _log.Error($"RemoteControlService failed to start: {ex.Message}");
        }
    }

    public void StopServer()
    {
        if (_serverCts == null) return;
        _serverCts.Cancel();
        _serverCts.Dispose();
        _serverCts = null;
        _listener?.Stop();
        _listener = null;
        _log.Info("RemoteControlService Server stopped");
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        
        while (!ct.IsCancellationRequested && client.Connected)
        {
            try
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;

                var evt = JsonSerializer.Deserialize<RemoteEvent>(line);
                if (evt != null)
                {
                    ProcessEvent(evt);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.Warning($"RemoteControl handle error: {ex.Message}");
                break;
            }
        }
    }

    private void ProcessEvent(RemoteEvent evt)
    {
        if (evt.Type == "Move")
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // MOUSEEVENTF_MOVE = 0x0001
                mouse_event(0x0001, (int)evt.DeltaX, (int)evt.DeltaY, 0, 0);
            }
        }
        else if (evt.Type == "Lock")
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                LockWorkStation();
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    // Client side (Android)
    private StreamWriter? _clientWriter;
    private Stream? _currentStream;

    public async Task<bool> ConnectToTargetAsync(Device targetDevice, string pkeyPath, CancellationToken ct)
    {
        Disconnect(); // Clean up previous connection

        var settings = SettingsService.Instance.Load();
        if (!settings.PeerUsernames.TryGetValue(targetDevice.IpAddress, out var username) || string.IsNullOrEmpty(username))
        {
            _log.Error($"Cannot connect to RemoteControl. Unpaired device: {targetDevice.IpAddress}");
            return false;
        }

        int sshPort = 22;
        if (targetDevice.Os?.Contains("android", StringComparison.OrdinalIgnoreCase) == true ||
            targetDevice.Name?.Contains("android", StringComparison.OrdinalIgnoreCase) == true)
        {
            sshPort = 2222;
        }

        try
        {
            _currentStream = await SshTunnelService.Instance.CreateTunneledStreamAsync(
                targetDevice.IpAddress, username, pkeyPath, RcTunnelPort, sshPort, ct);

            _clientWriter = new StreamWriter(_currentStream, Encoding.UTF8) { AutoFlush = true };
            _log.Info($"RemoteControl connected to {targetDevice.IpAddress}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to connect RemoteControl SSH Tunnel: {ex.Message}");
            return false;
        }
    }

    public void Disconnect()
    {
        _clientWriter?.Dispose();
        _clientWriter = null;
        _currentStream?.Dispose();
        _currentStream = null;
    }

    public async Task SendMoveAsync(double dx, double dy)
    {
        if (_clientWriter != null)
        {
            try
            {
                // Multiplier for sensitivity
                var evt = new RemoteEvent { Type = "Move", DeltaX = dx * 2.5, DeltaY = dy * 2.5 };
                await _clientWriter.WriteLineAsync(JsonSerializer.Serialize(evt));
            }
            catch (Exception ex)
            {
                _log.Warning($"RemoteControl send failed: {ex.Message}");
                Disconnect();
            }
        }
    }

    public async Task SendCommandAsync(string cmd)
    {
        if (_clientWriter != null)
        {
            try
            {
                var evt = new RemoteEvent { Type = cmd };
                await _clientWriter.WriteLineAsync(JsonSerializer.Serialize(evt));
            }
            catch { Disconnect(); }
        }
    }

    public class RemoteEvent
    {
        public string Type { get; set; } = "";
        public double DeltaX { get; set; }
        public double DeltaY { get; set; }
    }
}
