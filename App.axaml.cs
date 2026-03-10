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
using System;

namespace EchoLink;

public partial class App : Application
{
    private readonly LoggingService _log = LoggingService.Instance;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Start the tailscale daemon/service
        TailscaleService.Instance.StartDaemon();
        DisableAvaloniaDataAnnotationValidation();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            // Hook cleanup
            desktop.Exit += async (_, _) =>
            {
                await ClipboardSyncService.Instance.StopAsync();
                TailscaleService.Instance.StopDaemon();
            };

            // Check auth state asynchronously, then show the right window
            _ = InitializeAppAsync(desktop);
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // Show loading view immediately to avoid blank white screen
            singleView.MainView = new LoadingView();
            _ = InitializeAppAsync(singleView);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeAppAsync(object lifetime)
    {
        _log.Info("[Startup] Initializing application...");
        
        // Give the service/daemon time to initialize
        await Task.Delay(2000);

        _log.Info("[Startup] Checking connection status...");
        bool running = await TailscaleService.Instance.TryBringUpAsync(TimeSpan.FromSeconds(10));
        string state = await TailscaleService.Instance.GetBackendStateAsync();
        
        _log.Info($"[Startup] Running={running}, State={state}");

        // For Android, we want to be sure it's really "Running"
        if (state == "Running")
        {
            _log.Info("[Startup] Authenticated. Opening Dashboard.");
            NavigateToMain(lifetime);
        }
        else
        {
            _log.Info("[Startup] Not authenticated or transition needed. Opening Login.");
            NavigateToLogin(lifetime);
        }
    }

    private void NavigateToMain(object lifetime)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            var vm = new MainWindowViewModel();
            if (lifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var win = new MainWindow { DataContext = vm };
                desktop.MainWindow = win;
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;
                win.Show();
            }
            else if (lifetime is ISingleViewApplicationLifetime singleView)
            {
                singleView.MainView = new MainView { DataContext = vm };
            }
        });
    }

    private void NavigateToLogin(object lifetime)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            var vm = new LoginViewModel();
            vm.LoginSucceeded += () => NavigateToMain(lifetime);

            if (lifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var win = new LoginWindow { DataContext = vm };
                desktop.MainWindow = win;
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;
                win.Show();
            }
            else if (lifetime is ISingleViewApplicationLifetime singleView)
            {
                singleView.MainView = new LoginView { DataContext = vm };
            }
        });
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
