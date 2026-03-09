using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace EchoLink.Services;

public class SftpService
{
    private readonly LoggingService _log = LoggingService.Instance;
    private const int Socks5Port = 1055;

    /// <summary>
    /// Uploads a file to a remote Tailscale node via SFTP, tunneling through our SOCKS5 proxy.
    /// </summary>
    /// <param name="host">The Tailscale IP of the recipient.</param>
    /// <param name="username">The username on the target machine.</param>
    /// <param name="password">The password (or we could extend this for key-based auth).</param>
    /// <param name="localPath">Path to the file on this machine.</param>
    /// <param name="remotePath">Full destination path on the remote machine.</param>
    /// <param name="progressCallback">Callback for (bytesUploaded, totalBytes).</param>
    public async Task UploadFileAsync(
        string host, 
        string username, 
        string privateKeyPath,
        string localPath,
        string remotePath,
        Action<long, long> progressCallback,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var privateKeyFile = new PrivateKeyFile(privateKeyPath);
            // Configure connection to go through the Tailscale SOCKS5 proxy    
            var connectionInfo = new ConnectionInfo(
                host,
                22,
                username,
                ProxyTypes.Socks5,
                "127.0.0.1",
                Socks5Port,
                "",
                "",
                new PrivateKeyAuthenticationMethod(username, privateKeyFile));
            
            using var client = new SftpClient(connectionInfo);
            try
            {
                _log.Info($"[SFTP] Connecting to {host} via SOCKS5 proxy...");
                client.Connect();

                using var fileStream = File.OpenRead(localPath);
                long totalBytes = fileStream.Length;

                _log.Info($"[SFTP] Starting upload: {Path.GetFileName(localPath)} ({totalBytes} bytes)");

                client.UploadFile(fileStream, remotePath, (uploaded) =>
                {
                    ct.ThrowIfCancellationRequested(); // Automatically abort if cancel button clicked
                    progressCallback(checked((long)uploaded), totalBytes);
                });

                _log.Info("[SFTP] Upload completed successfully.");
            }
            catch (Exception ex)
            {
                _log.Error($"[SFTP] Upload failed: {ex.Message}");
                throw;
            }
            finally
            {
                if (client.IsConnected)
                    client.Disconnect();
            }
        }, ct);
    }

    /// <summary>
    /// Downloads a file from a remote Tailscale node via SFTP.
    /// </summary>
    public async Task DownloadFileAsync(
        string host,
        string username,
        string privateKeyPath,
        string remotePath,
        string localPath,
        Action<long, long> progressCallback,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var privateKeyFile = new PrivateKeyFile(privateKeyPath);
            var connectionInfo = new ConnectionInfo(
                host,
                22,
                username,
                ProxyTypes.Socks5,
                "127.0.0.1",
                Socks5Port,
                "",
                "",
new PrivateKeyAuthenticationMethod(username, privateKeyFile));
            
            using var client = new SftpClient(connectionInfo);
            try
            {
                client.Connect();
                
                var remoteFile = client.Get(remotePath);
                long totalBytes = remoteFile.Attributes.Size;

                using var fileStream = File.Create(localPath);
                client.DownloadFile(remotePath, fileStream, (downloaded) =>
                {
                    progressCallback(checked((long)downloaded), totalBytes);
                });
            }
            catch (Exception ex)
            {
                _log.Error($"[SFTP] Download failed: {ex.Message}");
                throw;
            }
            finally
            {
                if (client.IsConnected)
                    client.Disconnect();
            }
        }, ct);
    }
}
