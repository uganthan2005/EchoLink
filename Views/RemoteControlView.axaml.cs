using Avalonia.Controls;
using Avalonia.Input;
using EchoLink.ViewModels;

namespace EchoLink.Views;

public partial class RemoteControlView : UserControl
{
    public RemoteControlView()
    {
        InitializeComponent();
    }

    private RemoteControlViewModel? ViewModel => DataContext as RemoteControlViewModel;

    private void Trackpad_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(sender as Control);
        ViewModel?.OnPointerPressed(pos.X, pos.Y);
        (sender as Border)?.Focus();
    }

    private void Trackpad_PointerMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(sender as Control);
        ViewModel?.OnPointerMoved(pos.X, pos.Y);
    }

    private void Trackpad_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        ViewModel?.OnPointerReleased();
    }

    private async void MouseButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        byte button = (sender as Control)?.Name == "LeftBtn" ? (byte)0 : (byte)1;
        if (ViewModel != null)
        {
            await ViewModel.SetMouseButtonState(button, true);
        }
    }

    private async void MouseButton_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        byte button = (sender as Control)?.Name == "LeftBtn" ? (byte)0 : (byte)1;
        if (ViewModel != null)
        {
            await ViewModel.SetMouseButtonState(button, false);
        }
    }
}
