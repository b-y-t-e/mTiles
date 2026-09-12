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

    private Point? _start;
    private PointerPressedEventArgs? _pressed;

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
        _measuredIn = measuredIn;
        _mayArm = mayArm;
        _source = source;
        _dragging = dragging;

        handle.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        handle.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        handle.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
    }

    /// <summary>Forgets a press that has not become a drag yet.</summary>
    /// <remarks>For whoever acts on the press instead — a double-click that fills the layout with the
    /// tile moves its view, and an origin measured before that would read the next pixel of travel as a
    /// drag of several hundred.</remarks>
    public void Disarm()
    {
        _start = null;
        _pressed = null;
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
        _pressed = e;
    }

    private async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_start is not { } start || _pressed is not { } pressed) return;

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

        TileDragSession.Begin(leaf);
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(TileDragSession.DataFormat));

        _dragging(true);
        try
        {
            await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("DragDrop failed: {0}", ex.Message);
        }
        finally
        {
            _dragging(false);
            TileDragSession.End();
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e) => Disarm();
}
