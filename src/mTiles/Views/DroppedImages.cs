using System.Diagnostics;
using Avalonia.Media.Imaging;
using mTiles.Services;

namespace mTiles.Views;

/// <summary>Gives a picture dropped without a file a file, so a terminal can be handed its path.</summary>
/// <remarks>This half is the encoding, which is the only part that needs a UI toolkit; where the bytes go,
/// what the file is called and how long it is kept are <see cref="DroppedImageStore"/>'s, so that rule is
/// testable without one.</remarks>
public static class DroppedImages
{
    public static string? Save(Bitmap bitmap)
    {
        try
        {
            using var png = new MemoryStream();
            bitmap.Save(png);
            return DroppedImageStore.Save(png.ToArray(), DateTime.Now);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[ImageDrop] The dropped picture could not be encoded: {ex.Message}");
            return null;
        }
    }
}
