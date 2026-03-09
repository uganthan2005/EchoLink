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

namespace EchoLink.Services;

public class TailscaleService
{
    public static TailscaleService Instance { get; private set; } = new();

    private Process? _daemonProcess;
    private bool _stopping;
    private string _tailscaleDir = "";
    private string _socketPath = "";
    private readonly LoggingService _log = LoggingService.Instance;

    private const string HeadscaleServer = "https://echo-link.app";
    private const string HeadscaleHost = "echo-link.app";

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

    public void StartDaemon()
    {
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

        // 2. Set up a folder for Tailscale to save its data safely in the user's home folder
        string userConfigDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _tailscaleDir = Path.Combine(userConfigDir, "EchoLink", "Tailscale");
        Directory.CreateDirectory(_tailscaleDir);
        _log.Info($"[Tailscale] Data dir: {_tailscaleDir}");

        string stateFile = Path.Combine(_tailscaleDir, "tailscaled.state");

        // 3. Build the socket path.
        _socketPath = OperatingSystem.IsWindows()
            ? @"\\.\pipe\EchoLinkTailscaled"
            : Path.Combine(_tailscaleDir, "tailscaled.sock");

        _log.Info($"[Tailscale] Socket/pipe path: {_socketPath}");

        // 4. On Windows: add firewall allow-rules BEFORE starting the daemon.
        //    When an exe runs with CreateNoWindow=true, Windows Firewall silently
        //    blocks it without ever showing the "allow?" popup.
        if (OperatingSystem.IsWindows())
        {
            string cliPath = Path.Combine(appDir, "Binaries", "tailscale.exe");
            EnsureWindowsFirewallRule(binaryPath, "EchoLink tailscaled");
            EnsureWindowsFirewallRule(cliPath, "EchoLink tailscale CLI");
        }

        // 5. DNS pre-check: resolve the headscale server from C#.
        //    If this works but tailscaled still can't resolve it, the cause
        //    is firewall or process-level DNS isolation.
        RunDnsPreCheck();

        string arguments = $"--state=\"{stateFile}\" --socket=\"{_socketPath}\" --tun=userspace-networking --socks5-server=localhost:1055";

        _log.Info($"[Tailscale] Starting daemon: {binaryPath} {arguments}");

        // 6. Configure it to run invisibly in the background
        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // 7. Launch it
        try
        {
            _daemonProcess = Process.Start(startInfo)!;
            _log.Info($"[Tailscale] Daemon started (PID {_daemonProcess.Id})");

            // Pipe daemon output into the in-app log so it's visible in the debug console
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

            // Log prominently if the daemon exits unexpectedly
            _daemonProcess.EnableRaisingEvents = true;
            _daemonProcess.Exited += (_, _) =>
            {
                if (!_stopping)
                {
                    int code = -1;
                    try { code = _daemonProcess.ExitCode; } catch { }
                    _log.Error($"[Tailscale] !!! Daemon exited unexpectedly (exit code {code}) !!!");
                    _log.Error("[Tailscale] CLI commands will time out until the daemon is restarted.");
                }
            };

            // Deferred health check — just checks if the process is alive.
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

    // ── Windows firewall helper ─────────────────────────────────────────────

    private void EnsureWindowsFirewallRule(string exePath, string ruleName)
    {
        try
        {
            // Delete any stale rules first (ignore errors)
            RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");

            // Outbound — needed for DNS, HTTPS to headscale, DERP relays
            RunNetsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=out action=allow program=\"{exePath}\" enable=yes profile=any");

            // Inbound — needed for peer-to-peer WireGuard connections
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

    // ── DNS pre-check + hosts-file workaround ─────────────────────────────

    /// <summary>
    /// Resolves the headscale server from C# and, on Windows, writes the
    /// result into the system hosts file so tailscaled.exe can resolve it
    /// too.  Tailscale's Go-based DNS path on Windows is unreliable when
    /// the daemon runs with --tun=userspace-networking (it flushes the DNS
    /// cache and its internal resolver has empty forwarders).
    /// </summary>
    private void RunDnsPreCheck()
    {
        try
        {
            var addresses = Dns.GetHostAddresses(HeadscaleHost);
            if (addresses.Length == 0)
            {
                _log.Warning($"[DNS] Pre-check: {HeadscaleHost} resolved to 0 addresses.");
                return;
            }

            // Prefer IPv4
            string ip = addresses[0].ToString();
            foreach (var addr in addresses)
            {
                if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    ip = addr.ToString();
                    break;
                }
            }

            _log.Info($"[DNS] Pre-check OK: {HeadscaleHost} -> {ip}");

            if (OperatingSystem.IsWindows())
                EnsureHostsEntry(HeadscaleHost, ip);
        }
        catch (Exception ex)
        {
            _log.Error($"[DNS] Pre-check FAILED: Cannot resolve {HeadscaleHost}: {ex.Message}");
            _log.Error("[DNS] Tailscale login will likely fail. Check your internet / DNS settings.");
        }
    }

    /// <summary>
    /// Ensures the Windows hosts file contains an entry mapping
    /// <paramref name="host"/> to <paramref name="ip"/>.
    /// Requires admin (the app already runs elevated via the manifest).
    /// </summary>
    private void EnsureHostsEntry(string host, string ip)
    {
        const string hostsPath = @"C:\Windows\System32\drivers\etc\hosts";
        const string marker = "# Added by EchoLink";

        try
        {
            string content = File.Exists(hostsPath) ? File.ReadAllText(hostsPath) : "";

            // Check if there's already a correct entry
            string desiredLine = $"{ip}  {host}  {marker}";
            if (content.Contains(desiredLine))
            {
                _log.Info($"[DNS] Hosts file already has correct entry for {host}");
                return;
            }

            // Remove any stale EchoLink entries for this host
            var lines = content.Split('\n').ToList();
            lines.RemoveAll(l => l.Contains(host) && l.Contains(marker));

            // Add the new entry
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
            _log.Info("[Tailscale] Daemon stopped.");
        }
    }

    // ── CLI helpers ─────────────────────────────────────────────────────────

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

    /// <summary>
    /// Runs the bundled tailscale CLI and captures stdout + stderr.
    /// Each invocation has a hard 10-second timeout so a hung process
    /// (e.g. daemon pipe not ready) never blocks the caller forever.
    /// </summary>
    public async Task<(string Stdout, string Stderr)> RunCliAsync(
        string arguments, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        var merged = timeoutCts.Token;

        var fullArgs = PrefixSocketArg(arguments);
        var cliPath = CliPath();

        if (!File.Exists(cliPath))
        {
            _log.Error($"[CLI] Binary not found: {cliPath}");
            return ("", "binary not found");
        }

        _log.Debug($"[CLI] > {Path.GetFileName(cliPath)} {fullArgs}");

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = fullArgs,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8
        };

        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutSb.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderrSb.AppendLine(e.Data); };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            _log.Error($"[CLI] Failed to start process: {ex.Message}");
            return ("", ex.Message);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(merged);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already dead */ }

