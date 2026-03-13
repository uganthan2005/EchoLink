using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;
    private CancellationTokenSource? _loginCts;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "Sign in to connect to EchoLink mesh";

    /// <summary>
    /// Raised on the UI thread when authentication completes successfully.
    /// </summary>
    public event Action? LoginSucceeded;

    [RelayCommand]
    private async Task LoginWithGoogleAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusText = "Starting login...";

        _loginCts = new CancellationTokenSource();
        var ct = _loginCts.Token;

        try
        {
            TailscaleService.Instance.ResetRunningState();

            if (OperatingSystem.IsAndroid())
            {
                // Set up a background task to poll the native state and show errors
                _ = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested && IsLoading)
                    {
                        var state = await TailscaleService.Instance.GetBackendStateAsync(ct);
                        if (state == "Error")
                        {
                            var errorMsg = TailscaleService.Instance.GetAndroidNativeLastError();
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                StatusText = $"Native Bridge Error:\n{errorMsg}";
                                _log.Error($"[Login] Native error: {errorMsg}");
                                // Don't cancel immediately so user can read it, but stop loading
                            });
                        }
                        else
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                if (StatusText.StartsWith("Starting") || StatusText.StartsWith("State:"))
                                    StatusText = $"State: {state}... waiting for URL";
                            });
                        }
                        await Task.Delay(1000, ct);
                    }
                }, ct);
            }

            await TailscaleService.Instance.LoginAsync(authUrl =>
            {
                _log.Info($"[Login] Auth URL received: {authUrl}");
                OpenBrowser(authUrl);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    StatusText = "Browser opened — complete Google sign-in...");
            }, ct);

            _log.Info("[Login] 'tailscale up' succeeded — transitioning to main window.");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => LoginSucceeded?.Invoke());
        }
        catch (OperationCanceledException)
        {
            StatusText = "Login cancelled.";
        }
        catch (Exception ex)
        {
            _log.Error($"[Login] Unexpected error: {ex.Message}");
            StatusText = $"Login failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            // Primary attempt: Native shell commands are often more reliable for various DEs/environments
            if (OperatingSystem.IsWindows())
            {
                // cmd.exe /c start replaces UseShellExecute=true for published apps
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", $"/c start {url.Replace("&", "^&")}") { CreateNoWindow = true });
                return;
            }
            if (OperatingSystem.IsLinux())
            {
                System.Diagnostics.Process.Start("xdg-open", url);
                return;
            }
            if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", url);
                return;
            }

            // Fallback: Avalonia native launcher
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow);
                if (topLevel?.Launcher != null)
                {
                    _ = topLevel.Launcher.LaunchUriAsync(new Uri(url));
                    return;
                }
            }
            else if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime single && single.MainView != null)
            {
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(single.MainView);
                if (topLevel?.Launcher != null)
                {
                    _ = topLevel.Launcher.LaunchUriAsync(new Uri(url));
                    return;
                }
            }

            // Ultimate fallback
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"[Login] Failed to open browser: {ex.Message}");
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        _loginCts?.Cancel();
        _log.Info("[Login] Login cancelled by user.");
    }
}
