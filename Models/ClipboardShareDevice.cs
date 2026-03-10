using CommunityToolkit.Mvvm.ComponentModel;

namespace EchoLink.Models;

public partial class ClipboardShareDevice : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _ipAddress = string.Empty;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isSelf;

    public string OnlineLabel => IsOnline ? "Online" : "Offline";

    partial void OnIsOnlineChanged(bool value)
    {
        OnPropertyChanged(nameof(OnlineLabel));
    }
}
