using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EchoLink.Services
{
    public class SshPairingService
    {
        private const int KeyExchangePort = 44444; // Fixed port for key exchange over Tailscale
        private readonly TailscaleService _tailscaleService;
        private CancellationTokenSource? _listeningCts;

        // Echolink specific SSH keys
        private readonly string _sshDir;
        public string PrivateKeyPath { get; }
        public string PublicKeyPath { get; }

        public SshPairingService(TailscaleService tailscaleService)
        {
            _tailscaleService = tailscaleService;

            string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _sshDir = Path.Combine(homeDir, ".ssh");
            
            PrivateKeyPath = Path.Combine(_sshDir, "echolink_ed25519");
            PublicKeyPath = PrivateKeyPath + ".pub";
        }

        public async Task EnsureKeyPairAsync()
        {
            if (!Directory.Exists(_sshDir))
            {
                Directory.CreateDirectory(_sshDir);
            }

            // Secure the .ssh directory itself on Windows so sshd doesn't reject it
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("icacls", $"\"{_sshDir}\" /inheritance:r /q") { CreateNoWindow = true })?.WaitForExit();
                Process.Start(new ProcessStartInfo("icacls", $"\"{_sshDir}\" /grant SYSTEM:(F) /q") { CreateNoWindow = true })?.WaitForExit();
                Process.Start(new ProcessStartInfo("icacls", $"\"{_sshDir}\" /grant \"{Environment.UserName}:(F)\" /q") { CreateNoWindow = true })?.WaitForExit();
            }

            if (!File.Exists(PrivateKeyPath) || !File.Exists(PublicKeyPath))
            {
                // Generate a new ed25519 keypair without password via standard ssh-keygen
                var psi = new ProcessStartInfo
                {
                    FileName = "ssh-keygen",
                    Arguments = $"-t ed25519 -f \"{PrivateKeyPath}\" -N \"\" -q",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                }
            }
        }

        public async Task<string> GetMyPublicKeyAsync()
        {
            await EnsureKeyPairAsync();
            return await File.ReadAllTextAsync(PublicKeyPath);
        }

        /// <summary>
        /// Call this on App Startup if we have Tailscale IP available
        /// </summary>
        public void StartListening(Func<string, string, Task<bool>> onKeyReceivedConfirmation)
        {
            _listeningCts?.Cancel();
            _listeningCts = new CancellationTokenSource();
            _ = AcceptConnectionsAsync(onKeyReceivedConfirmation, _listeningCts.Token);
        }

        public void StopListening()
        {
            _listeningCts?.Cancel();
        }

        private async Task AcceptConnectionsAsync(Func<string, string, Task<bool>> onKeyReceivedConfirmation, CancellationToken cancellationToken)
        {
            try
            {
                // Ensure tailscale is up and we have an IP
                var myIp = await _tailscaleService.GetTailscaleIpAsync(cancellationToken);
                if (string.IsNullOrEmpty(myIp)) return;

                // Listen on all IPs including Tailscale
                var listener = new TcpListener(IPAddress.Any, KeyExchangePort);
                listener.Start();

                // Stop listener when cancelled
                cancellationToken.Register(() => listener.Stop());

                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cancellationToken);
                    _ = HandleClientConnectionAsync(client, onKeyReceivedConfirmation);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }

        private async Task HandleClientConnectionAsync(TcpClient client, Func<string, string, Task<bool>> onKeyReceivedConfirmation)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
            {
                try
                {
                    string incomingPayload = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(incomingPayload)) return;

                    // Payload should be: "HOSTNAME|||USERNAME|||PUBLIC_KEY"
                    var parts = incomingPayload.Split("|||");
                    if (parts.Length != 3) return;

                    string hostname = parts[0];
                    string remoteUsername = parts[1];
                    string publicKey = parts[2].Trim();

                    if (!publicKey.StartsWith("ssh-ed25519") && !publicKey.StartsWith("ssh-rsa"))
                    {
                        await writer.WriteLineAsync("REJECTED: Invalid key format");
                        return;
                    }

                    // 1. Silent Check: Is this key already trusted?
                    bool alreadyPaired = await IsKeyAlreadyAuthorizedAsync(publicKey);

                    bool accepted = alreadyPaired;

                    if (!alreadyPaired)
                    {
                        // 2. Prompt user via ViewModel callback ONLY if we don't already trust this key
                        accepted = await onKeyReceivedConfirmation(hostname, publicKey);
                    }

                    if (accepted)
                    {
                        if (!alreadyPaired)
                        {
                            await AddToAuthorizedKeysAsync(publicKey);
                        }
                        
                        // Reply with our OS username so the sender knows who to SSH as
                        await writer.WriteLineAsync($"ACCEPTED|||{Environment.UserName}");
                    }
                    else
                    {
                        await writer.WriteLineAsync("REJECTED: User declined");
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Instance.Error($"Key exchange error: {ex.Message}");
                }
            }
        }

        public async Task<bool> IsKeyAlreadyAuthorizedAsync(string publicKey)
        {
            string authKeysPath = Path.Combine(_sshDir, "authorized_keys");
            if (!File.Exists(authKeysPath)) return false;

            var lines = await File.ReadAllLinesAsync(authKeysPath);
            foreach (var line in lines)
            {
                if (line.Contains(publicKey))
                {
                    return true;
                }
            }
            return false;
        }

        private async Task<TcpClient?> ConnectViaSocks5Async(string targetIp, int port, CancellationToken ct)
        {
            var client = new TcpClient();
            try
            {
                // Connect to the local SOCKS5 proxy provided by Tailscale
                await client.ConnectAsync("127.0.0.1", 1055, ct);
                var stream = client.GetStream();

                // 1. SOCKS5 Greeting
                await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
                byte[] response1 = new byte[2];
                int read = await stream.ReadAsync(response1, ct);
                if (read != 2 || response1[1] != 0x00) return null;

                // 2. SOCKS5 Connection Request
                byte[] destIpBytes = IPAddress.Parse(targetIp).GetAddressBytes();
                byte[] portBytes = BitConverter.GetBytes((short)port);
                if (BitConverter.IsLittleEndian) Array.Reverse(portBytes);

                byte[] request = new byte[6 + destIpBytes.Length];
                request[0] = 0x05; // Version
                request[1] = 0x01; // CONNECT
                request[2] = 0x00; // RSV
                request[3] = 0x01; // IPv4
                destIpBytes.CopyTo(request, 4);
                portBytes.CopyTo(request, 4 + destIpBytes.Length);

                await stream.WriteAsync(request, ct);

                byte[] response2 = new byte[10];
                read = await stream.ReadAsync(response2, ct);
                if (response2[1] != 0x00) return null; // Success = 0x00

                return client;
            }
            catch
            {
                client.Dispose();
                return null;
            }
        }

        public async Task<(bool Accepted, string? TargetUsername)> RequestPairingAsync(string targetIp, string myHostname, string myUsername)
        {
            string myPubKey = await GetMyPublicKeyAsync();

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                
                // Route through SOCKS5 proxy since we are running userspace tailscale
                using var client = await ConnectViaSocks5Async(targetIp, KeyExchangePort, cts.Token);
                
                if (client == null || !client.Connected)
                {
                    LoggingService.Instance.Error($"SOCKS5 proxy rejected connection to {targetIp}:{KeyExchangePort}");
                    return (false, null);
                }

                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8);

                await writer.WriteLineAsync($"{myHostname}|||{myUsername}|||{myPubKey}");

                string? response = await reader.ReadLineAsync();

                if (response != null && response.StartsWith("ACCEPTED|||"))
                {
                    string targetUser = response.Substring("ACCEPTED|||".Length).Trim();
                    return (true, targetUser);
                }
                
                if (response == null)
                    LoggingService.Instance.Warning($"[Pairing] Connection closed by remote host {targetIp}");
                else
                    LoggingService.Instance.Warning($"[Pairing] Remote responded: {response}");

                return (false, null);
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Error($"Failed to pair with {targetIp}: {ex.Message}");
                return (false, null);
            }
        }

        private async Task AddToAuthorizedKeysAsync(string publicKey)
        {
            string authKeysPath = Path.Combine(_sshDir, "authorized_keys");

            // Ensure file exists
            if (!File.Exists(authKeysPath))
            {
                await File.WriteAllTextAsync(authKeysPath, "");
            }

            // Always enforce permissions on every key addition to ensure OpenSSH doesn't reject it
            if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo("chmod", $"600 \"{authKeysPath}\""));
            }
            else if (OperatingSystem.IsWindows())
            {
                // Unblock files if created by downloading or bad inheritance
                Process.Start(new ProcessStartInfo("icacls", $"\"{authKeysPath}\" /reset /q") { CreateNoWindow = true })?.WaitForExit();
                Process.Start(new ProcessStartInfo("icacls", $"\"{authKeysPath}\" /inheritance:r /q") { CreateNoWindow = true })?.WaitForExit();
                Process.Start(new ProcessStartInfo("icacls", $"\"{authKeysPath}\" /grant SYSTEM:(F) /q") { CreateNoWindow = true })?.WaitForExit();
                Process.Start(new ProcessStartInfo("icacls", $"\"{authKeysPath}\" /grant \"{Environment.UserName}:(F)\" /q") { CreateNoWindow = true })?.WaitForExit();
            }

            // Check if key already exists to prevent duplicates
            var existingKeys = await File.ReadAllLinesAsync(authKeysPath);
            foreach (var key in existingKeys)
            {
                if (key.Trim() == publicKey) return; // Already paired
            }

            // Append with a newline ensuring there's separation
            using (var sw = File.AppendText(authKeysPath))
            {
                await sw.WriteLineAsync();
                await sw.WriteLineAsync(publicKey);
            }
        }
    }
}