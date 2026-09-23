using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace mTiles.Views;

/// <summary>What arrived in one drop from outside the application: files, or a picture with no file.</summary>
/// <remarks>While the drag is still in the air <see cref="HasPicture"/> is answered and
/// <see cref="Bitmap"/> is not: the pixels are decoded only once they are dropped.</remarks>
public sealed record DroppedItems(IReadOnlyList<IStorageItem> Files, bool HasPicture, Bitmap? Bitmap = null);

/// <summary>
/// Makes a whole tile a place files and pictures can be dropped from the system, with a hint drawn over it
/// while something is held above it.
/// </summary>
/// <remarks>
/// <para>The platform's drag and drop, which is a different thing from moving a tile: that one is ours and
/// runs on pointer capture (<see cref="TileDragSession"/>), so the two never see each other's drags.</para>
/// <para>A picture dragged out of a browser often arrives as pixels with no file behind it, which is why
/// a bitmap is the second thing asked for — files first, because a file keeps its name.</para>
/// </remarks>
public static class ImageDrop
{
    /// <param name="target">What accepts the drop — the tile's view, so the whole card is the target.</param>
    /// <param name="hint">Shown while a drop would be accepted, hidden otherwise.</param>
    /// <param name="accepts">Whether this tile takes what is being dragged — asked on every move.</param>
    /// <param name="drop">What the tile does with it.</param>
    public static void Attach(Control target, Control hint, Func<DroppedItems, bool> accepts,
        Func<DroppedItems, Task> drop)
    {
        DragDrop.SetAllowDrop(target, true);
        hint.IsVisible = false;
        hint.IsHitTestVisible = false;

        // Counts the moves seen, so a leave can tell "the pointer crossed an inner control" from "the
        // drag is over" — see the leave handler.
        var moves = 0;

        void Over(object? sender, DragEventArgs e)
        {
            moves++;
            var ok = accepts(Peek(e));
            e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            hint.IsVisible = ok;
            e.Handled = true;
        }

        target.AddHandler(DragDrop.DragEnterEvent, Over, RoutingStrategies.Bubble, handledEventsToo: true);
        target.AddHandler(DragDrop.DragOverEvent, Over, RoutingStrategies.Bubble, handledEventsToo: true);
        // Avalonia raises a leave for every inner control the pointer crosses, so a leave that is still
        // over the tile is a move within it, not a way out — hiding there made the hint flicker. But a
        // drag abandoned with Escape over the tile leaves from inside too, and nothing follows it: hiding
        // only on a leave from outside left an opaque hint covering the terminal or the transcript for
        // good. So a leave from inside is answered by waiting one moment and hiding unless another move
        // arrived meanwhile — which is what tells a crossing apart from a cancellation.
        target.AddHandler(DragDrop.DragLeaveEvent, (_, e) =>
        {
            if (!new Avalonia.Rect(target.Bounds.Size).Contains(e.GetPosition(target)))
            {
                hint.IsVisible = false;
                return;
            }

            var seen = moves;
            DispatcherTimer.RunOnce(() =>
            {
                if (moves == seen) hint.IsVisible = false;
            }, TimeSpan.FromMilliseconds(200));
        },
            RoutingStrategies.Bubble, handledEventsToo: true);
        target.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            hint.IsVisible = false;
            var items = Read(e);
            // One line per drop: when nothing happens, its absence says the system never delivered the drag
            // — which is what an elevated process gets from Explorer (UIPI), with a no-drop cursor and no
            // error anywhere.
            Trace.TraceInformation(
                $"[ImageDrop] Dropped on {target.GetType().Name}: {items.Files.Count} file(s), picture: {items.Bitmap is not null}");
            if (!accepts(items))
            {
                // Decoded before it could be asked about, so nobody else will ever release it.
                items.Bitmap?.Dispose();
                return;
            }
            e.Handled = true;
            try { await drop(items); }
            catch (Exception ex) { Trace.TraceWarning($"[ImageDrop] The drop could not be taken: {ex.Message}"); }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static DroppedItems Peek(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles() is { Length: > 0 } files
            ? new(files, false)
            : new([], e.DataTransfer.Contains(DataFormat.Bitmap));

    private static DroppedItems Read(DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is { Length: > 0 } files) return new(files, false);
        try { return e.DataTransfer.TryGetBitmap() is { } bitmap ? new([], true, bitmap) : new([], false); }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[ImageDrop] The dropped picture could not be read: {ex.Message}");
            return new([], false);
        }
    }
}
