using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace mTiles.Views;

/// <summary>
/// What Ctrl+V and Alt+V do in a composer: the one rule for which key takes the clipboard's files, its text
/// and its image, and in which order — shared by every box something can be pasted into (the Goal tile's and
/// the Agent tile's).
/// </summary>
/// <remarks>
/// <para><b>Files come first.</b> A file manager — Explorer, Dolphin, Nautilus, Thunar — puts the files on the
/// clipboard and, on Linux, their paths beside them as text; pasted as text they are a line of paths nobody
/// asked for, so where the box takes files they arrive as attachments and the text is not pasted.</para>
/// <para><b>Text wins over an image</b>, which is the rule the terminal tile already follows: a copy from a
/// browser or a screenshot tool routinely puts text and an image on the clipboard at once, and pasting the
/// picture instead of the words the user selected is the more surprising of the two mistakes. <b>Alt+V</b> is
/// the way past it, as it is in a terminal tile — and here it is the way past it <em>everywhere</em>, because
/// the clipboard is read on this side. In a terminal tile that gesture is only as good as the agent's own
/// keymap, and Claude Code binds it on Windows and WSL alone.</para>
/// <para>The bitmap is handed over decoded; what each tile turns it into (PNG bytes, a scaled attachment) is
/// its own business.</para>
/// </remarks>
public static class ComposerPaste
{
    /// <summary>Pastes into <paramref name="box"/> off its window's clipboard; see the overload below.</summary>
    public static bool TryPaste(TextBox box, KeyEventArgs e, Action<Bitmap> takeImage,
        Func<IReadOnlyList<IStorageItem>, Task>? takeFiles = null) =>
        TryPaste(new AvaloniaComposerClipboard(box), e, box.Paste, takeImage, takeFiles);

    /// <summary>
    /// Takes the clipboard's files, text or image for Alt+V or Ctrl+V; answers whether the key was one of the two.
    /// </summary>
    /// <remarks>
    /// <para>Alt+V attaches the files, else the image, whatever text is on the clipboard, and nothing else wants
    /// the key — the box ignores it — so it is marked handled and taken outright.</para>
    /// <para><b>Ctrl+V is taken too where the box accepts files</b>, and <paramref name="pasteText"/> is called
    /// only once the clipboard has been read and holds no files. It cannot be left to run beside ours, as it is
    /// for an image: the box would paste the paths while the same files arrived as attachments, and which won
    /// would be whichever answer came back first. Deciding in the key handler is not possible either: reading a
    /// clipboard is asynchronous, and the key has been dispatched long before the answer comes back. Where no
    /// files are accepted Ctrl+V is left unmarked, the box pastes the text itself, and the two stay exclusive
    /// because the image is taken only when there is no text.</para>
    /// </remarks>
    public static bool TryPaste(IComposerClipboard clipboard, KeyEventArgs e, Action pasteText,
        Action<Bitmap> takeImage, Func<IReadOnlyList<IStorageItem>, Task>? takeFiles = null)
    {
        if (e is { Key: Key.V, KeyModifiers: KeyModifiers.Alt })
        {
            e.Handled = true;
            _ = AttachAsync(clipboard, takeImage, takeFiles);
            return true;
        }

        if (!IsTextPaste(e)) return false;

        if (takeFiles is null)
        {
            _ = TakeImageUnlessTextAsync(clipboard, takeImage);
            return true;
        }

        e.Handled = true;
        _ = PasteAsync(clipboard, pasteText, takeImage, takeFiles);
        return true;
    }

    /// <summary>
    /// The keys a <see cref="TextBox"/> pastes on — Ctrl+V, Ctrl+Shift+V and Shift+Insert. All three are the
    /// same paste, so files copied in a file manager arrive as attachments whichever one was pressed.
    /// </summary>
    private static bool IsTextPaste(KeyEventArgs e) => e switch
    {
        { Key: Key.V, KeyModifiers: KeyModifiers.Control } => true,
        { Key: Key.V, KeyModifiers: KeyModifiers.Control | KeyModifiers.Shift } => true,
        { Key: Key.Insert, KeyModifiers: KeyModifiers.Shift } => true,
        _ => false,
    };

    /// <summary>Alt+V: the files, else the image.</summary>
    private static async Task AttachAsync(IComposerClipboard clipboard, Action<Bitmap> takeImage,
        Func<IReadOnlyList<IStorageItem>, Task>? takeFiles)
    {
        if (await TryTakeFilesAsync(clipboard, takeFiles)) return;
        await TakeImageAsync(clipboard, takeImage);
    }

    /// <summary>Ctrl+V where the box takes files: the files, else the text, else the image.</summary>
    private static async Task PasteAsync(IComposerClipboard clipboard, Action pasteText, Action<Bitmap> takeImage,
        Func<IReadOnlyList<IStorageItem>, Task> takeFiles)
    {
        if (await TryTakeFilesAsync(clipboard, takeFiles)) return;
        if (await clipboard.HasTextAsync())
        {
            pasteText();
            return;
        }

        await TakeImageAsync(clipboard, takeImage);
    }

    /// <summary>Ctrl+V where the box pastes the text itself: the image, only when there is no text.</summary>
    private static async Task TakeImageUnlessTextAsync(IComposerClipboard clipboard, Action<Bitmap> takeImage)
    {
        if (await clipboard.HasTextAsync()) return;
        await TakeImageAsync(clipboard, takeImage);
    }

    private static async Task<bool> TryTakeFilesAsync(IComposerClipboard clipboard,
        Func<IReadOnlyList<IStorageItem>, Task>? takeFiles)
    {
        if (takeFiles is null || await clipboard.FilesAsync() is not { Count: > 0 } files) return false;
        try
        {
            await takeFiles(files);
        }
        catch (Exception ex)
        {
            // A copied file can be locked or gone by the time it is attached. The files were still what the
            // clipboard held, so the paste ends here rather than falling through to their paths as text.
            System.Diagnostics.Trace.TraceWarning($"Attaching files from the clipboard failed: {ex.Message}");
        }
        return true;
    }

    /// <summary>Hands the clipboard's image to <paramref name="takeImage"/>, and disposes it afterwards.</summary>
    private static async Task TakeImageAsync(IComposerClipboard clipboard, Action<Bitmap> takeImage)
    {
        if (await clipboard.BitmapAsync() is not { } bitmap) return;
        try
        {
            using (bitmap) takeImage(bitmap);
        }
        catch (Exception ex)
        {
            // An image on the clipboard can be one this machine cannot re-encode. Not worth a dialog over a
            // paste that can be tried again.
            System.Diagnostics.Trace.TraceWarning($"Pasting an image from the clipboard failed: {ex.Message}");
        }
    }
}
