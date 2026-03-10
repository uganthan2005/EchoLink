using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
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

    // Per-peer failure tracking — stops SOCKS5 rejection spam and retries
    private readonly ConcurrentDictionary<string, int> _peerFailCount = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _peerCooldownUntil = new(StringComparer.OrdinalIgnoreCase);

    public event Action<ClipboardEntry>? ClipboardReceived;

    private ClipboardSyncService() { }

    // ── Peer-failure helpers ─────────────────────────────────────────────────

    private bool IsOnCooldown(string peerIp)
        => _peerCooldownUntil.TryGetValue(peerIp, out var until) && DateTime.UtcNow < until;

    private void RecordPeerFailure(string peerIp)
    {
        var fails = _peerFailCount.AddOrUpdate(peerIp, 1, (_, v) => v + 1);
        // Exponential back-off: 30 s → 60 s → 120 s → 240 s → 300 s (max)
        var seconds = (int)Math.Min(30 * Math.Pow(2, fails - 1), 300);
        _peerCooldownUntil[peerIp] = DateTime.UtcNow.AddSeconds(seconds);

        // Only log on the first failure and every 5 attempts after that
        if (fails == 1 || fails % 5 == 0)
            _log.Warning($"MirrorClip: cannot reach {peerIp}:44555 (attempt {fails}). " +
                         $"Retrying in {seconds}s. Ensure EchoLink is running there and 'tailscale serve' " +
                         $"exposed port 44555 on that device.");
    }

    private void RecordPeerSuccess(string peerIp)
    {
        _peerFailCount.TryRemove(peerIp, out _);
        _peerCooldownUntil.TryRemove(peerIp, out _);
    }

    // ─────────────────────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_cts is not null)
            return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Port 44555 is already exposed by ExposeLocalPortsAsync called from
        // MainWindowViewModel.InitializeSetupAsync — no need to call it again here.

        _localAccountId = await TailscaleService.Instance.GetCurrentAccountIdAsync(_cts.Token)
            ?? "unknown-account";
        _log.Info($"MirrorClip: local account ID = {_localAccountId}");

        _listenerTask = Task.Run(() => RunListenerAsync(_cts.Token), _cts.Token);
        _monitorTask = Task.Run(() => RunMonitorAsync(_cts.Token), _cts.Token);
        _reliabilityTask = Task.Run(() => RunReliabilityLoopAsync(_cts.Token), _cts.Token);

        _log.Info("MirrorClip sync engine started (listener + monitor + reliability loops).");
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
        {
            _log.Warning("MirrorClip PushCurrentClipboard: clipboard was empty or unreadable.");
            return;
        }

        _log.Info($"MirrorClip PushCurrentClipboard: broadcasting {text.Length} chars...");
        await BroadcastClipboardAsync(text, ct);
    }

    public Task UpdateClipboardShareTargetsAsync(IEnumerable<string> targetIps)
    {
        var settings = _settings.Load();
        settings.ClipboardShareTargets = targetIps
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Select(ip => ip.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.ClipboardUseTargetSelection = true;

        _settings.Save(settings);

        // Drop pending messages for peers no longer selected for clipboard share.
        var selected = settings.ClipboardShareTargets
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var peerIp in _pendingByPeer.Keys.ToArray())
        {
            if (selected.Count > 0 && !selected.Contains(peerIp))
                _pendingByPeer.TryRemove(peerIp, out _);
        }

        _log.Info($"MirrorClip: updated clipboard target list ({settings.ClipboardShareTargets.Count} selected peer(s)).");
        return Task.CompletedTask;
    }

    private async Task RunMonitorAsync(CancellationToken ct)
    {
        _log.Info("MirrorClip monitor loop started.");
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
                _log.Info($"MirrorClip monitor: new clipboard content detected ({text.Length} chars), broadcasting...");
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

        // Fire event to add local clip to UI history
        ClipboardReceived?.Invoke(new ClipboardEntry(
            message.ContentText,
            message.OriginDeviceId + " (me)",
            DateTime.Now));

        var peers = GetEligibleClipboardPeers(devices, settings)
            .Select(d => d.IpAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _log.Info($"MirrorClip broadcast: {peers.Count} online peer(s) found. Self IP={sender}, Account={accountId}");
        if (peers.Count == 0)
            _log.Warning("MirrorClip broadcast: no peers found! Check that other devices are connected and online.");

        foreach (var peerIp in peers)
        {
            if (IsOnCooldown(peerIp))
            {
                QueuePending(peerIp, message); // will retry when cooldown expires
                continue;
            }

            try
            {
                bool acked = await SendClipToPeerAsync(peerIp, message, ct);
                if (acked)
                    RecordPeerSuccess(peerIp);
                else
                {
                    RecordPeerFailure(peerIp);
                    QueuePending(peerIp, message);
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"MirrorClip send failed to {peerIp}: {ex.Message}");
                RecordPeerFailure(peerIp);
                QueuePending(peerIp, message);
            }
        }

        _log.Debug($"MirrorClip broadcast complete ({peers.Count} peers).");
    }

    private async Task RunListenerAsync(CancellationToken ct)
    {
        _log.Info($"MirrorClip listener starting on port {ClipboardSyncPort}...");
        var listener = new TcpListener(IPAddress.Any, ClipboardSyncPort);
        listener.Start();
        _log.Info($"MirrorClip listener bound to 0.0.0.0:{ClipboardSyncPort}");
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

        if (!await IsSameAccountAsync(message.SenderAccountId, ct))
        {
            _log.Debug($"MirrorClip ignored clip from different account. sender={message.SenderAccountId}, local={_localAccountId}");
            return;
        }

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

        _suppressLocalUntilUtc = DateTime.UtcNow.AddSeconds(2);
        _lastObservedHash = ComputeHash(text);

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var app = Avalonia.Application.Current;
                if (app?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime dt || dt.MainWindow is null)
                    return;

                var clipboard = TopLevel.GetTopLevel(dt.MainWindow)?.Clipboard;
                if (clipboard is null)
                    return;

                await clipboard.SetTextAsync(text);
                _log.Info($"MirrorClip: applied remote clipboard ({text.Length} chars) to local device.");
            }
            catch (Exception ex)
            {
                _log.Warning($"MirrorClip: failed to apply remote clipboard: {ex.Message}");
            }
        });
    }

    private async Task<bool> IsSameAccountAsync(string? senderAccountId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(senderAccountId))
            return false;

        if (string.Equals(senderAccountId, "unknown-account", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(_localAccountId) || _localAccountId == "unknown-account")
        {
            _localAccountId = await TailscaleService.Instance.GetCurrentAccountIdAsync(ct)
                ?? _localAccountId;
        }

        // Only enforce strict matching when both sides have resolved IDs.
        if (string.IsNullOrWhiteSpace(_localAccountId) || _localAccountId == "unknown-account")
            return true;

        return string.Equals(senderAccountId, _localAccountId, StringComparison.Ordinal);
    }

    private static string ComputeHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }

    private async Task<string?> GetClipboardTextAsync()
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var app = Avalonia.Application.Current;
                if (app?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime dt || dt.MainWindow is null)
                    return null;

                var clipboard = TopLevel.GetTopLevel(dt.MainWindow)?.Clipboard;
                if (clipboard is null)
                    return null;

                return await clipboard.GetTextAsync();
            });
        }
        catch (Exception ex)
        {
            _log.Warning($"MirrorClip: clipboard read failed: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> SendClipToPeerAsync(string targetIp, ClipboardSyncMessage message, CancellationToken ct)
    {
        using var client = await ConnectToPeerAsync(targetIp, ClipboardSyncPort, ct);
        if (client is null || !client.Connected)
            throw new InvalidOperationException($"Could not connect to {targetIp}:{ClipboardSyncPort}");

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
            _log.Debug($"MirrorClip: ACK timeout from {targetIp}");
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
                var settings = _settings.Load();
                var onlinePeers = GetEligibleClipboardPeers(devices, settings)
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
        if (IsOnCooldown(peerIp))
            return;

        var recent = await _journal.GetRecentClipMessagesAsync(10, ct);
        if (recent.Count == 0)
            return;

        foreach (var msg in recent)
        {
            try
            {
                bool acked = await SendClipToPeerAsync(peerIp, msg, ct);
                if (acked)
                    RecordPeerSuccess(peerIp);
                else
                {
                    RecordPeerFailure(peerIp);
                    QueuePending(peerIp, msg);
                    break; // stop replaying once peer becomes unreachable
                }
            }
            catch
            {
                RecordPeerFailure(peerIp);
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

        if (IsOnCooldown(peerIp))
            return;

        foreach (var pair in pending.ToArray())
        {
            try
            {
                bool acked = await SendClipToPeerAsync(peerIp, pair.Value, ct);
                if (acked)
                {
                    RecordPeerSuccess(peerIp);
                    pending.TryRemove(pair.Key, out _);
                }
                else
                {
                    RecordPeerFailure(peerIp);
                    break; // stop retrying this peer this cycle
                }
            }
            catch
            {
                RecordPeerFailure(peerIp);
                break;
            }
        }
    }

    /// <summary>
    /// Tries SOCKS5 proxy first (for userspace networking).
    ///
    /// MirrorClip follows the same transport model as SSH/SFTP in this app, which
    /// consistently tunnels through tailscaled SOCKS5 when userspace networking is enabled.
    /// </summary>
    private async Task<TcpClient?> ConnectToPeerAsync(string targetIp, int port, CancellationToken ct)
    {
        return await ConnectViaSocks5Async(targetIp, port, ct);
    }

    private static IEnumerable<Device> GetEligibleClipboardPeers(
        IEnumerable<Device> devices,
        SettingsData settings)
    {
        var selected = settings.ClipboardShareTargets
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            if (!device.IsOnline || device.IsSelf || string.IsNullOrWhiteSpace(device.IpAddress))
                continue;

            if (!settings.ClipboardUseTargetSelection)
            {
                yield return device;
                continue;
            }

            if (selected.Contains(device.IpAddress))
                yield return device;
        }
    }

    private async Task<TcpClient?> ConnectViaSocks5Async(string targetIp, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            // Step 1: Connect to SOCKS5 proxy
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync("127.0.0.1", 1055, connectTimeout.Token);
            var stream = client.GetStream();

            // Step 2: SOCKS5 handshake
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
            byte[] response1 = new byte[2];
            int read = await stream.ReadAsync(response1, ct);
            if (read != 2 || response1[1] != 0x00)
            {
                _log.Debug($"MirrorClip SOCKS5: handshake failed (read={read}, method={response1[1]})");
                client.Dispose();
                return null;
            }

            // Step 3: SOCKS5 connect request
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
            {
                _log.Debug($"MirrorClip SOCKS5: connect to {targetIp}:{port} rejected (read={read}, reply=0x{response2[1]:X2})");
                client.Dispose();
                return null;
            }

            _log.Debug($"MirrorClip SOCKS5: connected to {targetIp}:{port}");
            return client;
        }
        catch (Exception ex)
        {
            _log.Debug($"MirrorClip SOCKS5: exception connecting to {targetIp}:{port}: {ex.Message}");
            client.Dispose();
            return null;
        }
    }
}
