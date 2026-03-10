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
                // Check if sshd service exists AND is currently Running, and configuration is patched
                var serviceCheck = await RunCommandAsync("powershell", "-NoProfile -Command \"$s = Get-Service -Name sshd -ErrorAction SilentlyContinue; if ($null -eq $s) { exit 1 } if ($s.Status -ne 'Running') { exit 1 } exit 0\"");
                bool isRunning = serviceCheck.ExitCode == 0;

                var configCheck = await RunCommandAsync("powershell", "-NoProfile -Command \"$c = Get-Content $env:ProgramData\\ssh\\sshd_config -ErrorAction SilentlyContinue; if ($c -match '^Match Group administrators') { exit 1 } else { exit 0 }\"");
                bool isConfigPatched = configCheck.ExitCode == 0;

                return isRunning && isConfigPatched;
            }
            else if (OperatingSystem.IsMacOS())
            {
                // On macOS, OpenSSH is built-in. We just need to check if Remote Login is turned on.
                var result = await RunCommandAsync("systemsetup", "-getremotelogin");
                return result.StandardOutput.Contains("On", StringComparison.OrdinalIgnoreCase);
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
                string binDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Binaries", "OpenSSH");
                string installerScriptPath = Path.Combine(binDir, "install-sshd.ps1");

                // Requires admin privileges.
                string script = $@"
try {{
    if (Test-Path '{installerScriptPath}') {{
        & '{installerScriptPath}'
    }} else {{
        Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0 -ErrorAction SilentlyContinue
    }}

    Set-Service -Name sshd -StartupType 'Automatic'
    Start-Service sshd -ErrorAction SilentlyContinue

    # Give sshd time to generate keys and default config if first run
    Start-Sleep -Seconds 2

    # Patch sshd_config so Administrators use ~/.ssh/authorized_keys
    $sshd_config = ""$env:ProgramData\ssh\sshd_config""
    if (Test-Path $sshd_config) {{
        $content = Get-Content $sshd_config
        $modified = $false
        
        $newContent = foreach ($line in $content) {{
            if ($line -match '^Match Group administrators') {{
                '#Match Group administrators'
                $modified = $true
            }} elseif ($line -match '^\s*AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys') {{
                '#       AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys'
                $modified = $true
            }} else {{
                $line
            }}
        }}
        
        if ($modified) {{
            $newContent | Set-Content $sshd_config
            Restart-Service sshd -ErrorAction SilentlyContinue
        }}
    }}

    # Open Firewall
    if (!(Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue | Select-Object -First 1)) {{
        New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22
    }}
}} catch {{
    exit 1
}}
exit 0
";
                string tmpScriptFile = Path.Combine(Path.GetTempPath(), "install_sshd.ps1");
                await File.WriteAllTextAsync(tmpScriptFile, script);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-WindowStyle Hidden -ExecutionPolicy Bypass -File \"{tmpScriptFile}\"",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
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
            else if (OperatingSystem.IsMacOS())
            {
                // macOS requires elevating to enable Remote Login (SSH)
                var psi = new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = "-e 'do shell script \"systemsetup -f -setremotelogin on\" with administrator privileges'",
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
