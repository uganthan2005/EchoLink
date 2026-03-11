using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace EchoLink.Services;

/// <summary>
/// Provides a universal mechanism to establish secure data streams over SSH Local Port Forwarding.
/// This acts as a generic transport for any internal EchoLink feature (like Clipboard, File Sync events, etc)
/// so that only Port 22 is exposed to Tailnet, and all other services bind strictly to localhost (127.0.0.1).
/// </summary>
public class SshTunnelService
{
    private static readonly Lazy<SshTunnelService> _instance = new(() => new SshTunnelService());
    public static SshTunnelService Instance => _instance.Value;

    private readonly LoggingService _log = LoggingService.Instance;
    private const int Socks5Port = 1055; // Tailscale Userspace local proxy

    private SshTunnelService() { }

    /// <summary>
    /// Establishes an SSH tunnel to the target machine and returns a fully duplex Stream connected to its local internal service.
    /// The caller is responsible for disposing of the Stream, which will automatically tear down the wrapper SSH tunnel.
    /// </summary>
    public async Task<Stream> CreateTunneledStreamAsync(
        string host,
        string username,
        string privateKeyPath,
        int remoteLocalPort,
        int sshPort = 2222,
        CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            var privateKeyFile = new PrivateKeyFile(privateKeyPath);

            // 1. Connect to peer's SSH proxy
            var connectionInfo = new ConnectionInfo(
                host,
                sshPort,
                username,
                ProxyTypes.Socks5, 
                "127.0.0.1", 
                Socks5Port, 
                "", 
                "", 
                new PrivateKeyAuthenticationMethod(username, privateKeyFile));
            
            var sshClient = new SshClient(connectionInfo);
            
            _log.Debug($"[SshTunnel] Establishing SSH connection to {host}...");
            sshClient.Connect();

            // 2. Set up a dynamic Local Port Forward mapped tightly to the peer's loopback interface
            var forwardedPort = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)remoteLocalPort);
            sshClient.AddForwardedPort(forwardedPort);
            forwardedPort.Start();

            uint boundLocalPort = forwardedPort.BoundPort;
            _log.Debug($"[SshTunnel] Tunnelling local {boundLocalPort} --> {host}:22 --> 127.0.0.1:{remoteLocalPort}");

            // 3. Connect our localized Stream to the Tunnel Head
            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync("127.0.0.1", (int)boundLocalPort, ct);
            var innerStream = tcpClient.GetStream();

            // 4. Return an active wrapper. When Stream closes, it chains tearing down in order cleanly.
            return new TunneledStream(sshClient, forwardedPort, tcpClient, innerStream);
        }, ct);
    }

    /// <summary>
    /// A robust wrapper that holds the lifetime of the underlying SshClient, Port Forward, and TcpClient
    /// so the consumer just has to treat it like a simple byte stream and `using (...)` will clean everything up.
    /// </summary>
    private class TunneledStream : Stream
    {
        private readonly SshClient _sshClient;
        private readonly ForwardedPortLocal _forwardport;
        private readonly TcpClient _tcpClient;
        private readonly NetworkStream _stream;

        public TunneledStream(SshClient ssh, ForwardedPortLocal port, TcpClient tcp, NetworkStream stream)
        {
            _sshClient = ssh;
            _forwardport = port;
            _tcpClient = tcp;
            _stream = stream;
        }

        public override bool CanRead => _stream.CanRead;
        public override bool CanSeek => _stream.CanSeek;
        public override bool CanWrite => _stream.CanWrite;
        public override long Length => _stream.Length;
        public override long Position 
        { 
            get => _stream.Position; 
            set => _stream.Position = value; 
        }

        public override void Flush() => _stream.Flush();
        public override async Task FlushAsync(CancellationToken ct) => await _stream.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => await _stream.ReadAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);
        public override void SetLength(long value) => _stream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => await _stream.WriteAsync(buffer, offset, count, ct);
        
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _stream.Dispose(); } catch { }
                try { _tcpClient.Close(); } catch { }
                try { _forwardport.Stop(); _forwardport.Dispose(); } catch { }
                try { _sshClient.Disconnect(); _sshClient.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}