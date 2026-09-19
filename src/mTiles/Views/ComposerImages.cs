using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using mTiles.AgentSessions.Events;

namespace mTiles.Views;

/// <summary>
/// Turns what a person pastes, drops or picks into an image an agent can be handed.
/// </summary>
/// <remarks>
/// <para><b>Every image is re-encoded as PNG and scaled to at most <see cref="LongEdge"/> pixels on its
/// longer side.</b> One shape for every agent — each session only wraps the bytes — and a size that fits:
/// 1568 px is what Anthropic's vision documentation recommends as the largest edge worth sending, a 4K
/// screenshot at full size is several megabytes, and every image is also stored in the conversation.</para>
/// <para>Here rather than in the view model because decoding and encoding need the imaging stack; the
/// view model is handed finished bytes, which is also what a test can give it.</para>
/// </remarks>
public static class ComposerImages
{
    public const int LongEdge = 1568;

    /// <summary>The extensions offered by the file picker and accepted from a drop.</summary>
    public static readonly string[] Extensions = ["png", "jpg", "jpeg", "gif", "webp", "bmp"];

    /// <summary>Asks for files to attach: any file at all, with pictures offered as a filter of their own.</summary>
    /// <remarks>Any file, because a composer takes a log or a schema too — by its path, see
    /// <c>ComposerFileReference</c>. "All files" comes first so it is what the dialog opens on.</remarks>
    public static Task<IReadOnlyList<IStorageFile>> PickAsync(IStorageProvider storage) =>
        storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("All files") { Patterns = ["*"] },
                new FilePickerFileType("Images") { Patterns = [.. Extensions.Select(extension => $"*.{extension}")] },
            ],
        });

    public static ImageAttachment FromBitmap(Bitmap bitmap, string? name = null)
    {
        var size = bitmap.PixelSize;
        var scale = Math.Min(1.0, (double)LongEdge / Math.Max(size.Width, size.Height));
        using var scaled = scale < 1.0
            ? bitmap.CreateScaledBitmap(new PixelSize(Math.Max(1, (int)(size.Width * scale)),
                Math.Max(1, (int)(size.Height * scale))))
            : null;

        using var png = new MemoryStream();
        (scaled ?? bitmap).Save(png);
        return new ImageAttachment("image/png", Convert.ToBase64String(png.ToArray()), name);
    }

    /// <summary>
    /// Hands every file over in the order given: a picture as a decoded bitmap, anything else by its path.
    /// </summary>
    /// <remarks>One rule for both composers, so a drop or a pick lands the same way in the Goal tile and the
    /// Agent tile. A file with a picture's name that will not decode — damaged, or too large — is still
    /// something the agent can open for itself, so it falls to its path. The bitmap is disposed after
    /// <paramref name="attachPicture"/> returns.</remarks>
    public static async Task AttachAllAsync(IEnumerable<IStorageItem> files,
        Action<Bitmap, string> attachPicture, Func<string, Task> attachPath)
    {
        foreach (var file in files)
        {
            using var picture = await DecodeAsync(file);
            if (picture is not null) attachPicture(picture, file.Name);
            else if (file.TryGetLocalPath() is { } path) await attachPath(path);
        }
    }

    /// <summary>The picture in a file, or null when it is not one this machine can decode.</summary>
    private static async Task<Bitmap?> DecodeAsync(IStorageItem item)
    {
        if (item is not IStorageFile file) return null;
        var name = file.Name;
        if (!Extensions.Contains(Path.GetExtension(name).TrimStart('.').ToLowerInvariant())) return null;

        try
        {
            await using var stream = await file.OpenReadAsync();
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Composer] {name} could not be read as an image: {ex.Message}");
            return null;
        }
    }
}
