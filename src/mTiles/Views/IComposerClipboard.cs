using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace mTiles.Views;

/// <summary>
/// The three things a composer's paste asks of the clipboard. Each answers "nothing" rather than throwing:
/// a clipboard held by another application, or an image this machine cannot decode, is a paste that found
/// nothing and can be tried again.
/// </summary>
public interface IComposerClipboard
{
    /// <summary>The local files a file manager copied, or an empty list.</summary>
    Task<IReadOnlyList<IStorageItem>> FilesAsync();

    /// <summary>Whether the clipboard carries any text.</summary>
    Task<bool> HasTextAsync();

    /// <summary>The clipboard's image, owned by the caller, or null.</summary>
    Task<Bitmap?> BitmapAsync();
}
