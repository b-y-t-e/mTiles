using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
// The clipboard's TryGetTextAsync/TryGetBitmapAsync are extensions living here.
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;

namespace mTiles.Views;

/// <summary>
/// An image pasted into a composer: the one rule for which key takes it and when, shared by every box a
/// picture can be pasted into (the Goal tile's and the Agent tile's).
/// </summary>
/// <remarks>
/// <para><b>Text wins when the clipboard holds both</b>, which is the rule the terminal tile already
/// follows: a copy from a browser or a screenshot tool routinely puts text and an image on the clipboard at
/// once, and pasting the picture instead of the words the user selected is the more surprising of the two
/// mistakes. <b>Alt+V</b> is the way past it, as it is in a terminal tile — and here it is the way past it
/// <em>everywhere</em>, because the clipboard is read on this side. In a terminal tile that gesture is only
/// as good as the agent's own keymap, and Claude Code binds it on Windows and WSL alone.</para>
/// <para>The bitmap is handed over decoded; what each tile turns it into (PNG bytes, a scaled attachment)
/// is its own business.</para>
/// </remarks>
public static class ClipboardImage
{
    /// <summary>
    /// Takes the clipboard's image for Alt+V or Ctrl+V; answers whether the key was one of the two.
    /// </summary>
    /// <remarks>Alt+V is the image whatever else is on the clipboard, and nothing else wants the key — the
    /// box ignores it — so it is marked handled and taken outright. Ctrl+V is deliberately <em>not</em>
    /// marked: the box's own paste has to go on working, and whether there is an image to take instead
    /// cannot be known here, because reading a clipboard is asynchronous and the key has been dispatched
    /// long before the answer comes back. Letting both run is safe precisely because the two are exclusive
    /// — the image is taken only when there is no text, which is the case in which the box's paste does
    /// nothing at all.</remarks>
    public static bool TryPaste(Visual view, KeyEventArgs e, Action<Bitmap> take)
    {
        if (e.Key != Key.V) return false;

        if (e.KeyModifiers == KeyModifiers.Alt)
        {
            e.Handled = true;
            _ = ReadAsync(view, evenWhenThereIsText: true, take);
            return true;
        }

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            _ = ReadAsync(view, evenWhenThereIsText: false, take);
            return true;
        }

        return false;
    }

    /// <summary>Hands the clipboard's image to <paramref name="take"/>, and disposes it afterwards.</summary>
    public static async Task ReadAsync(Visual view, bool evenWhenThereIsText, Action<Bitmap> take)
    {
        if (TopLevel.GetTopLevel(view)?.Clipboard is not { } clipboard) return;

        try
        {
            if (!evenWhenThereIsText && await clipboard.TryGetTextAsync() is { Length: > 0 }) return;
            if (await clipboard.TryGetBitmapAsync() is not { } bitmap) return;

            using (bitmap) take(bitmap);
        }
        catch (Exception ex)
        {
            // A clipboard can be held by another application, and an image on it can be one this machine
            // cannot decode. Neither is worth a dialog over a paste that can be tried again.
            System.Diagnostics.Trace.TraceWarning($"Reading an image from the clipboard failed: {ex.Message}");
        }
    }
}
