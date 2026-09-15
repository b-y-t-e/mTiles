using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>
/// Turns a press on one control into a drag of a tile: the threshold, the double-click guard and the
/// release that never arrives.
/// </summary>
/// <remarks>
/// <para>Lifted out of the tile header, where each of those three rules was paid for once, so that
/// anything else that stands for a tile — the window's own tiles draw headers of their own — gets the
/// same gesture rather than a second copy of it that has not learned them yet.</para>
/// <para>The handlers tunnel on purpose: a press has to be seen before a child control consumes it.</para>
/// </remarks>
internal sealed class TileDragHandle
{
    /// <summary>How far the pointer has to travel, with the button still down, before a press on the
    /// handle becomes a drag of the tile.</summary>
    /// <remarks>Comfortably above the platform's own double-tap slop (Avalonia's default is 4 DIP, so
    /// the second click of a double-click has to land within 2 of the first): at 6 the band between
    /// "still a double-click" and "already a drag" was four pixels wide, which on a mouse with no
    /// pointer acceleration — the usual Linux configuration — is hand tremor. Below it the header's
    /// double-click did not fire and the tile went translucent instead.</remarks>
    public const double Threshold = 12;

    private readonly Visual _measuredIn;
    private readonly Func<PointerPressedEventArgs, bool> _mayArm;
    private readonly Func<LeafTileNodeViewModel?> _source;
    private readonly Action<bool> _dragging;

    private static readonly Cursor DragCursor = new(StandardCursorType.DragMove);

    private readonly InputElement _handle;

    private Point? _start;

    /// <summary>The window a drag is in flight in, or null when there is none.</summary>
    private TopLevel? _window;
    private IPointer? _pointer;
    private Cursor? _previousCursor;

    /// <param name="handle">What the user presses to drag.</param>
    /// <param name="measuredIn">What the pointer's travel is measured against — the tile's own view,
    /// which moves with the tile.</param>
    /// <param name="mayArm">Whether this press may become a drag at all — not on a button, not while
    /// a name is being edited.</param>
    /// <param name="source">The tile being dragged, asked at the moment the drag starts.</param>
    /// <param name="dragging">Told when the drag starts and when it ends, however it ended.</param>
    public TileDragHandle(
        InputElement handle,
        Visual measuredIn,
        Func<PointerPressedEventArgs, bool> mayArm,
        Func<LeafTileNodeViewModel?> source,
        Action<bool> dragging)
    {
        _handle = handle;
        _measuredIn = measuredIn;
        _mayArm = mayArm;
        _source = source;
        _dragging = dragging;

        handle.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        handle.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        handle.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        handle.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
    }

    /// <summary>Forgets a press that has not become a drag yet.</summary>
    /// <remarks>For whoever acts on the press instead — a double-click that fills the layout with the
    /// tile moves its view, and an origin measured before that would read the next pixel of travel as a
    /// drag of several hundred.</remarks>
    public void Disarm()
    {
        _start = null;
    }

    /// <remarks>The second click of a double-click never arms a drag. Avalonia raises
    /// <c>DoubleTapped</c> from that very press, and this handler tunnels — so it runs first, armed the
    /// drag, and the header's double-click then filled the workspace with the tile, which detaches its
    /// view and puts it back somewhere else entirely. The armed origin was measured in the layout that no
    /// longer exists, so the next pointer move — a pixel of it — read as a drag of several
    /// hundred.</remarks>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        Disarm();
        if (!e.GetCurrentPoint(_measuredIn).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount > 1) return;
        if (!_mayArm(e)) return;

        _start = e.GetPosition(_measuredIn);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_window is { } window)
        {
            // The button came up without a release reaching us — see below. A drop nobody released is
            // not a drop, so the drag is abandoned rather than completed.
            if (!e.GetCurrentPoint(_measuredIn).Properties.IsLeftButtonPressed)
                EndDrag(drop: null);
            else
                TileDragSession.Over(window, e.GetPosition(window));
            e.Handled = true;
            return;
        }

        if (_start is null) return;
        var start = _start.Value;

        // A drag only ever begins while the button is still down, and this is the check rather than the
        // release handler below: a release is not guaranteed to arrive. On Wayland the pointer's focused
        // surface is cleared by a leave, and a button event with no focused surface is dropped by the
        // backend outright — which a maximize triggered from the header does, since it resizes the
        // window under the pointer mid-gesture. The arm then survived the whole click and the next move
        // put the tile into a drag nobody started.
        if (!e.GetCurrentPoint(_measuredIn).Properties.IsLeftButtonPressed)
        {
            Disarm();
            return;
        }

        var delta = e.GetPosition(_measuredIn) - start;
        if (Math.Abs(delta.X) < Threshold && Math.Abs(delta.Y) < Threshold) return;

        Disarm();

        if (_source() is not { } leaf) return;
        if (TopLevel.GetTopLevel(_handle) is not { } top) return;

        BeginDrag(leaf, top, e);
    }

    /// <summary>Starts a drag of <paramref name="leaf"/>, carried by this handle's pointer capture.</summary>
    /// <remarks>Ours rather than the platform's: <c>DragDrop.DoDragDropAsync</c> is an OLE modal loop on
    /// Windows and lagged behind the pointer there, while a tile never leaves the window, so none of what
    /// the platform's drag offers is used. Capturing the pointer is what keeps its moves and its release
    /// arriving here wherever it goes.</remarks>
    private void BeginDrag(LeafTileNodeViewModel leaf, TopLevel top, PointerEventArgs e)
    {
        TileDragSession.Begin(leaf);
        _window = top;
        _pointer = e.Pointer;
        _previousCursor = top.Cursor;
        top.Cursor = DragCursor;
        top.AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        _pointer.Capture(_handle);
        _dragging(true);

        TileDragSession.Over(top, e.GetPosition(top));
        e.Handled = true;
    }

    /// <summary>Ends the drag in flight, dropping at <paramref name="drop"/> or abandoning it when null.</summary>
    /// <remarks>Everything this handle changed is put back <em>before</em> the drop runs: a drop edits the
    /// tree, which rebuilds views — this one's included — and releasing a capture afterwards would be
    /// releasing it from a control that may no longer be in the window.</remarks>
    private void EndDrag(Point? drop)
    {
        if (_window is not { } window) return;
        _window = null;

        window.RemoveHandler(InputElement.KeyDownEvent, OnWindowKeyDown);
        window.Cursor = _previousCursor;
        _previousCursor = null;

        var pointer = _pointer;
        _pointer = null;
        pointer?.Capture(null);
        _dragging(false);

        try
        {
            if (drop is { } point) TileDragSession.Drop(window, point);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Tile drop failed: {0}", ex);
        }
        finally
        {
            TileDragSession.End();
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        Disarm();
        if (_window is not { } window) return;

        // Only the button carrying the drag drops it: a right or middle click made while it is held down
        // is swallowed rather than taken as the release, or the tile would land wherever the pointer is.
        if (e.InitialPressMouseButton == MouseButton.Left)
            EndDrag(e.GetPosition(window));
        e.Handled = true;
    }

    /// <summary>Anything that takes the capture away — another window, the control leaving the tree — ends
    /// the drag without a drop.</summary>
    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag(drop: null);

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        EndDrag(drop: null);
        e.Handled = true;
    }
}
