using Avalonia;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace EchoLink.Linux;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        EchoLink.Services.AudioStreamingService.Instance.RuntimeBridge = new LinuxAudioRuntimeBridge();
        
        // Ensure virtual mic is setup on Linux at startup
        EnsureVirtualMic();
        
        // Ensure uinput permissions for trackpad
        EnsureUinputPermissions();

        BuildAvaloniaApp()
            .WithDeveloperTools()
            .StartWithClassicDesktopLifetime(args);
    }

    private static void EnsureVirtualMic()
    {
        try
        {
            // PRIORITY 1: The 'Scripts' folder in the application directory (standard for published apps)
            string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "setup_linux_virtual_mic.sh");
            
            // PRIORITY 2: The 'Scripts' folder relative to source (for dotnet run/debug)
            if (!File.Exists(scriptPath))
            {
                scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Scripts", "setup_linux_virtual_mic.sh");
            }

            // PRIORITY 3: Just check the current directory if all else fails
            if (!File.Exists(scriptPath))
            {
                scriptPath = "Scripts/setup_linux_virtual_mic.sh";
            }

            if (File.Exists(scriptPath))
            {
                Console.WriteLine($"[Linux Startup] Running virtual mic setup: {scriptPath}");
                var processInfo = new ProcessStartInfo
                {
                    FileName = "bash",
                    Arguments = $"\"{scriptPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                process?.WaitForExit();
                
                string output = process?.StandardOutput.ReadToEnd() ?? "";
                string error = process?.StandardError.ReadToEnd() ?? "";
                if (!string.IsNullOrEmpty(output)) Console.WriteLine(output);
                if (!string.IsNullOrEmpty(error)) Console.WriteLine($"[Linux Startup] Script Error: {error}");

                // If we performed setup, give PulseAudio/PipeWire a second to settle 
                // and for ALSA bridges to see the new devices before the bridge starts.
                if (output.Contains("Creating") || output.Contains("Unloading"))
                {
                    Console.WriteLine("[Linux Startup] Devices modified. Waiting 1s for system to settle...");
                    Thread.Sleep(1000);
                }
            }
            else
            {
                Console.WriteLine($"[Linux Startup] setup_linux_virtual_mic.sh NOT FOUND. Looked in standard locations.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Linux Startup] Failed to ensure virtual mic: {ex.Message}");
        }
    }

    private static void EnsureUinputPermissions()
    {
        try
        {
            string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "setup_linux_uinput.sh");
            if (!File.Exists(scriptPath))
            {
                scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Scripts", "setup_linux_uinput.sh");
            }

            if (File.Exists(scriptPath))
            {
                Console.WriteLine($"[Linux Startup] Running uinput setup: {scriptPath}");
                var processInfo = new ProcessStartInfo
                {
                    FileName = "bash",
                    Arguments = $"\"{scriptPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                process?.WaitForExit();
                
                string output = process?.StandardOutput.ReadToEnd() ?? "";
                string error = process?.StandardError.ReadToEnd() ?? "";
                if (!string.IsNullOrEmpty(output)) Console.WriteLine(output);
                if (!string.IsNullOrEmpty(error)) Console.WriteLine($"[Linux Startup] Uinput Script Error: {error}");
            }
            else
            {
                 Console.WriteLine($"[Linux Startup] setup_linux_uinput.sh NOT FOUND.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Linux Startup] Failed to ensure uinput: {ex.Message}");
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
