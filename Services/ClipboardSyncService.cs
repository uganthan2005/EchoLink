using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using EchoLink.Models;

namespace EchoLink.Services;

public class ClipboardSyncService
{
    private static readonly Lazy<ClipboardSyncService> _instance = new(() => new ClipboardSyncService());
    public static ClipboardSyncService Instance => _instance.Value;

    private const int ClipboardSyncPort = 44555;

    private readonly LoggingService _log = LoggingService.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly ClipboardJournalService _journal = new();

    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private Task? _monitorTask;
    private Task? _reliabilityTask;

    private string _lastObservedHash = "";
    private DateTime _suppressLocalUntilUtc = DateTime.MinValue;
    private string _localAccountId = "";
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ClipboardSyncMessage>> _pendingByPeer = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _knownOnlinePeers = new(StringComparer.OrdinalIgnoreCase);

    public event Action<ClipboardEntry>? ClipboardReceived;

    private ClipboardSyncService() { }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_cts is not null)
            return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await TailscaleService.Instance.ExposeClipboardPortAsync(_cts.Token);

        _localAccountId = await TailscaleService.Instance.GetCurrentAccountIdAsync(_cts.Token)
            ?? "unknown-account";

        _listenerTask = Task.Run(() => RunListenerAsync(_cts.Token), _cts.Token);
        _monitorTask = Task.Run(() => RunMonitorAsync(_cts.Token), _cts.Token);
        _reliabilityTask = Task.Run(() => RunReliabilityLoopAsync(_cts.Token), _cts.Token);

        _log.Info("MirrorClip sync engine started.");
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            return;

        _cts.Cancel();
        try
        {
            if (_listenerTask is not null)
                await _listenerTask;
            if (_monitorTask is not null)
                await _monitorTask;
            if (_reliabilityTask is not null)
                await _reliabilityTask;
        }
        catch (OperationCanceledException) { }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _listenerTask = null;
            _monitorTask = null;
            _reliabilityTask = null;
        }

        _log.Info("MirrorClip sync engine stopped.");
    }

    public async Task PushCurrentClipboardAsync(CancellationToken ct = default)
    {
        var text = await GetClipboardTextAsync();
        if (string.IsNullOrWhiteSpace(text))
            return;

        await BroadcastClipboardAsync(text, ct);
    }

    private async Task RunMonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var settings = _settings.Load();
                if (!settings.MirrorClipEnabled)
                {
                    await Task.Delay(1200, ct);
                    continue;
                }

                var text = await GetClipboardTextAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    await Task.Delay(900, ct);
                    continue;
                }

                var hash = ComputeHash(text);
                if (DateTime.UtcNow < _suppressLocalUntilUtc)
                {
                    _lastObservedHash = hash;
                    await Task.Delay(900, ct);
                    continue;
                }

                if (hash == _lastObservedHash)
                {
                    await Task.Delay(900, ct);
                    continue;
                }

                _lastObservedHash = hash;
                await BroadcastClipboardAsync(text, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warning($"MirrorClip monitor loop error: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task BroadcastClipboardAsync(string text, CancellationToken ct)
    {
        var settings = _settings.Load();
        var hash = ComputeHash(text);

        var (selfIp, devices) = await TailscaleService.Instance.GetNetworkStatusAsync(ct);
        var sender = selfIp ?? Environment.MachineName;
        var accountId = await TailscaleService.Instance.GetCurrentAccountIdAsync(ct)
            ?? _localAccountId;

        var message = new ClipboardSyncMessage
        {
            Type = "clip",
            EventId = Guid.NewGuid().ToString("N"),
            Sequence = _journal.NextSequence(),
            OriginDeviceId = sender,
            SenderDeviceId = sender,
            SenderAccountId = accountId,
            TimestampUtc = DateTime.UtcNow,
            ContentType = "text/plain",
            ContentText = text,
            ContentHash = hash,
            GhostPaste = settings.GhostPasteEnabled
        };

        await _journal.AppendAsync(message, ct);

        var peers = devices
            .Where(d => d.IsOnline && !d.IsSelf && !string.IsNullOrWhiteSpace(d.IpAddress))
            .Select(d => d.IpAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var peerIp in peers)
        {
            try
            {
                bool acked = await SendClipToPeerAsync(peerIp, message, ct);
                if (!acked)
                    QueuePending(peerIp, message);
            }
            catch (Exception ex)
            {
                _log.Warning($"MirrorClip send failed to {peerIp}: {ex.Message}");
                QueuePending(peerIp, message);
            }
        }

        _log.Debug($"MirrorClip broadcast complete ({peers.Count} peers).");
    }

    private async Task RunListenerAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, ClipboardSyncPort);
        listener.Start();
        ct.Register(() => listener.Stop());

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleIncomingClientAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleIncomingClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

        var line = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(line))
            return;

        ClipboardSyncMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<ClipboardSyncMessage>(line);
        }
        catch
        {
            return;
        }

        if (message is null)
            return;

        if (message.Type == "ack")
            return;

        if (message.Type != "clip" || string.IsNullOrWhiteSpace(message.EventId))
            return;

        if (!string.Equals(message.SenderAccountId, _localAccountId, StringComparison.Ordinal))
            return;

        if (_journal.HasEvent(message.EventId))
        {
            await WriteAckAsync(writer, message.EventId, accepted: true);
            return;
        }

        var settings = _settings.Load();
        if (!settings.MirrorClipEnabled)
        {
            await WriteAckAsync(writer, message.EventId, accepted: false);
            return;
        }

        await _journal.AppendAsync(message, ct);
        await ApplyRemoteClipboardAsync(message.ContentText);

        ClipboardReceived?.Invoke(new ClipboardEntry(
            message.ContentText,
            message.SenderDeviceId,
            DateTime.Now));

        await WriteAckAsync(writer, message.EventId, accepted: true);

        _log.Info($"MirrorClip received clip from {message.SenderDeviceId}.");
    }

    private async Task ApplyRemoteClipboardAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var app = Avalonia.Application.Current;
        if (app?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime dt || dt.MainWindow is null)
            return;

        var clipboard = TopLevel.GetTopLevel(dt.MainWindow)?.Clipboard;
        if (clipboard is null)
            return;

        _suppressLocalUntilUtc = DateTime.UtcNow.AddSeconds(2);
        _lastObservedHash = ComputeHash(text);
        await clipboard.SetTextAsync(text);
    }

    private static string ComputeHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }

    private static async Task<string?> GetClipboardTextAsync()
    {
        var app = Avalonia.Application.Current;
        if (app?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime dt || dt.MainWindow is null)
            return null;

        var clipboard = TopLevel.GetTopLevel(dt.MainWindow)?.Clipboard;
        if (clipboard is null)
            return null;

        return await clipboard.GetTextAsync();
    }

    private async Task<bool> SendClipToPeerAsync(string targetIp, ClipboardSyncMessage message, CancellationToken ct)
    {
        using var client = await ConnectViaSocks5Async(targetIp, ClipboardSyncPort, ct);
        if (client is null || !client.Connected)
            throw new InvalidOperationException("Could not connect via SOCKS5.");

        using var stream = client.GetStream();
        using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var json = JsonSerializer.Serialize(message);
        await writer.WriteLineAsync(json);

        using var ackTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ackTimeout.CancelAfter(TimeSpan.FromSeconds(3));

        string? ackLine;
        try
        {
            ackLine = await reader.ReadLineAsync(ackTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(ackLine))
            return false;

        ClipboardSyncMessage? ack;
        try
        {
            ack = JsonSerializer.Deserialize<ClipboardSyncMessage>(ackLine);
        }
        catch
        {
            return false;
        }

        return ack is not null
            && ack.Type == "ack"
            && ack.Accepted
            && string.Equals(ack.AckForEventId, message.EventId, StringComparison.Ordinal);
    }

    private async Task WriteAckAsync(StreamWriter writer, string eventId, bool accepted)
    {
        var ack = new ClipboardSyncMessage
        {
            Type = "ack",
            AckForEventId = eventId,
            Accepted = accepted,
            SenderDeviceId = Environment.MachineName,
            SenderAccountId = _localAccountId,
            TimestampUtc = DateTime.UtcNow
        };

        await writer.WriteLineAsync(JsonSerializer.Serialize(ack));
    }

    private void QueuePending(string peerIp, ClipboardSyncMessage message)
    {
        var perPeer = _pendingByPeer.GetOrAdd(peerIp, _ => new ConcurrentDictionary<string, ClipboardSyncMessage>(StringComparer.Ordinal));
        perPeer[message.EventId] = message;
    }

    private async Task RunReliabilityLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (_, devices) = await TailscaleService.Instance.GetNetworkStatusAsync(ct);
                var onlinePeers = devices
                    .Where(d => d.IsOnline && !d.IsSelf && !string.IsNullOrWhiteSpace(d.IpAddress))
                    .Select(d => d.IpAddress)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var peer in onlinePeers)
                {
                    if (!_knownOnlinePeers.Contains(peer))
                        await ReplayRecentToPeerAsync(peer, ct);

                    await RetryPendingForPeerAsync(peer, ct);
                }

                _knownOnlinePeers = onlinePeers;
                await Task.Delay(5000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warning($"MirrorClip reliability loop error: {ex.Message}");
                await Task.Delay(2000, ct);
            }
        }
    }

    private async Task ReplayRecentToPeerAsync(string peerIp, CancellationToken ct)
    {
        var recent = await _journal.GetRecentClipMessagesAsync(10, ct);
        if (recent.Count == 0)
            return;

        foreach (var msg in recent)
        {
            try
            {
                bool acked = await SendClipToPeerAsync(peerIp, msg, ct);
                if (!acked)
                    QueuePending(peerIp, msg);
            }
            catch
            {
                QueuePending(peerIp, msg);
                break;
            }
        }

        _log.Debug($"MirrorClip replayed {recent.Count} recent clips to {peerIp}.");
    }

    private async Task RetryPendingForPeerAsync(string peerIp, CancellationToken ct)
    {
        if (!_pendingByPeer.TryGetValue(peerIp, out var pending) || pending.Count == 0)
            return;

        foreach (var pair in pending.ToArray())
        {
            try
            {
                bool acked = await SendClipToPeerAsync(peerIp, pair.Value, ct);
                if (acked)
                    pending.TryRemove(pair.Key, out _);
            }
            catch
            {
                // Keep pending and retry later.
            }
        }
    }

    private static async Task<TcpClient?> ConnectViaSocks5Async(string targetIp, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync("127.0.0.1", 1055, ct);
            var stream = client.GetStream();

            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
            byte[] response1 = new byte[2];
            int read = await stream.ReadAsync(response1, ct);
            if (read != 2 || response1[1] != 0x00)
                return null;

            byte[] destIpBytes = IPAddress.Parse(targetIp).GetAddressBytes();
            byte[] portBytes = BitConverter.GetBytes((short)port);
            if (BitConverter.IsLittleEndian) Array.Reverse(portBytes);

            byte[] request = new byte[6 + destIpBytes.Length];
            request[0] = 0x05;
            request[1] = 0x01;
            request[2] = 0x00;
            request[3] = destIpBytes.Length == 16 ? (byte)0x04 : (byte)0x01;
            destIpBytes.CopyTo(request, 4);
            portBytes.CopyTo(request, 4 + destIpBytes.Length);

            await stream.WriteAsync(request, ct);

            byte[] response2 = new byte[32];
            read = await stream.ReadAsync(response2, ct);
            if (read < 2 || response2[1] != 0x00)
                return null;

            return client;
        }
        catch
        {
            client.Dispose();
            return null;
        }
    }
}
