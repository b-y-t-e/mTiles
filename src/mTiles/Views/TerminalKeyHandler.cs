using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;

namespace mTiles.Views;

// Workaround for Iciclecreek.Terminal keyboard limitations:
// - It doesn't send ESC prefix for Alt+key combinations
// - Ctrl+V paste needs to be handled before the library sends raw ^V to PTY
// - WriterStream is not exposed publicly (requires reflection)
// Ctrl+C copy is handled natively by the library (≥2.6.0).
public sealed class TerminalKeyHandler
{
    private TerminalControl? _terminalControl;
    private TerminalView? _terminalView;
    private bool _registered;

    public void Attach(Control parent, TerminalControl tc)
    {
        var tv = tc.GetVisualDescendants().OfType<TerminalView>().FirstOrDefault();
        if (tv == null || tv == _terminalView) return;
        _terminalControl = tc;
        _terminalView = tv;

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
            if (_terminalControl == null || _terminalView == null) return;

            if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
            {
                e.Handled = true;
                await _terminalView.PasteAsync();
            }
            // TerminalView doesn't generate ESC prefix for Alt — sends raw char or nothing.
            // We write ESC+char directly; class handler won't duplicate (TryGetPrintableChar
            // fails with Alt modifier, so it sends nothing).
            else if (e.KeyModifiers == KeyModifiers.Alt && TryGetChar(e, out var ch))
            {
                e.Handled = true;
                PtyWriter.Write(_terminalControl, $"\x1b{ch}");
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
