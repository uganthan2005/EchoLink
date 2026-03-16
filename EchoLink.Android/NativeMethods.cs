using System;
using System.Runtime.InteropServices;

namespace EchoLink.Android;

public static class NativeMethods
{
    private const string LibraryName = "echolink"; // libecholink.so

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int StartEchoLinkNode(string configDir, string authKey, string hostname, string localIp);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void StopEchoLinkNode();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.LPStr)]
    public static extern string GetBackendState();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.LPStr)]
    public static extern string GetTailscaleIp();
    
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.LPStr)]
    public static extern string GetLoginUrl();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.LPStr)]
    public static extern string GetPeerListJson();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.LPStr)]
    public static extern string GetLastErrorMsg();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void LogoutNode();

    // Remote Control
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int InitializeVirtualMouse();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SendMouseRelative(int dx, int dy);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SendMouseClick(int button, int state);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SendSystemAction(int actionId);
}
