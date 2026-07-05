using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;

namespace mTiles.Views;

// Window-level Ctrl+C copy for terminals.
//
// Selection is created with the mouse in one terminal while keyboard focus may sit
// in another tile (the user switches tiles constantly). A per-tile key handler only
// sees key events when the focused element is inside its own tile, so it misses that
// case — and the unhandled Ctrl+C then sends SIGINT to the focused terminal.
//
// This coordinator listens once, on the window (tunnel), and copies from whichever
// terminal actually holds a selection: the focused one first, then the terminal the
// user most recently selected in, then any other live terminal. Without any selection
// Ctrl+C falls through and keeps its SIGINT meaning.
//
// Ctrl+V is NOT handled here — the terminal library does it (TerminalView.PasteOnCtrlV):
// clipboard text is pasted, and when the clipboard holds no text (e.g. an image) the
// raw Ctrl+V keystroke is forwarded so TUI apps (Claude Code) can paste the image
// from the clipboard themselves.
public static class TerminalClipboardCoordinator
{
    private static readonly List<WeakReference<TerminalView>> Terminals = new();
    private static WeakReference<TerminalView>? _lastSelectionOwner;

    public static void Attach(Window window)
    {
        window.AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        // Bubble fires after TerminalView.OnPointerReleased (EndSelection), so the
        // selection state is final when the owner is recorded. handledEventsToo is
        // required: TerminalView marks the release Handled when it ends a selection.
        window.AddHandler(InputElement.PointerReleasedEvent, OnWindowPointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    public static void Register(TerminalView terminal)
    {
        lock (Terminals)
        {
            Terminals.RemoveAll(wr => !wr.TryGetTarget(out _));
            if (!Terminals.Any(wr => wr.TryGetTarget(out var t) && ReferenceEquals(t, terminal)))
                Terminals.Add(new WeakReference<TerminalView>(terminal));
        }
    }

    private static void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var terminal = (e.Source as Visual)?.FindAncestorOfType<TerminalView>(includeSelf: true);
        if (terminal is not { HasSelection: true }) return;

        // Single global selection: clear the previous owner's selection so Ctrl+C
        // can never pick up stale text from another tile.
        if (_lastSelectionOwner?.TryGetTarget(out var prev) == true &&
            !ReferenceEquals(prev, terminal) && prev.HasSelection)
        {
            prev.Terminal?.Selection.ClearSelection();
            prev.InvalidateVisual();
        }

        _lastSelectionOwner = new WeakReference<TerminalView>(terminal);
    }

    private static async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key != Key.C) return;
            if (e.KeyModifiers != KeyModifiers.Control &&
                e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Shift)) return;

            var focused = (sender as Window)?.FocusManager?.GetFocusedElement() as Visual;

            // Don't hijack copy from text-editing controls (Note editor, commit message, dialogs).
            if (IsTextEditor(focused)) return;

            var target = FindSelectionOwner(focused);
            if (target == null) return; // no selection anywhere → Ctrl+C keeps SIGINT semantics

            e.Handled = true; // set before await — blocks SIGINT and the library's own path
            await target.CopyAsync();
            target.Terminal?.Selection.ClearSelection();
            target.InvalidateVisual();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Terminal copy failed: {0}", ex.Message);
        }
    }

    private static TerminalView? FindSelectionOwner(Visual? focused)
    {
        var focusedTerminal = focused?.FindAncestorOfType<TerminalView>(includeSelf: true);
        if (focusedTerminal is { HasSelection: true })
            return focusedTerminal;

        if (_lastSelectionOwner?.TryGetTarget(out var last) == true && last.HasSelection)
            return last;

        lock (Terminals)
        {
            foreach (var wr in Terminals)
                if (wr.TryGetTarget(out var t) && t.HasSelection)
                    return t;
        }

        return null;
    }

    private static bool IsTextEditor(Visual? element)
    {
        for (var v = element; v != null; v = v.GetVisualParent())
        {
            if (v is TextBox) return true;
            if ((v.GetType().FullName ?? "").StartsWith("AvaloniaEdit.")) return true;
        }
        return false;
    }
}
