using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;

namespace MTerminal.Views;

public sealed class TerminalKeyHandler
{
    private TerminalView? _terminalView;
    private FieldInfo? _ptyField;
    private string? _lastSelectedText;
    private bool _registered;

    public void Attach(Control parent, TerminalControl tc)
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

        if (!_registered)
        {
            parent.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            _registered = true;
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            if (_terminalView == null) return;

            if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
            {
                e.Handled = true;
                await _terminalView.PasteAsync();
            }
            else if (e.KeyModifiers == KeyModifiers.Alt && TryGetChar(e, out var ch))
            {
                e.Handled = true;
                WriteToPty($"\x1b{ch}");
            }
            else if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control && !string.IsNullOrEmpty(_lastSelectedText))
            {
                e.Handled = true;
                var topLevel = TopLevel.GetTopLevel(_terminalView);
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

    private void WriteToPty(string data)
    {
        var pty = _ptyField?.GetValue(_terminalView);
        if (pty == null) return;
        var stream = pty.GetType().GetProperty("WriterStream")?.GetValue(pty) as System.IO.Stream;
        if (stream == null) return;
        var bytes = Encoding.UTF8.GetBytes(data);
        stream.Write(bytes);
        stream.Flush();
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
