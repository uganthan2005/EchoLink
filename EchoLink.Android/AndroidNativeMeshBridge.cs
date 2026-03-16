using EchoLink.Services;
using System;

namespace EchoLink.Android;

public class AndroidNativeMeshBridge : INativeMeshBridge
{
    private bool _libraryLoaded = true;

    public AndroidNativeMeshBridge()
    {
        try 
        {
            // Test if we can call a simple method to verify library load
            NativeMethods.GetBackendState();
        }
        catch (DllNotFoundException ex)
        {
            Console.WriteLine($"[NativeBridge] CRITICAL: libecholink.so not found! {ex.Message}");
            _libraryLoaded = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NativeBridge] Error loading library: {ex.Message}");
            _libraryLoaded = false;
        }
    }

    public string GetBackendState() => _libraryLoaded ? NativeMethods.GetBackendState() : "LibraryLoadError";
    public string? GetTailscaleIp() => _libraryLoaded ? NativeMethods.GetTailscaleIp() : null;
    public string? GetLoginUrl() => _libraryLoaded ? NativeMethods.GetLoginUrl() : null;
    public string GetPeerListJson() => _libraryLoaded ? NativeMethods.GetPeerListJson() : "[]";
    public string? GetLastErrorMsg() => _libraryLoaded ? NativeMethods.GetLastErrorMsg() : "LibraryLoadError";
    
    public void StartNode(string configDir, string authKey, string hostname, string localIp)
    {
        if (_libraryLoaded)
            NativeMethods.StartEchoLinkNode(configDir, authKey, hostname, localIp);
    }

    public void StopNode()
    {
        if (_libraryLoaded)
            NativeMethods.StopEchoLinkNode();
    }

    public void LogoutNode()
    {
        if (_libraryLoaded)
            NativeMethods.LogoutNode();
    }

    public int InitializeVirtualMouse()
    {
        if (_libraryLoaded) return NativeMethods.InitializeVirtualMouse();
        return 0;
    }

    public void SendMouseRelative(int dx, int dy)
    {
        if (_libraryLoaded) NativeMethods.SendMouseRelative(dx, dy);
    }

    public void SendMouseClick(int button, int state)
    {
        if (_libraryLoaded) NativeMethods.SendMouseClick(button, state);
    }

    public void SendSystemAction(int actionId)
    {
        if (_libraryLoaded) NativeMethods.SendSystemAction(actionId);
    }
}
