using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using EchoLink.ViewModels;
using EchoLink.Views;
using EchoLink.Services;

namespace EchoLink;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Start the tailscale daemon
        TailscaleService.Instance.StartDaemon();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();

            // Show a temporary empty window while we check auth state
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            // Hook cleanup
            desktop.Exit += async (_, _) =>
            {
                await ClipboardSyncService.Instance.StopAsync();
                TailscaleService.Instance.StopDaemon();
            };

            // Check auth state asynchronously, then show the right window
            _ = ShowStartupWindowAsync(desktop);
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            DisableAvaloniaDataAnnotationValidation();
            singleView.MainView = new Views.MainView
            {
                DataContext = new MainWindowViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task ShowStartupWindowAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var log = EchoLink.Services.LoggingService.Instance;
        log.Info("[Startup] Waiting for tailscaled to be ready...");

        // Give the daemon a moment to start and load its state file.
        await Task.Delay(2000);

        // Try to bring the daemon to Running state. If the daemon has saved
        // credentials from a previous session, "tailscale up" will set
        // WantRunning=true and the daemon will auto-connect without requiring
        // a fresh login. If auth is needed, TryBringUpAsync detects the auth
        // URL and returns false so we can show the login window.
        log.Info("[Startup] Running 'tailscale up' to restore connection...");
        bool running = await TailscaleService.Instance.TryBringUpAsync(
            TimeSpan.FromSeconds(15));

        string decision = running ? "Running → MainWindow" : "not Running → LoginWindow";
        log.Info($"[Startup] Daemon {decision}");

        if (running)
        {
            // Already authenticated — go straight to main window
            var mainWindow = new MainWindow { DataContext = new MainWindowViewModel() };
            desktop.MainWindow = mainWindow;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;
            mainWindow.Show();
        }
        else
        {
            // Need to login
            var loginWindow = new LoginWindow { DataContext = new LoginViewModel() };
            desktop.MainWindow = loginWindow;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;
            loginWindow.Show();
        }
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}