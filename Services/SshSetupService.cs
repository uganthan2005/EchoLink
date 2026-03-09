using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace EchoLink.Services
{
    public class SshSetupService
    {
        public static async Task<bool> IsSshServerInstalledAsync()
        {
            if (OperatingSystem.IsWindows())
            {
                // Check if sshd service exists
                var result = await RunCommandAsync("powershell", "-NoProfile -Command \"Get-Service -Name sshd -ErrorAction SilentlyContinue\"");
                return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
            }
            else if (OperatingSystem.IsLinux())
            {
                // Check if sshd is running or installed
                var result = await RunCommandAsync("systemctl", "status sshd");
                if (result.ExitCode == 0) return true;

                result = await RunCommandAsync("systemctl", "status ssh");
                return result.ExitCode == 0;
            }

            return false;
        }

        public static async Task<bool> InstallAndStartSshServerAsync()
        {
            if (OperatingSystem.IsWindows())
            {
                // Requires admin privileges.
                // We'll execute a powershell script with 'runas' verb to elevate
                string script = @"
try {
    Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0 -ErrorAction Stop
    Set-Service -Name sshd -StartupType 'Automatic'
    Start-Service sshd
    # Open Firewall
    if (!(Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue | Select-Object -First 1)) {
        New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22
    }
} catch {
    exit 1
}
exit 0
";
                string tmpScriptFile = Path.Combine(Path.GetTempPath(), "install_sshd.ps1");
                await File.WriteAllTextAsync(tmpScriptFile, script);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-ExecutionPolicy Bypass -File \"{tmpScriptFile}\"",
                    UseShellExecute = true,
                    Verb = "runas" // Elevate
                };

                try
                {
                    var process = Process.Start(psi);
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                        return process.ExitCode == 0;
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // User declined UAC
                    return false;
                }
                finally
                {
                    if (File.Exists(tmpScriptFile))
                        File.Delete(tmpScriptFile);
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                // We are going to use pkexec or sudo to elevate
                var psi = new ProcessStartInfo
                {
                    FileName = "pkexec",
                    Arguments = "bash -c \"apt-get update && apt-get install -y openssh-server && systemctl enable --now ssh\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                try
                {
                    var process = Process.Start(psi);
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                        return process.ExitCode == 0;
                    }
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunCommandAsync(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return (-1, "", "");

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();
                
                string stdout = await stdoutTask;
                string stderr = await stderrTask;

                return (process.ExitCode, stdout, stderr);
            }
            catch
            {
                return (-1, "", "");
            }
        }
    }
}
