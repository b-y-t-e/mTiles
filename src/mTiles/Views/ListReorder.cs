using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace mTiles.Views;

/// <summary>
/// Lets the rows of an <see cref="ItemsControl"/> be put in another order by dragging a row's handle.
/// </summary>
/// <remarks>
/// <para><b>Only by the handle</b> — an element carrying the <c>drag-handle</c> class inside the row. A row
/// here is full of buttons, and a drag armed anywhere on it would turn a slightly unsteady click on Edit into a
/// reordering nobody asked for.</para>
/// <para><b>The row moves as the pointer crosses the next one</b>, rather than a marker being drawn and the
/// move made on release: the list itself is the preview, so there is no second picture of it to keep in step.
/// Each crossing is handed to <c>move</c>; <c>commit</c> is called once, on release, which is where anything
/// that costs something — a save, every chooser redrawing — belongs.</para>
/// <para>The drag is ours rather than the platform's, for the reason a tile drag is (<c>TileDragHandle</c>):
/// a row never leaves the window, and on Windows the platform's drag is a modal loop.</para>
/// </remarks>
internal sealed class ListReorder
{
    private const string HandleClass = "drag-handle";
    private const string DraggingClass = "dragging";

    private readonly ItemsControl _list;
    private readonly Action<int, int> _move;
    private readonly Action _commit;
    private int _index = -1;
    private IPointer? _pointer;

    private ListReorder(ItemsControl list, Action<int, int> move, Action commit)
    {
        _list = list;
        _move = move;
        _commit = commit;
        list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
    }

    /// <param name="list">The list whose rows are reordered.</param>
    /// <param name="move">Moves the item at the first index to the second.</param>
    /// <param name="commit">The order is final — the pointer was released.</param>
    public static void Attach(ItemsControl list, Action<int, int> move, Action commit) =>
        _ = new ListReorder(list, move, commit);

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Visual source || !IsOnHandle(source)) return;

        var index = IndexAt(e.GetPosition(_list));
        if (index < 0) return;

        _index = index;
        _pointer = e.Pointer;
        e.Pointer.Capture(_list);
        SetDragging(true);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_index < 0 || e.Pointer != _pointer) return;

        var over = IndexAt(e.GetPosition(_list));
        if (over < 0 || over == _index) return;

        SetDragging(false);
        _move(_index, over);
        _index = over;
        SetDragging(true);
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_index < 0 || e.Pointer != _pointer) return;
        e.Handled = true;
        End();
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_index >= 0) End();
    }

    private void End()
    {
        SetDragging(false);
        _index = -1;
        var pointer = _pointer;
        _pointer = null;
        pointer?.Capture(null);
        _commit();
    }

    private bool IsOnHandle(Visual source)
    {
        for (Visual? v = source; v is not null && v != _list; v = v.GetVisualParent())
            if (v is StyledElement styled && styled.Classes.Contains(HandleClass))
                return true;
        return false;
    }

    /// <summary>The row the point is over, or the nearest end when it is above or below them all.</summary>
    private int IndexAt(Point point)
    {
        var count = _list.ItemCount;
        for (var i = 0; i < count; i++)
        {
            if (_list.ContainerFromIndex(i) is not { } row) continue;
            var top = row.TranslatePoint(new Point(0, 0), _list);
            if (top is null) continue;
            if (point.Y < top.Value.Y + row.Bounds.Height) return i;
        }

        return count - 1;
    }

    private void SetDragging(bool on)
    {
        if (_index >= 0 && _list.ContainerFromIndex(_index) is { } row)
            row.Classes.Set(DraggingClass, on);
    }
}
