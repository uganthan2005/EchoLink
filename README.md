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
