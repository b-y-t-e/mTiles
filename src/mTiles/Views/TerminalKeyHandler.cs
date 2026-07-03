using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;

namespace mTiles.Views;

// Tunnel handler (runs before the library's own OnKeyDown):
// - Ctrl+V paste must be intercepted before the library sends raw \x16 to PTY.
// - Ctrl+C copy is handled here directly (not left to the library), because the
//   library only copies when the inner TerminalView has keyboard focus — which is
//   fragile in the tiled/multi-terminal layout. Copying via CopyAsync() does NOT
//   require focus, so this works reliably regardless of focus/library DLL version.
public sealed class TerminalKeyHandler
{
    private TerminalView? _terminalView;
    private bool _registered;

    private static FieldInfo? _terminalField;

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
                return;
            }

            // Ctrl+C / Ctrl+Shift+C: copy the selection ourselves (focus-independent).
            // Only mark Handled when there IS a selection — otherwise let Ctrl+C fall
            // through to the library so it sends SIGINT to the process as usual.
            if (e.Key == Key.C &&
                (e.KeyModifiers == KeyModifiers.Control ||
                 e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)) &&
                HasSelection(_terminalView))
            {
                e.Handled = true; // set before await → blocks SIGINT and the library's path
                await _terminalView.CopyAsync();
                return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Terminal key handler failed: {0}", ex.Message);
        }
    }

    // Reflects TerminalView._terminal.Selection.HasSelection (no public accessor).
    private static bool HasSelection(TerminalView tv)
    {
        _terminalField ??= typeof(TerminalView)
            .GetField("_terminal", BindingFlags.NonPublic | BindingFlags.Instance);

        var terminal = _terminalField?.GetValue(tv);
        var selection = terminal?.GetType().GetProperty("Selection")?.GetValue(terminal);
        var hasSelection = selection?.GetType().GetProperty("HasSelection")?.GetValue(selection);
        return hasSelection is true;
    }
}