            if (ct.IsCancellationRequested)
                throw;  // propagate caller-requested cancellation

            _log.Warning($"[CLI] Timed out after 10 s: {arguments}");
            _log.Warning("[CLI] If this happens repeatedly, the daemon may not be running.");
            return ("", "timed out");
        }

        var stdout = stdoutSb.ToString();
        var stderr = stderrSb.ToString();

        if (!string.IsNullOrWhiteSpace(stderr))
            _log.Debug($"[CLI] stderr: {stderr.Trim()}");
        if (proc.ExitCode != 0)
            _log.Warning($"[CLI] Exit code {proc.ExitCode} for: {arguments}");

        return (stdout, stderr);
    }

    // ── Auth state ───────────────────────────────────────────────────────────

    public async Task<string> GetBackendStateAsync(CancellationToken ct = default)
    {
        try
        {
            var (stdout, _) = await RunCliAsync("status --json", ct);
            if (string.IsNullOrWhiteSpace(stdout)) return "Unknown";

            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("BackendState", out var state))
                return state.GetString() ?? "Unknown";

            return "Unknown";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"[Tailscale] GetBackendState error: {ex.Message}");
            return "Unknown";
        }
    }

    public async Task<string?> GetTailscaleIpAsync(CancellationToken ct = default)
    {
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
                    if (s != null && !s.Contains(':'))
                        return s;
                }
                return ips[0].GetString();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ── Network status (real device list) ───────────────────────────────────

    public async Task<(string? SelfIp, System.Collections.Generic.List<Models.Device> Devices)>
        GetNetworkStatusAsync(CancellationToken ct = default)
    {
        var devices = new System.Collections.Generic.List<Models.Device>();
        string? selfIp = null;

        try
        {
            var (stdout, _) = await RunCliAsync("status --json", ct);
            if (string.IsNullOrWhiteSpace(stdout))
                return (null, devices);

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            // Check BackendState — only parse devices if the daemon is Running
            if (root.TryGetProperty("BackendState", out var stateEl))
            {
                var state = stateEl.GetString();
                _log.Debug($"[CLI] BackendState: {state}");
                if (state != "Running")
                    return (null, devices);
            }

            if (root.TryGetProperty("Self", out var self))
            {
                selfIp = ExtractIpv4(self);
                devices.Add(ParseDevice(self, isSelf: true));
            }

            if (root.TryGetProperty("Peer", out var peers) &&
                peers.ValueKind == JsonValueKind.Object)
            {
                foreach (var peer in peers.EnumerateObject())
                    devices.Add(ParseDevice(peer.Value, isSelf: false));
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[Tailscale] Failed to parse status: {ex.Message}");
        }

        return (selfIp, devices);
    }

    private static string? ExtractIpv4(JsonElement node)
    {
        if (!node.TryGetProperty("TailscaleIPs", out var ips)) return null;
        foreach (var ip in ips.EnumerateArray())
        {
            var s = ip.GetString();
            if (s != null && !s.Contains(':'))
                return s;
        }
        return ips.GetArrayLength() > 0 ? ips[0].GetString() : null;
    }

    private static Models.Device ParseDevice(JsonElement node, bool isSelf)
    {
        string hostName = node.TryGetProperty("HostName", out var hn) ? hn.GetString() ?? "" : "";
        string os = node.TryGetProperty("OS", out var osEl) ? osEl.GetString() ?? "" : "";
        bool online = node.TryGetProperty("Online", out var onEl) && onEl.GetBoolean();
        string ip = ExtractIpv4(node) ?? "";

        string deviceType = os.ToLowerInvariant() switch
        {
            "android" or "ios" => "Phone",
            "darwin" => "Laptop",
            "linux" or "windows" => "Desktop",
            _ => "Desktop"
        };

        string lastSeen = "";
        if (node.TryGetProperty("LastSeen", out var ls) && ls.ValueKind == JsonValueKind.String)
        {
            if (DateTime.TryParse(ls.GetString(), out var dt))
                lastSeen = dt.ToLocalTime().ToString("g");
        }

        return new Models.Device
        {
            Name = hostName,
            IpAddress = ip,
            IsOnline = online,
            DeviceType = deviceType,
            Os = os,
            LastSeen = lastSeen,
            IsSelf = isSelf
        };
    }

    // ── Login ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs "tailscale up --login-server=..." to both authenticate AND bring
    /// the VPN up.  We use "up" instead of "login" because on Windows the
    /// daemon tears down the VPN when the "login" CLI exits (each CLI
    /// disconnection triggers a profile-switch restart).  "up" stays
    /// connected until the daemon reaches Running state before exiting,
    /// so when it exits with code 0 the VPN IS up.
    ///
    /// If auth is needed, "up" prints the same auth URL as "login" does.
    /// </summary>
    public async Task LoginAsync(Action<string> onAuthUrl, CancellationToken ct = default)
    {
        string cliPath = CliPath();
        string unattended = OperatingSystem.IsWindows() ? " --unattended" : "";
        string args = PrefixSocketArg($"up --login-server={HeadscaleServer}{unattended}");

        if (!File.Exists(cliPath))
        {
            _log.Error($"[Tailscale] CLI binary not found: {cliPath}");
            throw new Exception($"tailscale CLI not found at {cliPath}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8
        };

        _log.Info($"[Tailscale] Login cmd: {Path.GetFileName(cliPath)} {args}");

        bool urlOpened = false;
        var capturedOutput = new StringBuilder();

        void TryExtractUrl(string? line)
        {
            if (urlOpened || string.IsNullOrWhiteSpace(line)) return;
            int idx = line.IndexOf("https://", StringComparison.Ordinal);
            if (idx >= 0)
            {
                urlOpened = true;
                onAuthUrl(line[idx..].Trim());
            }
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            capturedOutput.AppendLine(e.Data);
            _log.Debug($"[tailscale up stdout] {e.Data}");
            TryExtractUrl(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            capturedOutput.AppendLine(e.Data);
            _log.Debug($"[tailscale up stderr] {e.Data}");
            TryExtractUrl(e.Data);
        };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            _log.Error($"[Tailscale] Failed to start 'tailscale up': {ex.Message}");
            throw;
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _log.Info("[Tailscale] 'tailscale up' started — waiting for auth URL or Running state...");

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            _log.Info("[Tailscale] 'tailscale up' cancelled — killing process.");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return;
        }

        int exitCode = proc.ExitCode;
        _log.Info($"[Tailscale] 'tailscale up' exited (code {exitCode})");

        if (exitCode != 0)
        {
            string detail = capturedOutput.ToString().Trim();
            string msg = string.IsNullOrWhiteSpace(detail)
                ? $"tailscale up exited with code {exitCode}"
                : detail;
            _log.Error($"[Tailscale] 'tailscale up' failed: {msg}");
            throw new Exception(msg);
        }

        _log.Info("[Tailscale] 'tailscale up' completed successfully (daemon reached Running).");
    }

    /// <summary>
    /// Attempts to bring the daemon to Running state by running
    /// "tailscale up --login-server=...".  Used at app startup to restore
    /// a previously authenticated session.
    ///
    /// Returns true if the daemon reached Running state before the timeout.
    /// Returns false if auth is required (the command prints an auth URL
    /// and waits indefinitely) or the daemon didn't come up in time.
    /// </summary>
    public async Task<bool> TryBringUpAsync(TimeSpan timeout)
    {
        string cliPath = CliPath();
        if (!File.Exists(cliPath))
        {
            _log.Error($"[Tailscale] CLI binary not found for TryBringUp: {cliPath}");
            return false;
        }

        string unattended = OperatingSystem.IsWindows() ? " --unattended" : "";
        string args = PrefixSocketArg($"up --login-server={HeadscaleServer}{unattended}");

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8
        };

        _log.Info($"[Tailscale] TryBringUp: {Path.GetFileName(cliPath)} {args}");

        bool authUrlSeen = false;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            _log.Debug($"[TryBringUp stdout] {e.Data}");
            if (e.Data.Contains("https://"))
                authUrlSeen = true;
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            _log.Debug($"[TryBringUp stderr] {e.Data}");
            if (e.Data.Contains("https://"))
                authUrlSeen = true;
        };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            _log.Error($"[Tailscale] TryBringUp failed to start: {ex.Message}");
            return false;
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeout);

        try
        {
            // Poll: either the process exits, an auth URL appears, or we time out.
            while (!proc.HasExited && !authUrlSeen)
            {
                await Task.Delay(500, cts.Token);
            }

            if (authUrlSeen)
            {
                // Auth needed — user must go through the login flow.
                _log.Info("[Tailscale] TryBringUp: auth URL seen → needs login.");
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            // Process exited on its own — check exit code.
            if (proc.ExitCode == 0)
            {
                _log.Info("[Tailscale] TryBringUp: 'tailscale up' exited with code 0 → Running.");
                return true;
            }

            _log.Warning($"[Tailscale] TryBringUp: 'tailscale up' exited with code {proc.ExitCode}.");
            return false;
        }
        catch (OperationCanceledException)
        {
            // Timeout
            _log.Warning("[Tailscale] TryBringUp: timed out.");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return false;
        }
    }

    // ── Logout ───────────────────────────────────────────────────────────────

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        _log.Info("[Tailscale] Logging out...");
        var (_, stderr) = await RunCliAsync("logout", ct);
        if (!string.IsNullOrWhiteSpace(stderr))
            _log.Warning($"[tailscale logout] {stderr.Trim()}");
        _log.Info("[Tailscale] Logged out.");
    }
}
