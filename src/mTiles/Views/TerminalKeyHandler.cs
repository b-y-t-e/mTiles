using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;

namespace mTiles.Views;

// Ctrl+V paste must be intercepted before the library sends raw \x16 to PTY.
// Ctrl+C copy and Alt+key ESC prefix are handled natively by the library (≥2.6.0).
public sealed class TerminalKeyHandler
{
    private TerminalView? _terminalView;
    private bool _registered;

    public void Attach(Control parent, TerminalControl tc)
    {
        var tv = tc.GetVisualDescendants().OfType<TerminalView>().FirstOrDefault();
        if (tv == null || tv == _terminalView) return;
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
            if (_terminalView == null) return;

            if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
            {
                e.Handled = true;
                await _terminalView.PasteAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Terminal key handler failed: {0}", ex.Message);
        }
    }
}
