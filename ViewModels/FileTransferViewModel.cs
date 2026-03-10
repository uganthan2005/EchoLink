using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class FileTransferViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;
    private readonly SftpService _sftp = new();

    [ObservableProperty] private Device? _selectedTarget;
    [ObservableProperty] private string _selectedFileName = string.Empty;
    [ObservableProperty] private double _uploadProgress;
    [ObservableProperty] private bool _isUploading;
    [ObservableProperty] private string _statusText = "Drop a file or click to browse";
    [ObservableProperty] private bool _isDropZoneActive;

    private CancellationTokenSource? _uploadCts;

    public ObservableCollection<Device> OnlineDevices { get; } = new();

    public FileTransferViewModel()
    {
        _ = LoadDevicesAsync();
    }

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        try
        {
            var (_, devices) = await TailscaleService.Instance.GetNetworkStatusAsync();
            OnlineDevices.Clear();
            foreach (var device in devices)
            {
                if (device.IsOnline && !device.IsSelf)
                {
                    OnlineDevices.Add(device);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[FileTransfer] Failed to load devices: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task BrowseFileAsync()
    {
        _log.Info("Opening file picker...");
        // File dialog will be opened from the code-behind; VM handles the rest via SetFile().
        await Task.CompletedTask;
    }

    [ObservableProperty] private string _selectedFilePath = string.Empty;
    [ObservableProperty] private bool _hasFileSelected;

    public void SetFile(string filePath)
    {
        SelectedFilePath = filePath;
        SelectedFileName = System.IO.Path.GetFileName(filePath);
        HasFileSelected = true;
        _log.Info($"File selected: {SelectedFileName}");
        StatusText = "File ready to send. Click Send.";
    }

    [RelayCommand]
    public async Task SendFileAsync()
    {
        if (SelectedTarget is null)
        {
            StatusText = "Please select a target device first.";
            return;
        }

        if (string.IsNullOrEmpty(SelectedFilePath))
        {
            StatusText = "Please select a file first.";
            return;
        }

        await PerformSftpUploadAsync(SelectedFilePath);
    }

    private async Task PerformSftpUploadAsync(string filePath)
    {
        if (SelectedTarget == null) return;

        IsUploading = true;
        UploadProgress = 0;
        var fileName = System.IO.Path.GetFileName(filePath);
        
        _uploadCts = new CancellationTokenSource();
        var ct = _uploadCts.Token;

        _log.Info($"[SFTP] Preparing upload of '{fileName}' → {SelectedTarget.IpAddress}");
        StatusText = $"Connecting to {SelectedTarget.Name}...";

        try
        {
            // With Tailscale SSH, the host accepts the connection regardless of the password 
            // as long as the Headscale ACL allows it!
            string username = Environment.UserName;
            
            var pairingService = new SshPairingService(TailscaleService.Instance);
            await pairingService.EnsureKeyPairAsync();
            string privateKeyPath = pairingService.PrivateKeyPath;
            
            // Try to pair silently (or prompt the other side)
            _log.Info($"[SFTP] Requesting pairing with {SelectedTarget.IpAddress}...");
            var pairingResult = await pairingService.RequestPairingAsync(SelectedTarget.IpAddress, Environment.MachineName, Environment.UserName);
            
            string targetUsername = pairingResult.TargetUsername ?? "root"; // fallback

            if (pairingResult.Accepted && !string.IsNullOrWhiteSpace(pairingResult.TargetUsername))
            {
                // Save it for background services like ClipboardSync that need to SSH silently
                var settingsData = SettingsService.Instance.Load();
                settingsData.PeerUsernames[SelectedTarget.IpAddress] = pairingResult.TargetUsername;
                SettingsService.Instance.Save(settingsData);
            }

            if (!pairingResult.Accepted)
            {
                _log.Warning("[SFTP] Pairing rejected or timed out. SFTP connection may fail if not already authorized.");
            }

            // Just pass the filename, let the SftpService resolve the remote OS folder dynamically
            string remotePath = fileName; 

            await _sftp.UploadFileAsync(
                SelectedTarget.IpAddress,
                targetUsername,
                privateKeyPath,
                filePath,
                remotePath,
                (uploaded, total) =>
                {
                    double progress = (total == 0) ? 0 : ((double)uploaded / total * 100);
                    // Marshaling property changes to UI thread implicitly handled by Avalonia/ObservableProperty but good practice
                    UploadProgress = progress;
                    StatusText = $"Uploading {fileName}... {progress:F1}%";
                }, ct);

            StatusText = $"✔ '{fileName}' sent to {SelectedTarget.Name}";
            _log.Info($"[SFTP] Upload complete: {fileName}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "❌ Upload cancelled.";
            _log.Warning($"[SFTP] Upload cancelled: {fileName}");
        }
        catch (Exception ex)
        {
            StatusText = $"❌ Failed: {ex.Message}";
            _log.Error($"[SFTP] Upload error: {ex.Message}");
        }
        finally
        {
            IsUploading = false;
            _uploadCts?.Dispose();
            _uploadCts = null;
        }
    }

    private async Task SimulateUploadAsync(string filePath)
    {
        IsUploading    = true;
        UploadProgress = 0;
        var fileName   = System.IO.Path.GetFileName(filePath);

        _log.Info($"Starting upload of '{fileName}' → {SelectedTarget?.Name}");

        for (int i = 1; i <= 100; i++)
        {
            UploadProgress = i;
            StatusText     = $"Uploading {fileName}… {i}%";
            await Task.Delay(30);
        }

        StatusText   = $"✔ '{fileName}' sent to {SelectedTarget!.Name}";
        IsUploading  = false;
        _log.Info($"Upload complete: {fileName}");
    }

    [RelayCommand]
    private void CancelUpload()
    {
        if (IsUploading && _uploadCts != null)
        {
            _log.Info("Cancelling upload...");
            _uploadCts.Cancel();
        }
        else
        {
            IsUploading  = false;
            StatusText   = "Upload cancelled.";
            UploadProgress = 0;
            _log.Warning("Upload cancelled by user.");
        }
    }
}
