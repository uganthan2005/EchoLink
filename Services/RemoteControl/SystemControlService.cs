using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace EchoLink.Services;

public class SystemControlService
{
    private static SystemControlService? _instance;
    public static SystemControlService Instance => _instance ??= new SystemControlService();

    private readonly LoggingService _log = LoggingService.Instance;

    public async Task HandleSystemActionAsync(byte[] payload, CancellationToken ct)
    {
        if (payload.Length >= 1)
        {
            byte actionId = payload[0];  // 0=Lock, 1=Restart, 2=Shutdown
            _log.Info($"[SystemControl] Received system action: {actionId}");
            
            if (actionId == 0)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    LockWorkStation();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    await RunCommandAsync("loginctl", "lock-sessions");
                }
            }
            else if (actionId == 1)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    await RunCommandAsync("shutdown", "/r /t 0");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // Try sudo first, if it fails due to password, we can't do much here 
                    // unless setup was already performed.
                    await RunCommandAsync("sudo", "systemctl reboot");
                }
            }
            else if (actionId == 2)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    await RunCommandAsync("shutdown", "/s /t 0");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    await RunCommandAsync("sudo", "systemctl poweroff");
                }
            }
        }
    }

    public async Task<bool> IsLinuxPowerSetupDoneAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return true;

        // Check if we can run systemctl reboot without a password
        var result = await RunCommandAsync("sudo", "-n -l /usr/bin/systemctl reboot");
        if (result == 0) return true;

        // Some distros might have systemctl in /bin
        result = await RunCommandAsync("sudo", "-n -l /bin/systemctl reboot");
        return result == 0;
    }

    public async Task<bool> IsLinuxInputSetupDoneAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return true;

        // Check if udev rule exists and user is in input group
        if (!File.Exists("/etc/udev/rules.d/99-echolink-uinput.rules")) return false;

        var groupsResult = await RunCommandWithOutputAsync("groups", "");
        return groupsResult.Contains("input");
    }

    public async Task<bool> SetupLinuxInputAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return true;

        _log.Info("[SystemControl] Starting Linux input setup via pkexec...");

        string user = Environment.UserName;
        string udevContent = "KERNEL==\"uinput\", GROUP=\"input\", MODE=\"0660\"";
        string udevFile = "/etc/udev/rules.d/99-echolink-uinput.rules";

        // Create udev rule and add user to input group
        string command = $"bash -c \"echo '{udevContent}' > {udevFile} && udevadm control --reload-rules && udevadm trigger && usermod -aG input {user}\"";
        
        var psi = new ProcessStartInfo
        {
            FileName = "pkexec",
            Arguments = command,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync();
                bool success = process.ExitCode == 0;
                _log.Info($"[SystemControl] Input setup finished with exit code: {process.ExitCode}");
                return success;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[SystemControl] Input setup failed: {ex.Message}");
        }

        return false;
    }

    private async Task<string> RunCommandWithOutputAsync(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process != null)
            {
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                return output;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[SystemControl] Failed to run with output {fileName} {arguments}: {ex.Message}");
        }
        return string.Empty;
    }

    public async Task<bool> SetupLinuxPowerActionsAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return true;

        _log.Info("[SystemControl] Starting Linux power actions setup via pkexec...");

        // Find systemctl path
        string systemctlPath = "/usr/bin/systemctl";
        if (!File.Exists(systemctlPath)) systemctlPath = "/bin/systemctl";

        string user = Environment.UserName;
        string sudoersContent = $"{user} ALL=(ALL) NOPASSWD: {systemctlPath} reboot, {systemctlPath} poweroff";
        string sudoersFile = "/etc/sudoers.d/echolink-power";

        // Use pkexec to write the file with root privileges. This will trigger a GUI password prompt.
        string command = $"bash -c \"echo '{sudoersContent}' > {sudoersFile} && chmod 440 {sudoersFile}\"";
        
        var psi = new ProcessStartInfo
        {
            FileName = "pkexec",
            Arguments = command,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync();
                bool success = process.ExitCode == 0;
                _log.Info($"[SystemControl] Setup finished with exit code: {process.ExitCode}");
                return success;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[SystemControl] Setup failed: {ex.Message}");
        }

        return false;
    }

    private async Task<int> RunCommandAsync(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync();
                return process.ExitCode;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[SystemControl] Failed to run {fileName} {arguments}: {ex.Message}");
        }
        return -1;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();
}