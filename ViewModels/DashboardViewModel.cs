using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;

    [ObservableProperty] private bool _isMeshOnline;
    [ObservableProperty] private string _tailscaleIp = "—";
    [ObservableProperty] private string _networkName = "EchoLink-Mesh";
    [ObservableProperty] private string _statusText = "Disconnected";
    [ObservableProperty] private bool _isRefreshing;

    public ObservableCollection<Device> Devices { get; } = [];

    public DashboardViewModel()
    {
        _log.Info("Dashboard initialized.");
        _ = RefreshNetworkAsync();
    }

    [RelayCommand]
    private async Task RefreshNetworkAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        _log.Info("Refreshing network status...");
        StatusText = "Checking...";

        try
        {
            // Wait for the daemon to reach Running state by polling the CLI.
            // This ensures the daemon has fully initialised its network map.
            bool ready = await TailscaleService.Instance.WaitForDaemonRunningAsync(
                TimeSpan.FromSeconds(15));

            if (!ready)
            {
                _log.Warning("[Dashboard] Daemon not Running after 15 s — showing disconnected.");
                TailscaleIp = "—";
                IsMeshOnline = false;
                StatusText = "Disconnected";
                return;
            }

            // Fetch status via the CLI.
            // Retry a few times in case the daemon is still settling.
            string? selfIp = null;
            var devices = new System.Collections.Generic.List<Models.Device>();

            for (int attempt = 1; attempt <= 4; attempt++)
            {
                (selfIp, devices) = await TailscaleService.Instance.GetNetworkStatusAsync();

                if (selfIp != null)
                    break;

                _log.Info($"[Dashboard] Refresh attempt {attempt}/4: no data yet, retrying...");
                await Task.Delay(2000);
            }

            Devices.Clear();
            foreach (var d in devices)
                Devices.Add(d);

            if (selfIp != null)
            {
                TailscaleIp = selfIp;
                IsMeshOnline = true;
                StatusText = "Connected";
                _log.Info($"Mesh online. IP: {TailscaleIp}, {devices.Count} device(s)");
            }
            else
            {
                TailscaleIp = "—";
                IsMeshOnline = false;
                StatusText = "Disconnected";
                _log.Warning("Could not retrieve Tailscale status.");
            }
        }
        catch (Exception ex)
        {
            StatusText = "Error";
            _log.Error($"Refresh failed: {ex.Message}");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task CopyIpAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt
            && dt.MainWindow is { } window)
        {
            var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(window)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(TailscaleIp);
        }
        _log.Info($"Copied IP {TailscaleIp} to clipboard.");
    }

    [RelayCommand]
    private async Task PairDeviceAsync(Device device)
    {
        if (device == null || string.IsNullOrWhiteSpace(device.IpAddress) || device.IsSelf) 
            return;

        if (!device.IsOnline)
        {
            _log.Warning($"[Dashboard] Cannot pair because {device.Name} is currently offline.");
            return;
        }

        try
        {
            _log.Info($"[Dashboard] Requesting manual pairing with {device.IpAddress}...");
            var pairingService = new SshPairingService(TailscaleService.Instance);
            await pairingService.EnsureKeyPairAsync();
            
            var result = await pairingService.RequestPairingAsync(device.IpAddress, Environment.MachineName, Environment.UserName);
            if (result.Accepted && !string.IsNullOrWhiteSpace(result.TargetUsername))
            {
                var settingsData = SettingsService.Instance.Load();
                settingsData.PeerUsernames[device.IpAddress] = result.TargetUsername;
                SettingsService.Instance.Save(settingsData);
                
                device.IsPaired = true;
                _log.Info($"[Dashboard] Successfully paired with {device.IpAddress}");
            }
            else
            {
                _log.Warning($"[Dashboard] Pairing request rejected or timed out for {device.IpAddress}");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[Dashboard] Error pairing with {device.IpAddress}: {ex.Message}");
        }
    }
}
