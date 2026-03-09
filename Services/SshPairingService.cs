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

                    // Prompt user via ViewModel callback (pass hostname for context)
                    bool accepted = await onKeyReceivedConfirmation(hostname, publicKey);

                    if (accepted)
                    {
                        await AddToAuthorizedKeysAsync(publicKey);
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

        public async Task<(bool Accepted, string? TargetUsername)> RequestPairingAsync(string targetIp, string myHostname, string myUsername)
        {
            string myPubKey = await GetMyPublicKeyAsync();

            try
            {
                using var client = new TcpClient();
                // 5 seconds timeout to connect
                var connectTask = client.ConnectAsync(targetIp, KeyExchangePort);
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) != connectTask)
                {
                    return (false, null); // Timeout
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
                
                // On Linux restrict permissions
                if (OperatingSystem.IsLinux())
                {
                    Process.Start(new ProcessStartInfo("chmod", $"600 \"{authKeysPath}\""));
                }
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