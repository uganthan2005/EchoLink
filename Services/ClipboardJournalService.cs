using System.Text.Json;
using EchoLink.Models;

namespace EchoLink.Services;

public class ClipboardJournalService
{
    private readonly LoggingService _log = LoggingService.Instance;
    private readonly string _journalPath;
    private readonly HashSet<string> _seenEventIds = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastSequence;

    public ClipboardJournalService()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EchoLink");
        Directory.CreateDirectory(appData);

        _journalPath = Path.Combine(appData, "clipboard_journal.jsonl");
        LoadExistingIndex();
    }

    public long NextSequence() => Interlocked.Increment(ref _lastSequence);

    public bool HasEvent(string eventId)
    {
        lock (_seenEventIds)
            return _seenEventIds.Contains(eventId);
    }

    public async Task AppendAsync(ClipboardSyncMessage message, CancellationToken ct = default)
    {
        var line = JsonSerializer.Serialize(message);

        await _gate.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(_journalPath, line + Environment.NewLine, ct);
            lock (_seenEventIds)
                _seenEventIds.Add(message.EventId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ClipboardSyncMessage>> GetRecentClipMessagesAsync(int maxCount, CancellationToken ct = default)
    {
        if (maxCount <= 0)
            return [];

        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_journalPath))
                return [];

            var lines = await File.ReadAllLinesAsync(_journalPath, ct);
            var result = new List<ClipboardSyncMessage>(maxCount);

            for (int i = lines.Length - 1; i >= 0 && result.Count < maxCount; i--)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                ClipboardSyncMessage? msg;
                try
                {
                    msg = JsonSerializer.Deserialize<ClipboardSyncMessage>(line);
                }
                catch
                {
                    continue;
                }

                if (msg is null || msg.Type != "clip")
                    continue;

                result.Add(msg);
            }

            result.Reverse();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void LoadExistingIndex()
    {
        if (!File.Exists(_journalPath))
            return;

        try
        {
            foreach (var line in File.ReadLines(_journalPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var msg = JsonSerializer.Deserialize<ClipboardSyncMessage>(line);
                if (msg is null || string.IsNullOrWhiteSpace(msg.EventId))
                    continue;

                _seenEventIds.Add(msg.EventId);
                if (msg.Sequence > _lastSequence)
                    _lastSequence = msg.Sequence;
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed loading clipboard journal index: {ex.Message}");
        }
    }
}
