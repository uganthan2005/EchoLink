namespace EchoLink.Services.UnifiedProtocol;

/// <summary>
/// Message types for unified protocol (port 55555)
/// Format: [Type:1 byte][Length:4 bytes big-endian][Payload:N bytes]
/// </summary>
public enum UnifiedMessageType : byte
{
    // === Remote Control (0x01-0x05) ===
    MouseMove = 0x01,
    MouseClick = 0x02,
    KeyPress = 0x03,
    Scroll = 0x04,
    SystemAction = 0x05,
    
    // === Audio Streaming (0x06) ===
    AudioFrame = 0x06,
    
    // === System Monitor (0x07-0x08) ===
    MonitorRequest = 0x07,
    MonitorResponse = 0x08,
    
    // === Clipboard Sync (0x09) ===
    ClipboardSync = 0x09,
    
    // === Macros (0x0A) ===
    MacroExecute = 0x0A,
    
    // === File Browser (0x0B-0x0C) ===
    FileBrowserRequest = 0x0B,
    FileBrowserResponse = 0x0C,

    KeyboardEvent = 0x0D,

    // === Keepalive (0xFF) ===
    PingPong = 0xFF
}
