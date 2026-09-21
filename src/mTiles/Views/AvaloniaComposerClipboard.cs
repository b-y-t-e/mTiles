using Avalonia;
using Avalonia.Controls;
// The clipboard's TryGetTextAsync/TryGetBitmapAsync/TryGetFilesAsync are extensions living here.
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace mTiles.Views;

/// <summary>
/// <see cref="IComposerClipboard"/> over the clipboard of the window <paramref name="view"/> is in, read when
/// asked rather than when built, so a composer not yet in a window simply finds nothing.
/// </summary>
public sealed class AvaloniaComposerClipboard(Visual view) : IComposerClipboard
{
    private IClipboard? Clipboard => TopLevel.GetTopLevel(view)?.Clipboard;

    /// <summary>Explorer's <c>CF_HDROP</c>, a Linux file manager's <c>text/uri-list</c> — only the entries that
    /// are files on this machine.</summary>
    public Task<IReadOnlyList<IStorageItem>> FilesAsync() => ReadAsync<IReadOnlyList<IStorageItem>>("files", [],
        async clipboard => await clipboard.TryGetFilesAsync() is { } files
            ? files.Where(file => file.TryGetLocalPath() is not null).ToList()
            : []);

    public Task<bool> HasTextAsync() => ReadAsync("text", false,
        async clipboard => await clipboard.TryGetTextAsync() is { Length: > 0 });

    public Task<string?> TextAsync() => ReadAsync<string?>("text", null,
        async clipboard => await clipboard.TryGetTextAsync());

    public Task<Bitmap?> BitmapAsync() => ReadAsync<Bitmap?>("an image", null,
        async clipboard => await clipboard.TryGetBitmapAsync());

    private async Task<T> ReadAsync<T>(string what, T nothing, Func<IClipboard, Task<T>> read)
    {
        if (Clipboard is not { } clipboard) return nothing;
        try
        {
            return await read(clipboard);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Reading {what} from the clipboard failed: {ex.Message}");
            return nothing;
        }
    }
}
