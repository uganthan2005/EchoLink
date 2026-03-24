using Avalonia.Controls;
using Avalonia.Input;
using EchoLink.ViewModels;

namespace EchoLink.Views;

public partial class RemoteControlView : UserControl
{
    public RemoteControlView()
    {
        InitializeComponent();

        var trackpad = this.FindControl<Border>("TrackpadArea");
        if (trackpad is null) return;

        trackpad.PointerPressed  += OnPointerPressed;
        trackpad.PointerMoved    += OnPointerMoved;
        trackpad.PointerReleased += OnPointerReleased;
    }

    private RemoteControlViewModel? ViewModel => DataContext as RemoteControlViewModel;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(sender as Control);
        ViewModel?.OnPointerPressed(pos.X, pos.Y, e.Pointer.Id);
        (sender as Border)?.Focus();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(sender as Control);
        ViewModel?.OnPointerMoved(pos.X, pos.Y, e.Pointer.Id);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        ViewModel?.OnPointerReleased(e.Pointer.Id);
    }

    private bool _keepKeyboardOpen = false;

    private void ToggleKeyboard_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var input = this.FindControl<TextBox>("HiddenInput");
        if (input != null)
        {
            _keepKeyboardOpen = !_keepKeyboardOpen;
            if (_keepKeyboardOpen)
            {
                input.Focus();
                (sender as Button)!.Background = Avalonia.Media.Brush.Parse("#00E5FF");
            }
            else
            {
                this.Focus();
                (sender as Button)!.Background = Avalonia.Media.Brush.Parse("#2D2D2D");
            }
        }
    }

    private void HiddenInput_KeyDown(object? sender, KeyEventArgs e)
    {
        // Handle special keys that TextInput might miss (Backspace, Enter, Tab, etc.)
        if (e.Key is Key.Back or Key.Enter or Key.Tab or Key.Escape or Key.Delete)
        {
            ViewModel?.OnKeyDown(e.Key, e.KeyModifiers);
        }
    }

    private void HiddenInput_TextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;

        foreach (var c in e.Text)
        {
            // Map character to Key if possible
            Key key = MapCharToKey(c);
            if (key != Key.None)
            {
                ViewModel?.OnKeyDown(key, KeyModifiers.None);
            }
        }
    }

    private void HiddenInput_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_keepKeyboardOpen)
        {
            // Small delay to prevent infinite loop or focus fighting
            Task.Delay(100).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Invoke(() => (sender as TextBox)?.Focus()));
        }
    }

    private Key MapCharToKey(char c)
    {
        // Simple mapping for common characters
        if (char.IsLetter(c)) return (Key)Enum.Parse(typeof(Key), char.ToUpper(c).ToString());
        if (char.IsDigit(c)) return (Key)Enum.Parse(typeof(Key), "D" + c);
        
        return c switch
        {
            ' ' => Key.Space,
            ',' => Key.OemComma,
            '.' => Key.OemPeriod,
            ';' => Key.OemSemicolon,
            '\'' => Key.OemQuotes,
            '[' => Key.OemOpenBrackets,
            ']' => Key.OemCloseBrackets,
            '\\' => Key.OemPipe,
            '-' => Key.OemMinus,
            '=' => Key.OemPlus,
            '/' => Key.OemQuestion,
            _ => Key.None
        };
    }
}
