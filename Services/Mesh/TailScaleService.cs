using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace EchoLink.Services;

public class TailscaleService
{
    public static TailscaleService Instance { get; private set; } = new();

    private Process? _daemonProcess;
    private bool _stopping;
    private string _tailscaleDir = "";
    private string _socketPath = "";
    private readonly LoggingService _log = LoggingService.Instance;

    private const string HeadscaleServer = "https://control.echo-link.app";
    private const string HeadscaleHost = "control.echo-link.app";

    public INativeMeshBridge? NativeBridge { get; set; }

    /// <summary>
    /// Waits for the daemon to reach Running state by polling the CLI.
    /// </summary>
    public async Task<bool> WaitForDaemonRunningAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var state = await GetBackendStateAsync(ct);
            if (state == "Running")
                return true;
            await Task.Delay(1000, ct);
        }
        return false;
    }

    /// <summary>
    /// Resets the Running flag conceptually, though it's now polled directly.
    /// </summary>
    public void ResetRunningState() { }

    private void KillExistingDaemons()
    {
        try
        {
            var processes = Process.GetProcessesByName(OperatingSystem.IsWindows() ? "tailscaled" : "tailscaled");
            foreach (var p in processes)
            {
                if (p.Id == Environment.ProcessId) continue;
                
                try 
                {
                    _log.Info($"[Tailscale] Attempting to kill orphaned daemon process (PID {p.Id})");
                    p.Kill(); 
                } 
                catch 
                { 
                    // Ignore processes we don't have permission to kill (e.g. system-wide tailscaled)
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"[Tailscale] Failed to enumerate existing daemons: {ex.Message}");
        }
    }

    public bool IsEphemeralSession { get; private set; }

    public async Task StartDaemonAsync(string authKey, bool isEphemeral, CancellationToken ct = default)
    {
        IsEphemeralSession = isEphemeral;

        if (OperatingSystem.IsAndroid())
        {
            _log.Info($"[Tailscale] Android detected. Requesting daemon start via native bridge. Ephemeral={isEphemeral}");
            string userConfigDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string tailscaleDir = Path.Combine(userConfigDir, "EchoLink", "Tailscale");
            
            // Start the node
            NativeBridge?.StartNode(tailscaleDir, authKey, "android-device", "127.0.0.1", isEphemeral);
            
            // WAIT for the node to reach a definitive state
            _log.Info("[Tailscale] Waiting for Android node to reach Running or NeedsLogin state...");
            for (int i = 0; i < 30; i++) // 15 second timeout
            {
                var state = NativeBridge?.GetBackendState();
                if (state == "Running")
                {
                    _log.Info("[Tailscale] Android node started successfully (Running).");
                    return;
                }
                if (state == "NeedsLogin")
                {
                    // If we provided an auth key and it's still asking for login, 
                    // it means the key was likely rejected or ignored.
                    _log.Warning("[Tailscale] Android node reached NeedsLogin. Auth key may have been invalid.");
                    return; 
                }
                if (state == "Error")
                {
                    var err = NativeBridge?.GetLastErrorMsg();
                    throw new Exception($"Native node failed to start: {err}");
                }
                
                await Task.Delay(500, ct);
            }
            
            _log.Warning("[Tailscale] Timeout waiting for Android node state transition.");
            return;
        }

        KillExistingDaemons();

        _log.Info($"[Tailscale] OS: {Environment.OSVersion} | IsWindows={OperatingSystem.IsWindows()}");
        _log.Info($"[Tailscale] AppBase: {AppDomain.CurrentDomain.BaseDirectory}");

        // 1. Locate the bundled binary dynamically (name differs by OS)
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string binaryName = OperatingSystem.IsWindows() ? "tailscaled.exe" : "tailscaled";
        string binaryPath = Path.Combine(appDir, "Binaries", binaryName);

        if (!File.Exists(binaryPath))
        {
            _log.Error($"[Tailscale] Daemon binary NOT FOUND at: {binaryPath}");
            _log.Error("[Tailscale] Cannot proceed. Make sure the Binaries/ folder is present.");
            return;
        }

        _log.Info($"[Tailscale] Daemon binary found: {binaryPath}");

        // 2. Set up a folder for Tailscale to save its data
        string userConfigDirRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (isEphemeral)
        {
            _tailscaleDir = Path.Combine(Path.GetTempPath(), "EchoLink", "Tailscale_Temp_" + Guid.NewGuid().ToString("N"));
        }
        else
        {
            _tailscaleDir = Path.Combine(userConfigDirRoot, "EchoLink", "Tailscale");
        }
        Directory.CreateDirectory(_tailscaleDir);
        _log.Info($"[Tailscale] Data dir: {_tailscaleDir}");

        string stateFile = Path.Combine(_tailscaleDir, "tailscaled.state");

        // 3. Build the socket path.
        _socketPath = OperatingSystem.IsWindows()
            ? @"\\.\pipe\EchoLinkTailscaled"
            : Path.Combine(_tailscaleDir, "tailscaled.sock");

        _log.Info($"[Tailscale] Socket/pipe path: {_socketPath}");

        // 4. On Windows: add firewall allow-rules BEFORE starting the daemon.
        if (OperatingSystem.IsWindows())
        {
            string cliPath = Path.Combine(appDir, "Binaries", "tailscale.exe");
            EnsureWindowsFirewallRule(binaryPath, "EchoLink tailscaled");
            EnsureWindowsFirewallRule(cliPath, "EchoLink tailscale CLI");
        }

        // 5. DNS pre-check
        RunDnsPreCheck();

        string arguments = $"--state=\"{stateFile}\" --socket=\"{_socketPath}\" --tun=userspace-networking --socks5-server=localhost:1055";

        _log.Info($"[Tailscale] Starting daemon: {binaryPath} {arguments}");

        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            _daemonProcess = Process.Start(startInfo)!;
            _log.Info($"[Tailscale] Daemon started (PID {_daemonProcess.Id})");

            _daemonProcess.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _log.Debug($"[tailscaled stdout] {e.Data}");
            };
            _daemonProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    _log.Warning($"[tailscaled stderr] {e.Data}");
                }
            };
            _daemonProcess.BeginOutputReadLine();
            _daemonProcess.BeginErrorReadLine();

            _daemonProcess.EnableRaisingEvents = true;
            _daemonProcess.Exited += (_, _) =>
            {
                if (!_stopping)
                {
                    int code = -1;
                    try { code = _daemonProcess.ExitCode; } catch { }
                    _log.Error($"[Tailscale] !!! Daemon exited unexpectedly (exit code {code}) !!!");
                }
            };

            // Only pass --authkey and --force-reauth if a non-empty authKey is provided
            string cliPathCommand = CliPath();
            string unattended = OperatingSystem.IsWindows() ? " --unattended" : "";
            string upArgs;
            if (!string.IsNullOrEmpty(authKey))
            {
                upArgs = PrefixSocketArg($"up --login-server={HeadscaleServer} --authkey=\"{authKey}\" --force-reauth{unattended}");
            }
            else
            {
                upArgs = PrefixSocketArg($"up --login-server={HeadscaleServer}{unattended}");
            }
            var upPsi = new ProcessStartInfo
            {
                FileName = cliPathCommand,
                Arguments = upArgs,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _log.Info($"[Tailscale] Running up command: {upPsi.FileName} {upPsi.Arguments}");
            var upProcess = Process.Start(upPsi);
            if (upProcess != null) await upProcess.WaitForExitAsync(ct);

            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                if (_daemonProcess.HasExited)
                {
                    _log.Error($"[Tailscale] Daemon died within 3 s of launch (exit code {_daemonProcess.ExitCode}).");
                    return;
                }
                _log.Info($"[Tailscale] Daemon still alive after 3 s.");
            });
        }
        catch (Exception ex)
        {
            _log.Error($"[Tailscale] Failed to start daemon: {ex}");
        }
    }

    private void EnsureWindowsFirewallRule(string exePath, string ruleName)
    {
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
            RunNetsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=out action=allow program=\"{exePath}\" enable=yes profile=any");
            RunNetsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow program=\"{exePath}\" enable=yes profile=any");
            _log.Info($"[Firewall] Added allow rules for {Path.GetFileName(exePath)}");
        }
        catch (Exception ex)
        {
            _log.Warning($"[Firewall] Could not add rules for {Path.GetFileName(exePath)}: {ex.Message}");
        }
    }

    private void RunNetsh(string arguments)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        proc.WaitForExit(5000);
    }

    private void RunDnsPreCheck()
    {
        try
        {
            var addresses = Dns.GetHostAddresses(HeadscaleHost);
            if (addresses.Length == 0) return;

            string ip = addresses.First(a => a.AddressFamily == AddressFamily.InterNetwork).ToString();
            _log.Info($"[DNS] Pre-check OK: {HeadscaleHost} -> {ip}");

            if (OperatingSystem.IsWindows())
                EnsureHostsEntry(HeadscaleHost, ip);
        }
        catch (Exception ex)
        {
            _log.Error($"[DNS] Pre-check FAILED: {ex.Message}");
        }
    }

    private void EnsureHostsEntry(string host, string ip)
    {
        const string hostsPath = @"C:\Windows\System32\drivers\etc\hosts";
        const string marker = "# Added by EchoLink";
        try
        {
            string content = File.Exists(hostsPath) ? File.ReadAllText(hostsPath) : "";
            string desiredLine = $"{ip}  {host}  {marker}";
            if (content.Contains(desiredLine)) return;

            var lines = content.Split('\n').ToList();
            lines.RemoveAll(l => l.Contains(host) && l.Contains(marker));
            lines.Add(desiredLine);
            File.WriteAllText(hostsPath, string.Join('\n', lines));
            _log.Info($"[DNS] Wrote hosts entry: {ip}  {host}");
        }
        catch (Exception ex)
        {
            _log.Warning($"[DNS] Could not update hosts file: {ex.Message}");
        }
    }

    public void StopDaemon()
    {
        if (OperatingSystem.IsAndroid()) return;

        if (_daemonProcess != null && !_daemonProcess.HasExited)
        {
            _stopping = true;
            _log.Info("[Tailscale] Stopping daemon...");
            try
            {
                _daemonProcess.Kill(entireProcessTree: true);
                _daemonProcess.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warning($"[Tailscale] Error stopping daemon: {ex.Message}");
            }
        }
    }

    private string CliPath()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string name = OperatingSystem.IsWindows() ? "tailscale.exe" : "tailscale";
        return Path.Combine(appDir, "Binaries", name);
    }

    private string PrefixSocketArg(string arguments)
    {
        if (!string.IsNullOrEmpty(_socketPath))
            return $"--socket=\"{_socketPath}\" {arguments}";
        return arguments;
    }

    public async Task<(string Stdout, string Stderr)> RunCliAsync(
        string arguments, CancellationToken ct = default)
    {
        if (OperatingSystem.IsAndroid()) return ("", "Not supported on Android");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        var merged = timeoutCts.Token;

        var fullArgs = PrefixSocketArg(arguments);
        var cliPath = CliPath();

        if (!File.Exists(cliPath)) return ("", "binary not found");

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = fullArgs,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutSb.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderrSb.AppendLine(e.Data); };

        try { proc.Start(); } catch (Exception ex) { return ("", ex.Message); }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try { await proc.WaitForExitAsync(merged); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return ("", "timed out");
        }

        return (stdoutSb.ToString(), stderrSb.ToString());
    }

    public async Task<string> GetBackendStateAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsAndroid())
        {
            var state = await Task.Run(() => GetAndroidNativeState());
            _log.Debug($"[Tailscale] Android Native State: {state}");
            return state;
        }

        try
        {
            var (stdout, _) = await RunCliAsync("status --json", ct);
            if (string.IsNullOrWhiteSpace(stdout)) return "Unknown";

            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("BackendState", out var state))
                return state.GetString() ?? "Unknown";
            return "Unknown";
        }
        catch { return "Unknown"; }
    }

    private string GetAndroidNativeState()
    {
        return NativeBridge?.GetBackendState() ?? "Unknown";
    }

    public async Task<string?> GetTailscaleIpAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsAndroid())
        {
             return await Task.Run(() => GetAndroidNativeIp());
        }

        try
        {
            var (stdout, _) = await RunCliAsync("status --json", ct);
            if (string.IsNullOrWhiteSpace(stdout)) return null;

            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("Self", out var self) &&
                self.TryGetProperty("TailscaleIPs", out var ips) &&
                ips.GetArrayLength() > 0)
            {
                foreach (var ip in ips.EnumerateArray())
                {
                    var s = ip.GetString();
                    if (s != null && !s.Contains(':')) return s;
                }
                return ips[0].GetString();
            }
            return null;
        }
        catch { return null; }
    }

    private string? GetAndroidNativeIp()
    {
        return NativeBridge?.GetTailscaleIp();
    }

    public async Task<string?> GetCurrentAccountIdAsync(CancellationToken ct = default)
    {
        try
        {
            var (stdout, _) = await RunCliAsync("status --json", ct);
            if (string.IsNullOrWhiteSpace(stdout))
                return null;

            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("Self", out var self))
                return null;

            if (self.TryGetProperty("UserID", out var userId))
            {
                var value = userId.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            if (self.TryGetProperty("DNSName", out var dnsName))
            {
                var value = dnsName.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<(string? SelfIp, System.Collections.Generic.List<Models.Device> Devices)>
        GetNetworkStatusAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsAndroid())
        {
            var androidDevices = new System.Collections.Generic.List<Models.Device>();
            try
            {
                var json = NativeBridge?.GetPeerListJson();
                var settingsData = SettingsService.Instance.Load();
                
                if (!string.IsNullOrEmpty(json))
                {
                    var peerDevices = JsonSerializer.Deserialize<System.Collections.Generic.List<Models.Device>>(json, 
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (peerDevices != null)
                    {
                        foreach (var peer in peerDevices)
                        {
                            peer.IsPaired = peer.IsSelf || settingsData.PeerUsernames.ContainsKey(peer.IpAddress);
                            androidDevices.Add(peer);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[Tailscale] Android peer parse failed: {ex.Message}");
            }
            return (GetAndroidNativeIp(), androidDevices);
        }

        var devices = new System.Collections.Generic.List<Models.Device>();
        string? selfIp = null;

        try
        {
            var (stdout, _) = await RunCliAsync("status --json", ct);
            if (string.IsNullOrWhiteSpace(stdout))
                return (null, devices);

            var settingsData = SettingsService.Instance.Load();

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            if (root.TryGetProperty("BackendState", out var stateEl) && stateEl.GetString() != "Running")
                return (null, devices);

            if (root.TryGetProperty("Self", out var self))
            {
                selfIp = ExtractIpv4(self);
                devices.Add(ParseDevice(self, isSelf: true, settingsData));
            }

            if (root.TryGetProperty("Peer", out var peers) && peers.ValueKind == JsonValueKind.Object)
            {
                foreach (var peer in peers.EnumerateObject())
                    devices.Add(ParseDevice(peer.Value, isSelf: false, settingsData));
            }
        }
        catch (Exception ex) { _log.Error($"[Tailscale] Failed to parse status: {ex.Message}"); }

        return (selfIp, devices);
    }

    private static string? ExtractIpv4(JsonElement node)
    {
        if (!node.TryGetProperty("TailscaleIPs", out var ips)) return null;
        foreach (var ip in ips.EnumerateArray())
        {
            var s = ip.GetString();
            if (s != null && !s.Contains(':')) return s;
        }
        return ips.GetArrayLength() > 0 ? ips[0].GetString() : null;
    }

    private static Models.Device ParseDevice(JsonElement node, bool isSelf, SettingsData settingsData)
    {
        string hostName = node.TryGetProperty("HostName", out var hn) ? hn.GetString() ?? "" : "";
        string os = node.TryGetProperty("OS", out var osEl) ? osEl.GetString() ?? "" : "";
        bool online = node.TryGetProperty("Online", out var onEl) && onEl.GetBoolean();
        string ip = ExtractIpv4(node) ?? "";
        string userId = node.TryGetProperty("UserID", out var uid) ? uid.ValueKind == JsonValueKind.Number ? uid.GetInt64().ToString() : uid.GetString() ?? "" : "";
        
        var tags = new List<string>();
        if (node.TryGetProperty("Tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tagsEl.EnumerateArray())
            {
                string? s = t.GetString();
                if (s != null) tags.Add(s);
            }
        }

        string deviceType = os.ToLowerInvariant() switch
        {
            "android" or "ios" => "Phone",
            "darwin" => "Laptop",
            "linux" or "windows" => "Desktop",
            _ => "Desktop"
        };

        string lastSeen = "";
        if (node.TryGetProperty("LastSeen", out var ls) && DateTime.TryParse(ls.GetString(), out var dt))
        {
            if (!online && (DateTime.UtcNow - dt).TotalMinutes <= 10)
            {
                online = true;
            }
            lastSeen = dt.ToLocalTime().ToString("g");
        }

        bool isPaired = isSelf || (settingsData.PeerUsernames != null && settingsData.PeerUsernames.ContainsKey(ip));

        return new Models.Device
        {
            Name = hostName,
            IpAddress = ip,
            IsOnline = online,
            DeviceType = deviceType,
            Os = os,
            LastSeen = lastSeen,
            IsSelf = isSelf,
            IsPaired = isPaired,
            UserId = userId,
            Tags = tags
        };
    }
public async Task LoginAsync(Action<string> onAuthUrl, CancellationToken ct = default)
{
    if (OperatingSystem.IsAndroid())
    {
        // On Android, we poll the native bridge for the URL.
        // We also try to bring the node up to force tsnet to generate the URL if missing.
        _log.Info("[Tailscale] Android Login: Waiting for URL from native bridge...");

        // Trigger a bring-up in the background to kickstart URL generation
        _ = TryBringUpAsync(TimeSpan.FromSeconds(5));

        bool urlSent = false;
        while (!ct.IsCancellationRequested)
        {
            var state = await GetBackendStateAsync(ct);
            if (state == "Running")
            {
                _log.Info("[Tailscale] Android Login: Node is Running. Authentication complete.");
                return;
            }

            if (!urlSent)
            {
                var url = GetAndroidNativeLoginUrl();
                if (!string.IsNullOrEmpty(url))
                {
                    _log.Info($"[Tailscale] Android Login: URL captured: {url}");
                    onAuthUrl(url);
                    urlSent = true;
                }
            }
            await Task.Delay(1000, ct);
        }
        return;
    }

    string cliPath = CliPath();
    string unattended = OperatingSystem.IsWindows() ? " --unattended" : "";
    string args = PrefixSocketArg($"up --login-server={HeadscaleServer} --force-reauth{unattended}");
    if (!File.Exists(cliPath)) throw new Exception($"tailscale CLI not found at {cliPath}");

    var psi = new ProcessStartInfo
    {
        FileName = cliPath,
        Arguments = args,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    bool urlOpened = false;
    var capturedOutput = new StringBuilder();

    using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
    proc.OutputDataReceived += (_, e) =>
    {
        if (e.Data == null) return;
        capturedOutput.AppendLine(e.Data);
        if (!urlOpened && e.Data.Contains("https://"))
        {
            int idx = e.Data.IndexOf("https://");
            string part = e.Data[idx..].Trim();
            // Extract only the URL (until next space)
            int spaceIdx = part.IndexOf(' ');
            if (spaceIdx > 0) part = part[..spaceIdx];

            if (part.Contains("control.echo-link.app/a/") || part.Contains("control.echo-link.app/register/")) // Ensure it's an auth URL, not just the server URL
            {
                urlOpened = true;
                onAuthUrl(part);
            }        }
    };
    proc.ErrorDataReceived += (_, e) =>
    {
        if (e.Data == null) return;
        capturedOutput.AppendLine(e.Data);
        if (!urlOpened && e.Data.Contains("https://"))
        {
            int idx = e.Data.IndexOf("https://");
            string part = e.Data[idx..].Trim();
            int spaceIdx = part.IndexOf(' ');
            if (spaceIdx > 0) part = part[..spaceIdx];

            if (part.Contains("control.echo-link.app/a/") || part.Contains("control.echo-link.app/register/"))
            {
                urlOpened = true;
                onAuthUrl(part);
            }
        }
    };

    proc.Start();
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();

    await proc.WaitForExitAsync(ct);
    if (proc.ExitCode != 0) throw new Exception(capturedOutput.ToString());
}

    private string? GetAndroidNativeLoginUrl()
    {
        return NativeBridge?.GetLoginUrl();
    }

    public string? GetAndroidNativeLastError()
    {
        return NativeBridge?.GetLastErrorMsg();
    }

    public async Task<bool> TryBringUpAsync(TimeSpan timeout)
    {
        if (OperatingSystem.IsAndroid())
        {
            return await WaitForDaemonRunningAsync(timeout);
        }

        string cliPath = CliPath();
        if (!File.Exists(cliPath)) return false;

        string unattended = OperatingSystem.IsWindows() ? " --unattended" : "";
        string args = PrefixSocketArg($"up --login-server={HeadscaleServer}{unattended}");

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        bool authUrlSeen = false;
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data?.Contains("https://") == true) authUrlSeen = true; };
        proc.ErrorDataReceived += (_, e) => { if (e.Data?.Contains("https://") == true) authUrlSeen = true; };

        try { proc.Start(); } catch { return false; }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (!proc.HasExited && !authUrlSeen) await Task.Delay(500, cts.Token);
            return !authUrlSeen && proc.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// No-op — port 44444 is already configured by <see cref="ExposeLocalPortsAsync"/>.
    /// Kept to avoid breaking call-sites compiled against the old signature.
    /// </summary>
    public Task ExposeClipboardPortAsync(CancellationToken ct = default)
    {
        _log.Debug("[Tailscale] ExposeClipboardPortAsync: clipboard port (44444) was already set up by ExposeLocalPortsAsync — skipping duplicate serve call.");
        return Task.CompletedTask;
    }

    public async Task ExposeLocalPortsAsync(CancellationToken ct = default)
    {
        _log.Info("[Tailscale] Setting up port forwarding (SSH=22, Pairing=44444)...");

        // We strictly expose port 22 (for all SSH payloads), Port 44444 (for unauthenticated Key-Pairing) and Port 55555 (Unified Protocol).
        // Clipboard and all future stream services now ride inside the encrypted SSH stream natively!
        foreach (var (port, label) in new (int, string)[] { (22, "SSH"), (44444, "Pairing"), (55555, "Unified") })
        {
            var (stdout, stderr) = await RunCliAsync($"serve --bg --tcp={port} tcp://127.0.0.1:{port}", ct);
            if (!string.IsNullOrWhiteSpace(stdout))
                _log.Debug($"[Tailscale] Serve {label} stdout: {stdout.Trim()}");
            if (!string.IsNullOrWhiteSpace(stderr))
                _log.Warning($"[Tailscale] Serve {label} error: {stderr.Trim()}");
        }

        // Verify what Tailscale is actually forwarding so any misconfiguration shows up
        // in the Debug Console immediately.
        var (status, _) = await RunCliAsync("serve status", ct);
        if (!string.IsNullOrWhiteSpace(status))
            _log.Info($"[Tailscale] Active serve config:\n{status.Trim()}");
        else
            _log.Warning("[Tailscale] 'tailscale serve status' returned no output — ports may NOT be exposed to peers. " +
                         "Check that 'tailscale serve' is supported on this platform/version.");
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var settings = SettingsService.Instance.Load();
        settings.IsLoggedIn = false;
        SettingsService.Instance.Save(settings);

        if (OperatingSystem.IsAndroid())
        {
            _log.Info("[Tailscale] Android Logout: Calling native bridge logout...");
            NativeBridge?.LogoutNode();
            return;
        }
        await RunCliAsync("logout", ct);
    }
}
