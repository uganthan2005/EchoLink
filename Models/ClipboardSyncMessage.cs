namespace EchoLink.Models;

public class ClipboardSyncMessage
{
    public string Type { get; set; } = "clip";
    public string EventId { get; set; } = "";
    public long Sequence { get; set; }
    public string OriginDeviceId { get; set; } = "";
    public string SenderDeviceId { get; set; } = "";
    public string SenderAccountId { get; set; } = "";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string ContentType { get; set; } = "text/plain";
    public string ContentText { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public bool GhostPaste { get; set; } = true;

    // Ack fields (used when Type == "ack")
    public string AckForEventId { get; set; } = "";
    public bool Accepted { get; set; } = true;
}
