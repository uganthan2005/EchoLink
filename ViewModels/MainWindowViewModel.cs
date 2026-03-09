using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty] private ViewModelBase _currentPage;
    [ObservableProperty] private string _currentPageTitle = "Dashboard";
    [ObservableProperty] private bool _isSidebarOpen = true;

    [ObservableProperty] private bool _isSshInstalling;
    [ObservableProperty] private string _sshStatusText = "";

    [ObservableProperty] private bool _isSshReady;

    [ObservableProperty] private bool _showPairingRequest;
    [ObservableProperty] private string _pairingRequestText = "";

    private System.Threading.Tasks.TaskCompletionSource<bool>? _pairingTcs;

    public DashboardViewModel     Dashboard     { get; } = new();
    public FileTransferViewModel  FileTransfer  { get; } = new();
    public ClipboardViewModel     Clipboard     { get; } = new();
    public RemoteControlViewModel RemoteControl { get; } = new();
    public DebugConsoleViewModel  DebugConsole  { get; } = new();

    /// <summary>
    /// Raised when logout completes so the hosting window can switch to LoginWindow.
    /// </summary>
    public event System.Action? LoggedOut;

    public MainWindowViewModel()
    {
        _currentPage = Dashboard;
        _ = InitializeSetupAsync();
    }

    private async System.Threading.Tasks.Task InitializeSetupAsync()
    {
        // One-time SSH Setup
        IsSshInstalling = true;
        SshStatusText = "Checking SSH Server...";
        bool isSshInstalled = await SshSetupService.IsSshServerInstalledAsync();
        if (!isSshInstalled)
        {
            SshStatusText = "Installing SSH Server (Please accept UAC prompt if asked)...";
            LoggingService.Instance.Info("SSH Server not found. Attempting to install...");
            isSshInstalled = await SshSetupService.InstallAndStartSshServerAsync();
        }
        IsSshInstalling = false;
        IsSshReady = isSshInstalled;

        // Start listening for key exchanges
        var pairingService = new SshPairingService(TailscaleService.Instance);
        await pairingService.EnsureKeyPairAsync();
        pairingService.StartListening(async (hostname, publicKey) =>
        {
            // Prompt user via UI
            return await PromptUserForPairingAsync(hostname);
        });
    }

    private async System.Threading.Tasks.Task<bool> PromptUserForPairingAsync(string hostname)
    {
        // Must be on UI thread or simply awaited since it's a notification
        // Reset TCS
        _pairingTcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        
        // Setup UI state
        PairingRequestText = $"{hostname} wants to pair for secure file transfers. Accept?";
        ShowPairingRequest = true;

        // Await user action
        bool result = await _pairingTcs.Task;
        
        // Hide UI
        ShowPairingRequest = false;
        return result;
    }

    [RelayCommand]
    private void AcceptPairing() => _pairingTcs?.TrySetResult(true);

    [RelayCommand]
    private void RejectPairing() => _pairingTcs?.TrySetResult(false);

    [RelayCommand] private void NavigateDashboard()     => Navigate(Dashboard,     "Dashboard");
    [RelayCommand] private void NavigateFileTransfer()  => Navigate(FileTransfer,  "File Transfer");
    [RelayCommand] private void NavigateClipboard()     => Navigate(Clipboard,     "Clipboard Hub");
    [RelayCommand] private void NavigateRemoteControl() => Navigate(RemoteControl, "Remote Control");
    [RelayCommand] private void NavigateDebugConsole()  => Navigate(DebugConsole,  "Debug Console");

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    [RelayCommand]
    private async System.Threading.Tasks.Task LogoutAsync()
    {
        await TailscaleService.Instance.LogoutAsync();
        LoggedOut?.Invoke();
    }

    private void Navigate(ViewModelBase vm, string title)
    {
        CurrentPage      = vm;
        CurrentPageTitle = title;
    }
}
