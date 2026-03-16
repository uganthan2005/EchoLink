using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using EchoLink.Models;

namespace EchoLink.Services;

public class RemoteControlService
{
    private static RemoteControlService? _instance;
    public static RemoteControlService Instance => _instance ??= new RemoteControlService();

    private readonly LoggingService _log = LoggingService.Instance;
    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;

    private const int RcDirectPort = 55555;

    // P/Invoke for Linux uinput (from libecholink.so)
    [DllImport("echolink", CallingConvention = CallingConvention.Cdecl)]
    private static extern int InitializeVirtualMouse();

    [DllImport("echolink", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SendMouseRelative(int dx, int dy);

    [DllImport("echolink", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SendMouseClick(int button, int state);

    [DllImport("echolink", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SendSystemAction(int actionId);

    // P/Invoke for Windows
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    private static bool IsAndroid() => RuntimeInformation.IsOSPlatform(OSPlatform.Create("ANDROID"));

    // ── Server Side (Linux / Windows) ──────────────────────────────────────────

    public void StartServer()
    {
        if (_serverCts != null) return;
        _serverCts = new CancellationTokenSource();
        
        _listener = new TcpListener(IPAddress.Any, RcDirectPort);
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !IsAndroid())
        {
            try { InitializeVirtualMouse(); }
            catch (Exception ex) { _log.Warning($"[RC] Failed to init Linux uinput: {ex.Message}"); }
        }

        try
        {
            _listener.Start();
            _log.Info($"[RC] Server listening on TCP port {RcDirectPort}");

            _ = Task.Run(async () =>
            {
                while (!_serverCts.IsCancellationRequested)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync(_serverCts.Token);
                        client.NoDelay = true; // IMPORTANT for low latency
                        _log.Info($"[RC] Client connected from {client.Client.RemoteEndPoint}");
                        _ = HandleClientAsync(client, _serverCts.Token);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _log.Debug($"[RC] Accept loop error: {ex.Message}"); }
                }
            });
        }
        catch (Exception ex)
        {
            _log.Error($"[RC] Server failed to start: {ex.Message}");
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
        _log.Info("RemoteControl Server stopped");
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var stream = client.GetStream();
        var buffer = new byte[16];
        
        while (!ct.IsCancellationRequested && client.Connected)
        {
            try
            {
                int headerBytes = await ReadExactAsync(stream, buffer, 3, ct);
                if (headerBytes < 3) break;

                byte msgType = buffer[0];
                ushort payloadLen = BitConverter.ToUInt16(buffer, 1);

                if (payloadLen > 0)
                {
                    int payloadBytes = await ReadExactAsync(stream, buffer, payloadLen, ct);
                    if (payloadBytes < payloadLen) break;
                }

                ProcessEvent(msgType, buffer);
            }
            catch
            {
                break;
            }
        }
    }

    private async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer, totalRead, count - totalRead, ct);
            if (read == 0) return totalRead;
            totalRead += read;
        }
        return totalRead;
    }

    private void ProcessEvent(byte type, byte[] payload)
    {
        if (type == 0x01) // MOUSE_MOVE
        {
            short dx = BitConverter.ToInt16(payload, 0);
            short dy = BitConverter.ToInt16(payload, 2);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                mouse_event(0x0001, dx, dy, 0, 0); // MOUSEEVENTF_MOVE
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !IsAndroid())
            {
                try { SendMouseRelative(dx, dy); } catch {}
            }
        }
        else if (type == 0x02) // MOUSE_CLICK
        {
            byte button = payload[0];
            byte state = payload[1];

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                uint flag = 0;
                if (button == 0) // Left
                    flag = state == 1 ? 0x0002u : 0x0004u; // DOWN : UP
                else if (button == 1) // Right
                    flag = state == 1 ? 0x0008u : 0x0010u; // DOWN : UP
                if (flag != 0) mouse_event(flag, 0, 0, 0, 0);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !IsAndroid())
            {
                try { SendMouseClick(button, state); } catch {}
            }
        }
        else if (type == 0x05) // SYSTEM_ACTION
        {
            byte action = payload[0];
            if (action == 0x00) // Lock
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    LockWorkStation();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !IsAndroid())
                {
                    _log.Info("[RC] Locking screen via loginctl...");
                    Task.Run(() => System.Diagnostics.Process.Start("loginctl", "lock-sessions"));
                }
            }
            else if (action == 0x01) // Restart
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !IsAndroid())
                {
                    _log.Info("[RC] Restarting system via systemctl...");
                    Task.Run(() => System.Diagnostics.Process.Start("sudo", "systemctl reboot"));
                }
            }
            else if (action == 0x02) // Shutdown
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !IsAndroid())
                {
                    _log.Info("[RC] Shutting down system via systemctl...");
                    Task.Run(() => System.Diagnostics.Process.Start("sudo", "systemctl poweroff"));
                }
            }
        }
    }

    // ── Client Side (Android / Desktop) ──────────────────────────────────────

    private TcpClient? _client;
    private NetworkStream? _clientStream;

    public async Task<bool> ConnectToTargetAsync(Device targetDevice, string pkeyPath, CancellationToken ct)
    {
        Disconnect();
        _log.Info($"[RC] Connecting to {targetDevice.Name} ({targetDevice.IpAddress})...");

        try
        {
            _client = new TcpClient();
            _client.NoDelay = true;

            if (IsAndroid())
            {
                _log.Debug("[RC] Using SOCKS5 proxy on 127.0.0.1:1055");
                await _client.ConnectAsync("127.0.0.1", 1055, ct);
                var stream = _client.GetStream();
                
                // SOCKS5 Handshake
                await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
                byte[] response = new byte[2];
                await stream.ReadAsync(response, ct);
                if (response[0] != 0x05 || response[1] != 0x00) throw new Exception("SOCKS5 auth failed");

                var ipBytes = IPAddress.Parse(targetDevice.IpAddress).GetAddressBytes();
                byte[] portBytes = BitConverter.GetBytes((ushort)RcDirectPort);
                if (BitConverter.IsLittleEndian) Array.Reverse(portBytes); // Network byte order

                byte[] connectReq = new byte[6 + ipBytes.Length];
                connectReq[0] = 0x05;
                connectReq[1] = 0x01; // CONNECT
                connectReq[2] = 0x00; // RSV
                connectReq[3] = 0x01; // IPv4
                Array.Copy(ipBytes, 0, connectReq, 4, 4);
                Array.Copy(portBytes, 0, connectReq, 8, 2);

                await stream.WriteAsync(connectReq, ct);
                
                byte[] connectResp = new byte[10];
                await stream.ReadAsync(connectResp, ct);
                if (connectResp[1] != 0x00) throw new Exception($"SOCKS5 connect failed (code {connectResp[1]})");

                _clientStream = stream;
            }
            else
            {
                _log.Debug($"[RC] Direct connection to {targetDevice.IpAddress}:{RcDirectPort}");
                await _client.ConnectAsync(targetDevice.IpAddress, RcDirectPort, ct);
                _clientStream = _client.GetStream();
            }

            _log.Info($"[RC] Connected to {targetDevice.IpAddress}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"[RC] Connection failed: {ex.Message}");
            return false;
        }
    }

    public void Disconnect()
    {
        _clientStream?.Dispose();
        _clientStream = null;
        _client?.Dispose();
        _client = null;
    }

    public async Task SendMoveAsync(double dx, double dy)
    {
        if (_clientStream == null || !_client.Connected) return;

        try
        {
            short sendDx = (short)(dx * 2.5);
            short sendDy = (short)(dy * 2.5);

            byte[] packet = new byte[7];
            packet[0] = 0x01; // MOUSE_MOVE
            
            byte[] len = BitConverter.GetBytes((ushort)4);
            packet[1] = len[0];
            packet[2] = len[1];

            byte[] xBytes = BitConverter.GetBytes(sendDx);
            byte[] yBytes = BitConverter.GetBytes(sendDy);
            
            packet[3] = xBytes[0];
            packet[4] = xBytes[1];
            packet[5] = yBytes[0];
            packet[6] = yBytes[1];

            await _clientStream.WriteAsync(packet, 0, packet.Length);
        }
        catch
        {
            Disconnect();
        }
    }

    public async Task SendCommandAsync(string cmd)
    {
        if (_clientStream == null || !_client.Connected) return;
        try
        {
            byte actionId = cmd switch
            {
                "Lock"     => 0x00,
                "Restart"  => 0x01,
                "Shutdown" => 0x02,
                _          => 0xFF
            };
            
            if (actionId == 0xFF) return;

            byte[] packet = new byte[4];
            packet[0] = 0x05; // SYSTEM_ACTION
            
            byte[] len = BitConverter.GetBytes((ushort)1);
            packet[1] = len[0];
            packet[2] = len[1];
            
            packet[3] = actionId;

            await _clientStream.WriteAsync(packet, 0, packet.Length);
        }
        catch { Disconnect(); }
    }

    public async Task SendClickAsync(int button, int state)
    {
        if (_clientStream == null || !_client.Connected) return;
        try
        {
            byte[] packet = new byte[5];
            packet[0] = 0x02; // MOUSE_CLICK
            
            byte[] len = BitConverter.GetBytes((ushort)2);
            packet[1] = len[0];
            packet[2] = len[1];
            
            packet[3] = (byte)button;
            packet[4] = (byte)state;

            await _clientStream.WriteAsync(packet, 0, packet.Length);
        }
        catch { Disconnect(); }
    }
}