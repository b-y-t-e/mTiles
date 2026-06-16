using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;
using MTerminal.ViewModels;
using XTerm.Options;

namespace MTerminal.Views;

public partial class TerminalPaneView : UserControl
{
    private TerminalView? _terminalView;
    private string? _lastSelectedText;
    private FieldInfo? _ptyField;
    private bool _keyHandlerRegistered;

    public TerminalPaneView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not TerminalPaneViewModel vm) return;

        if (vm.CachedControl is TerminalControl cached)
        {
            ControlHelper.DetachFromParent(cached);
            Content = cached;
            HookTerminalView(cached);
            return;
        }

        var theme = vm.Theme;
        var terminal = new TerminalControl
        {
            Process = string.Empty,
            FontFamily = new FontFamily(vm.FontFamily),
            FontSize = vm.FontSize,
            BufferSize = 5000,
            Background = new SolidColorBrush(Color.Parse(theme.Background)),
            Foreground = new SolidColorBrush(Color.Parse(theme.Foreground)),
            Options = new TerminalOptions
            {
                Theme = new ThemeOptions
                {
                    Foreground = theme.Foreground,
                    Background = theme.Background,
                    Cursor = theme.Cursor,
                    Selection = theme.Selection,
                    Black = theme.Black,
                    Red = theme.Red,
                    Green = theme.Green,
                    Yellow = theme.Yellow,
                    Blue = theme.Blue,
                    Magenta = theme.Magenta,
                    Cyan = theme.Cyan,
                    White = theme.White,
                    BrightBlack = theme.BrightBlack,
                    BrightRed = theme.BrightRed,
                    BrightGreen = theme.BrightGreen,
                    BrightYellow = theme.BrightYellow,
                    BrightBlue = theme.BrightBlue,
                    BrightMagenta = theme.BrightMagenta,
                    BrightCyan = theme.BrightCyan,
                    BrightWhite = theme.BrightWhite
                }
            }
        };

        vm.CachedControl = terminal;
        Content = terminal;

        AttachedToVisualTree += OnceAttached;

        async void OnceAttached(object? s, VisualTreeAttachmentEventArgs args)
        {
            AttachedToVisualTree -= OnceAttached;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

            HookTerminalView(terminal);

            if (!vm.IsLaunched)
            {
                vm.IsLaunched = true;
                await terminal.LaunchProcess(vm.WorkingDirectory, vm.Shell.ExecutablePath, vm.Shell.Args);
            }
        }
    }

    private void HookTerminalView(TerminalControl tc)
    {
        var tv = tc.GetVisualDescendants().OfType<TerminalView>().FirstOrDefault();
        if (tv == null || tv == _terminalView) return;
        _terminalView = tv;
        _ptyField = typeof(TerminalView).GetField("_ptyConnection", BindingFlags.NonPublic | BindingFlags.Instance);

        tv.AddHandler(
            InputElement.PointerReleasedEvent,
            (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _lastSelectedText = tv.Terminal?.Selection?.HasSelection == true
                        ? tv.Terminal.Selection.GetSelectionText()
                        : null;
                }, DispatcherPriority.Background);
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        if (!_keyHandlerRegistered)
        {
            this.AddHandler(InputElement.KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
            _keyHandlerRegistered = true;
        }
    }

    private async void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        try
        {
            if (_terminalView == null) return;

            // Ctrl+V: paste from clipboard
            if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
            {
                e.Handled = true;
                await _terminalView.PasteAsync();
            }
            // Alt+<key>: fix TerminalView bug (sends raw char instead of ESC+char).
            // Write ESC+char directly to PTY stream, then suppress TerminalView's handler.
            else if (e.KeyModifiers == KeyModifiers.Alt && TryGetChar(e, out var ch))
            {
                e.Handled = true;
                var pty = _ptyField?.GetValue(_terminalView);
                if (pty != null)
                {
                    // Write ESC+char synchronously then null pty to suppress class handler
                    var stream = pty.GetType().GetProperty("WriterStream")?.GetValue(pty) as System.IO.Stream;
                    if (stream != null)
                    {
                        var bytes = Encoding.UTF8.GetBytes($"\x1b{ch}");
                        stream.Write(bytes);
                        stream.Flush();
                    }

                    _ptyField.SetValue(_terminalView, null);
                    Dispatcher.UIThread.Post(() => _ptyField.SetValue(_terminalView, pty),
                        DispatcherPriority.Input);
                }
            }
            // Ctrl+C with pre-captured selection: copy to clipboard
            else if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control && !string.IsNullOrEmpty(_lastSelectedText))
            {
                e.Handled = true;
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard != null)
                    await topLevel.Clipboard.SetTextAsync(_lastSelectedText);
                _lastSelectedText = null;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Terminal key handler failed: {0}", ex.Message);
        }
    }

    private static bool TryGetChar(KeyEventArgs e, out char ch)
    {
        ch = '\0';
        var keyStr = e.Key.ToString();
        if (keyStr.Length == 1 && char.IsLetterOrDigit(keyStr[0]))
        {
            ch = char.ToLower(keyStr[0]);
            return true;
        }
        return false;
    }
}
