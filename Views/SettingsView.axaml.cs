using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EchoLink.Models;

namespace EchoLink.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void HotkeyTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.Tag is not HotkeyBinding binding) return;

        // Ignore lone modifier presses
        if (e.Key is Key.LeftShift or Key.RightShift
            or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin)
            return;

        var parts = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))     parts.Add("Alt");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))   parts.Add("Shift");
        parts.Add(e.Key.ToString());

        binding.KeyGesture = string.Join("+", parts);
        e.Handled = true;
    }

    private void HotkeyTextBox_GotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is TextBox tb)
            tb.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFFFFF"));
    }

    private void HotkeyTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#00E5FF"));
    }
}
