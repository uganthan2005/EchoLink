using System;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using EchoLink.Services;
using EchoLink.Services.UnifiedProtocol;
using EchoLink.ViewModels;

namespace EchoLink.Views;

public partial class RemoteControlView : UserControl
{
    private string _previousText = " ";
    private bool _isResetting = false;

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
        // When trackpad is used, focus the keyboard trap to show keyboard
        KeyboardTrap.Focus();
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

    private void KeyboardTrap_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            _ = SendControlKey(13); // VK_RETURN
            e.Handled = true;
        }
    }

    private void KeyboardTrap_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isResetting) return;

        var newText = KeyboardTrap.Text ?? "";

        // 1. Handle complete clear or empty state
        if (string.IsNullOrEmpty(newText))
        {
            // Send a backspace and reset
            _ = SendControlKey(8); // VK_BACK
            _isResetting = true;
            KeyboardTrap.Text = " ";
            KeyboardTrap.CaretIndex = 1;
            _isResetting = false;
            _previousText = " ";
            return;
        }

        // 2. Find common prefix
        int commonPrefix = 0;
        while (commonPrefix < newText.Length &&
               commonPrefix < _previousText.Length &&
               newText[commonPrefix] == _previousText[commonPrefix])
        {
            commonPrefix++;
        }

        // 3. Calculate and send deletions
        int deletions = _previousText.Length - commonPrefix;
        for (int i = 0; i < deletions; i++)
        {
            _ = SendControlKey(8); // VK_BACK
        }

        // 4. Calculate and send additions
        string charsToAdd = newText.Substring(commonPrefix);
        if (!string.IsNullOrEmpty(charsToAdd))
        {
            _ = SendTextString(charsToAdd);
        }

        // 5. Update previous text state
        _previousText = newText;

        // 6. Memory Flush: If the text box ends with a space, reset it.
        if (newText.EndsWith(" "))
        {
            _isResetting = true;
            KeyboardTrap.Text = " ";
            KeyboardTrap.CaretIndex = 1; // Move cursor to the end
            _isResetting = false;
            _previousText = " ";
        }
    }

    private async Task SendControlKey(short keyCode)
    {
        if (ViewModel?.SelectedTarget == null) return;
        try
        {
            var payload = new byte[3];
            payload[0] = 0; // Type 0 for Control Key
            BitConverter.GetBytes(keyCode).CopyTo(payload, 1);
            await UnifiedProtocolClient.Instance.SendKeyboardEventAsync(payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Log error, handle disconnection etc.
            System.Diagnostics.Debug.WriteLine($"[KEYBOARD] Failed to send control key: {ex.Message}");
        }
    }

    private async Task SendTextString(string text)
    {
        if (ViewModel?.SelectedTarget == null) return;
        try
        {
            var textBytes = Encoding.UTF8.GetBytes(text);
            var payload = new byte[textBytes.Length + 1];
            payload[0] = 1; // Type 1 for Text String
            textBytes.CopyTo(payload, 1);
            await UnifiedProtocolClient.Instance.SendKeyboardEventAsync(payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Log error, handle disconnection etc.
            System.Diagnostics.Debug.WriteLine($"[KEYBOARD] Failed to send text string: {ex.Message}");
        }
    }
}
