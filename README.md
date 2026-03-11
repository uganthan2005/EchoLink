<div align="center">

<h1>EchoLink - Sync With Devices</h1>

Secure, SSH-based device-to-device connectivity and sharing across Windows, Linux, and Android—powered by a self-hosted Headscale tailnet.

<a href='https://github.com/uganthan2005/EchoLink'><img src='https://img.shields.io/badge/Project-Page-green'></a>
<a href='https://github.com/uganthan2005/EchoLink/issues'><img src='https://img.shields.io/badge/Contributions-Welcome-blue'></a>
<a href='https://fossunited.org/hack/2026'><img src='https://img.shields.io/badge/FOSS%20Hack-2026-red'></a>
</div>

> **TL; DR:** EchoLink connects your devices into a private mesh network and uses SSH/SFTP to enable seamless clipboard sync, file transfers, and remote actions between any two online devices—keeping your data entirely off third-party clouds.

## ✨ Overview
EchoLink is an open-source, cross-platform app designed to make device-to-device connectivity feel effortless while remaining fiercely security-first. Built with C# and Avalonia, it spins up a private tailnet and forces all communication through standard SSH and SFTP, ensuring your data travels directly and securely between your own machines. 

**Initial Features:**
- 📋 **Clipboard Sync:** Instantly push and apply your clipboard across devices.
- 📁 **File Transfer:** Send and receive files directly via SFTP.
- 💻 **Remote Actions:** Execute safe commands like locking the screen, shutting down, or restarting a PC right from your phone.

*More features and automation workflows will be added as the project evolves!*

## 📑 Todo List

- [x] Set up the Headscale control server and integrate the private mesh networking.
- [ ] Build the cross-platform UI and secure, QR-based device pairing.
- [ ] Develop the core SSH/SFTP features: clipboard sync, file transfers, and remote actions.
- [ ] Implement the Android background service to keep the mobile device reachable.
- [ ] Polish the user experience and write quickstart documentation.

## 🛠️ Developer Documentation & Crucial Technical Details

When contributing to or modifying EchoLink, please keep the following critical architectural decisions and platform-specific constraints in mind:

### 1. Android Native Mesh Node (Go + tsnet)
Android explicitly forbids applications from managing the OS routing table (
etlinkrib) without a dedicated VPN slot or root privileges. 
- EchoLink bypasses this by embedding Tailscale purely in userspace using 	ailscale.com/tsnet compiled as a C-shared library (libecholink.so).
- **Do not attempt to use 	un on Android.** The application relies strictly on userspace networking and environment variables (TS_DISABLE_LINUX_ROUTING=true, TS_ANDROID_ALLOW_UNCONFIGURED_ROUTING=true).
- **String Marshaling:** When making P/Invoke calls from C# to the Go libecholink.so via DllImport, you **MUST** specify CharSet = CharSet.Ansi. Failing to do so will result in corrupted strings being passed to Go.

### 2. SOCKS5 Proxy & Traffic Routing
Because Android runs Tailscale in pure userspace, standard sockets cannot route to the 100.x.y.z Tailscale IP addresses.
- All outbound traffic (SSH, SFTP, Pairing requests) originating from the Android C# layer **must** be routed through the local SOCKS5 proxy hosted by the Go bridge on 127.0.0.1:1055.
- Do not attempt to use direct TcpClient connections to Tailscale IPs on Android.

### 3. SSH / SFTP Implementation
- **Port 2222:** The internal SSH/SFTP server runs on port 2222 over the Tailscale interface. It uses an automatically generated RSA-2048 host key.
- **In-Memory vs File-System SFTP:** The Android Go bridge uses a real file-system-backed SFTP server (sftp.NewServer). Do not use sftp.InMemHandler(), as incoming files will be discarded.
- **Android Pathing:** Due to Android's strict file system, incoming files from PCs must be routed to the app's isolated iles/tailscale directory (accessible via GetExternalFilesDir(null)). The C# SftpService automatically intercepts and corrects remote paths to accommodate this.

### 4. Android Scoped Storage (Sending Files)
Android uses Scoped Storage (URIs like content://...) instead of physical file paths.
- **Never use TryGetLocalPath() on Android.** It will return 
ull.
- Instead, use Avalonia's IStorageFile and call OpenReadAsync() to extract a byte stream. The SftpService utilizes UploadStreamAsync to stream data directly from the URI over the SOCKS5 proxy.

### 5. The Pairing Handshake (Port 44444)
- Pairing happens completely unauthenticated over a raw TCP socket on port 44444. 
- **Inbound Bridge:** On Android, because 	snet is isolated, Go/main.go runs a forwarder that listens on the Tailscale mesh for port 44444 and forwards traffic down to the C# listener on 127.0.0.1:44444.
- **State Persistence:** When a device initiates a pairing request and it is accepted, the initiator **must** immediately save the target's username into SettingsService.Instance.Load().PeerUsernames and save it to disk. Failure to do so will result in the device showing as "Unpaired" upon the next UI refresh.

