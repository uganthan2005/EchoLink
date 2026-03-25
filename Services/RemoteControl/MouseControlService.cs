using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace EchoLink.Services;

public class MouseControlService
{
    private static MouseControlService? _instance;
    public static MouseControlService Instance => _instance ??= new MouseControlService();

    public void Initialize()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try 
            { 
                InitializeVirtualMouse(); 
                InitializeVirtualKeyboard();
            } 
            catch (Exception ex) 
            {
                LoggingService.Instance.Warning($"[MouseControl] Linux uinput init failed: {ex.Message}");
            }
        }
    }

    public Task HandleMouseMoveAsync(byte[] payload, CancellationToken ct)
    {
        if (payload.Length >= 4)
        {
            // Use LittleEndian as per C# BitConverter defaults on most platforms, 
            // matching how the packet is typically packed.
            short dx = BitConverter.ToInt16(payload, 0);
            short dy = BitConverter.ToInt16(payload, 2);
            
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                mouse_event(0x0001, dx, dy, 0, 0);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try { SendMouseRelative(dx, dy); } catch { }
            }
        }
        return Task.CompletedTask;
    }

    public Task HandleMouseClickAsync(byte[] payload, CancellationToken ct)
    {
        if (payload.Length >= 2)
        {
            byte button = payload[0];  // 0=left, 1=right
            byte state = payload[1];   // 0=up, 1=down
            
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                uint flags = button switch
                {
                    0 => state == 1 ? 0x0002u : 0x0004u,
                    1 => state == 1 ? 0x0008u : 0x0010u,
                    _ => 0
                };
                mouse_event(flags, 0, 0, 0, 0);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try { SendMouseClick(button, state); } catch { }
            }
        }
        return Task.CompletedTask;
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);

    [DllImport("echolink", CallingConvention = CallingConvention.Cdecl)]
    private static extern int InitializeVirtualMouse();

    [DllImport("echolink", CallingConvention = CallingConvention.Cdecl)]
    private static extern int InitializeVirtualKeyboard();

    [DllImport("echolink", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SendMouseRelative(int dx, int dy);

    [DllImport("echolink", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SendMouseClick(int button, int state);
}
