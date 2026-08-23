using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Terminal.Avalonia;

namespace mTiles.Services;

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
// Everything else is the control's own job and is deliberately not here: Ctrl+V (it
// pastes clipboard text itself), Ctrl+C within one terminal, and knowing when a
// selection appears or goes away (SelectionChanged).
//
// UI thread only, all of it: SelectionChanged and KeyDown are raised on it, and the terminal control is
// thread-affine anyway. Hence no locking — a lock on some of the state and not the rest is worse than
// none, because it reads as a promise. The mutating entry points assert the thread instead, so a caller
// that gets this wrong finds out at the call rather than through a corrupted list months later.
public static class TerminalClipboardCoordinator
{
    private static readonly List<WeakReference<TerminalControl>> Terminals = new();
    private static WeakReference<TerminalControl>? _lastSelectionOwner;

    public static void Attach(Window window)
        => window.AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

    public static void Register(TerminalControl terminal)
    {
        Dispatcher.UIThread.VerifyAccess();
        Terminals.RemoveAll(wr => !wr.TryGetTarget(out _));
        if (Terminals.Any(wr => wr.TryGetTarget(out var t) && ReferenceEquals(t, terminal)))
            return;

        Terminals.Add(new WeakReference<TerminalControl>(terminal));
        terminal.SelectionChanged += OnSelectionChanged;
    }

    /// <summary>Drops a terminal whose tile has closed. The weak references make this optional for
    /// memory, but not for behaviour: a disposed terminal that still held a selection would go on
    /// answering Ctrl+C with text from a tile that is no longer on screen.</summary>
    public static void Unregister(TerminalControl terminal)
    {
        Dispatcher.UIThread.VerifyAccess();
        terminal.SelectionChanged -= OnSelectionChanged;
        Terminals.RemoveAll(wr => !wr.TryGetTarget(out var t) || ReferenceEquals(t, terminal));

        if (_lastSelectionOwner?.TryGetTarget(out var owner) == true && ReferenceEquals(owner, terminal))
            _lastSelectionOwner = null;
    }

    /// <summary>One selection across all tiles: the terminal that just got one takes ownership, and the
    /// previous owner loses its highlight so Ctrl+C can never pick up stale text from another tile.</summary>
    private static void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (sender is not TerminalControl terminal || !terminal.HasSelection) return;

        if (_lastSelectionOwner?.TryGetTarget(out var prev) == true &&
            !ReferenceEquals(prev, terminal) && prev.HasSelection)
        {
            prev.ClearSelection();
        }

        _lastSelectionOwner = new WeakReference<TerminalControl>(terminal);
    }

    private static void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key != Key.C) return;
            if (e.KeyModifiers != KeyModifiers.Control &&
                e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Shift)) return;

            var focused = (sender as Window)?.FocusManager?.GetFocusedElement() as Visual;

            // Don't hijack copy from a control that has a selection of its own (Note editor, commit
            // message, dialogs, the Goal tile's transcript).
            if (HandlesItsOwnCopy(focused)) return;

            var target = FindSelectionOwner(focused);
            if (target == null) return; // no selection anywhere → Ctrl+C keeps SIGINT semantics

            // Handled only if there was something to copy. `Copy` says nothing about the clipboard
            // accepting the text — the control absorbs that failure deliberately — but it does report
            // a selection that yields no text at all, which a stray drag across blank cells produces.
            // Swallowing the key for that would cost the user the interrupt and give nothing back.
            if (!target.Copy())
                return;

            e.Handled = true; // blocks SIGINT and the control's own Ctrl+C
            target.ClearSelection();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Terminal copy failed: {0}", ex.Message);
        }
    }

    private static TerminalControl? FindSelectionOwner(Visual? focused)
    {
        var focusedTerminal = focused?.FindAncestorOfType<TerminalControl>(includeSelf: true);
        if (focusedTerminal is { HasSelection: true })
            return focusedTerminal;

        if (_lastSelectionOwner?.TryGetTarget(out var last) == true && last.HasSelection)
            return last;

        foreach (var wr in Terminals)
            if (wr.TryGetTarget(out var t) && t.HasSelection)
                return t;

        return null;
    }

    /// <summary>
    /// Whether the focused element handles Ctrl+C itself. Named for that rather than for text editing:
    /// a SelectableTextBlock edits nothing, and the old name argued against including it. <see cref="SelectableTextBlock"/> counts:
    /// it holds its own selection, is focusable, and copies on Ctrl+C — but this tunnel handler runs
    /// first, so without it a selection made in the Goal tile's transcript was answered with text from
    /// whichever terminal still had one, in a tile the user was not even looking at.
    /// </summary>
    private static bool HandlesItsOwnCopy(Visual? element)
    {
        for (var v = element; v != null; v = v.GetVisualParent())
        {
            if (v is TextBox or SelectableTextBlock) return true;
            if ((v.GetType().FullName ?? "").StartsWith("AvaloniaEdit.")) return true;
        }
        return false;
    }
}
