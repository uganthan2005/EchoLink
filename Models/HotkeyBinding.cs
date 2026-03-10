using CommunityToolkit.Mvvm.ComponentModel;

namespace EchoLink.Models;

public partial class HotkeyBinding : ObservableObject
{
    [ObservableProperty] private string _actionName = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _keyGesture = "";
    [ObservableProperty] private bool _isEnabled = true;

    public HotkeyBinding() { }

    public HotkeyBinding(string actionName, string displayName, string defaultGesture)
    {
        _actionName = actionName;
        _displayName = displayName;
        _keyGesture = defaultGesture;
    }
}
