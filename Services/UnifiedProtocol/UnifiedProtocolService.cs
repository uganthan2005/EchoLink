using System.Net;
using System.Net.Sockets;

namespace EchoLink.Services.UnifiedProtocol;

/// <summary>
/// Unified protocol server that accepts connections on port 55555
/// and dispatches messages to registered handlers by message type.
/// </summary>
public class UnifiedProtocolService
{
    private static UnifiedProtocolService? _instance;
    public static UnifiedProtocolService Instance => _instance ??= new UnifiedProtocolService();

    private readonly LoggingService _log = LoggingService.Instance;
    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;

    public const int UnifiedPort = 55555;

    // Registered handlers (services plug in here)
    // The second parameter is a func that allows the handler to send a reply back over the same stream:
    // async Task ReplyAsync(UnifiedMessageType type, byte[] payload)
    private readonly Dictionary<UnifiedMessageType, Func<byte[], Func<UnifiedMessageType, byte[], Task>, CancellationToken, Task>> _handlers = new();

    /// <summary>
    /// Register a handler for a specific message type.
    /// Services call this to receive messages of their type.
    /// </summary>
    public void RegisterHandler(UnifiedMessageType type, Func<byte[], Func<UnifiedMessageType, byte[], Task>, CancellationToken, Task> handler)
    {
        _handlers[type] = handler;
        _log.Debug($"[Unified] Registered handler for {type}");
    }

    /// <summary>
    /// Start the unified protocol server.
    /// Call once at application startup.
    /// </summary>
    public void StartServer()
    {
        if (_serverCts != null) return;
        _serverCts = new CancellationTokenSource();
        
        _listener = new TcpListener(IPAddress.Any, UnifiedPort);
        _listener.Start();
        _log.Info($"[Unified] Server listening on TCP port {UnifiedPort}");

        _ = Task.Run(async () =>
        {
            while (!_serverCts.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_serverCts.Token);
                    client.NoDelay = true; // Disable Nagle for low latency
                    _log.Info($"[Unified] Client connected from {client.Client.RemoteEndPoint}");
                    _ = HandleClientAsync(client, _serverCts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) 
                { 
                    _log.Debug($"[Unified] Accept loop error: {ex.Message}"); 
                }
            }
        });
    }

    /// <summary>
    /// Stop the unified protocol server.
    /// </summary>
    public void StopServer()
    {
        if (_serverCts == null) return;
        _serverCts.Cancel();
        _serverCts.Dispose();
        _serverCts = null;
        _listener?.Stop();
        _listener = null;
        _log.Info("[Unified] Server stopped");
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var stream = client.GetStream();
        var headerBuffer = new byte[5];
        
        while (!ct.IsCancellationRequested && client.Connected)
        {
            try
            {
                // Read 5-byte header: [Type:1][Length:4]
                int headerBytes = await ReadExactAsync(stream, headerBuffer, 5, ct);
                if (headerBytes < 5) break;

                byte messageType = headerBuffer[0];
                int payloadLen = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(headerBuffer, 1));

                // Read payload
                byte[] payload = Array.Empty<byte>();
                if (payloadLen > 0)
                {
                    payload = new byte[payloadLen];
                    await ReadExactAsync(stream, payload, payloadLen, ct);
                }

                // Dispatch to registered handler
                await DispatchMessageAsync((UnifiedMessageType)messageType, payload, stream, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Debug($"[Unified] Client handler error: {ex.Message}");
                break;
            }
        }
    }

    private async Task DispatchMessageAsync(UnifiedMessageType messageType, byte[] payload, NetworkStream stream, CancellationToken ct)
    {
        if (_handlers.TryGetValue(messageType, out var handler))
        {
            try
            {
                // Create a reply function bound to this specific stream
                Func<UnifiedMessageType, byte[], Task> replyFunc = async (replyType, replyPayload) =>
                {
                    var header = new byte[5];
                    header[0] = (byte)replyType;
                    var lengthBytes = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(replyPayload.Length));
                    Array.Copy(lengthBytes, 0, header, 1, 4);

                    // To prevent overlapping writes, we might need a lock on the stream.
                    // For now, assume handlers don't write concurrently to the same stream.
                    await stream.WriteAsync(header, ct);
                    if (replyPayload.Length > 0)
                    {
                        await stream.WriteAsync(replyPayload, ct);
                    }
                    await stream.FlushAsync(ct);
                };

                await handler(payload, replyFunc, ct);
            }
            catch (Exception ex)
            {
                _log.Error($"[Unified] Handler error for {messageType}: {ex.Message}");
            }
        }
        else
        {
            _log.Warning($"[Unified] No handler for message type: 0x{(byte)messageType:X2}");
        }
    }

    /// <summary>
    /// Read exactly 'count' bytes from stream.
    /// Returns total bytes read (may be less than count if connection closes).
    /// </summary>
    private async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), ct);
            if (read == 0) return totalRead; // Connection closed
            totalRead += read;
        }
        return totalRead;
    }
}
