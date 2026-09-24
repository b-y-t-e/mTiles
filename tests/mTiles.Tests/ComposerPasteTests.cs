using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The order a composer's paste takes the clipboard in: files before text — a file manager puts the paths
/// beside the files, and pasted as text they are a line nobody asked for — text before an image, and Ctrl+V
/// taken from the box only where the box takes files.
/// </summary>
public class ComposerPasteTests
{
    /// <summary>
    /// What each paste key takes, from what the clipboard holds, into a box that takes what it takes.
    /// </summary>
    /// <remarks>Clipboard: <c>f</c> a file, <c>t</c> a short text, <c>L</c> a long text, <c>i</c> an image.
    /// Box: <c>F</c> takes files, <c>I</c> takes images, <c>N</c> folds a long paste into a note,
    /// <c>X</c> and that fold fails. Expected: files taken, texts pasted, images taken.</remarks>
    [Theory]
    // Files before text: pasted as text, a file manager's paths are a line nobody asked for.
    [InlineData("ft", "FI", "Ctrl+V", true, "1,0,0", false)]
    // Text before an image.
    [InlineData("ti", "FI", "Ctrl+V", true, "0,1,0", false)]
    [InlineData("i", "FI", "Ctrl+V", true, "0,0,1", false)]
    // Ctrl+V is left to the box where the box takes no files.
    [InlineData("ti", "I", "Ctrl+V", false, "0,0,0", false)]
    // Alt+V attaches files first and otherwise the image over any text.
    [InlineData("fti", "FI", "Alt+V", true, "1,0,0", false)]
    [InlineData("ti", "FI", "Alt+V", true, "0,0,1", false)]
    // Shift+V is not a paste.
    [InlineData("ft", "FI", "Shift+V", false, "0,0,0", false)]
    // Every paste key of the box attaches copied files.
    [InlineData("ft", "FI", "Ctrl+Shift+V", true, "1,0,0", false)]
    [InlineData("ft", "FI", "Shift+Insert", true, "1,0,0", false)]
    // A long paste is folded into a note and never reaches the box; a short one is the box's own.
    [InlineData("L", "FIN", "Ctrl+V", true, "0,0,0", true)]
    [InlineData("t", "FIN", "Ctrl+V", true, "0,1,0", false)]
    // Ctrl+Shift+V is the way past the folding.
    [InlineData("L", "FIN", "Ctrl+Shift+V", true, "0,1,0", false)]
    // A fold that fails is still a paste, into the box.
    [InlineData("L", "FINX", "Ctrl+V", true, "0,1,0", false)]
    // Files still come before a long text.
    [InlineData("fL", "FIN", "Ctrl+V", true, "1,0,0", false)]
    // The Goal tile's plan field takes neither files nor images and still folds.
    [InlineData("L", "N", "Ctrl+V", true, "0,0,0", true)]
    [InlineData("i", "N", "Alt+V", false, "0,0,0", false)]
    // A box that takes only files still gets them.
    [InlineData("f", "F", "Ctrl+V", true, "1,0,0", false)]
    public void A_paste_takes_the_clipboard_in_order(
        string clipboard, string box, string keys, bool handled, string taken, bool folded) => Ui.Run(() =>
    {
        var paste = new Paste(
            new FakeClipboard
            {
                Files = clipboard.Contains('f') ? [AFile()] : [],
                Text = clipboard.Contains('L') ? ALongPaste : clipboard.Contains('t') ? "a word" : null,
                Image = clipboard.Contains('i') ? Picture() : null,
            },
            takesFiles: box.Contains('F'), foldsLongText: box.Contains('N'),
            foldThrows: box.Contains('X'), takesImages: box.Contains('I'));

        var modifiers = KeyModifiers.None;
        if (keys.Contains("Ctrl")) modifiers |= KeyModifiers.Control;
        if (keys.Contains("Shift")) modifiers |= KeyModifiers.Shift;
        if (keys.Contains("Alt")) modifiers |= KeyModifiers.Alt;

        var e = paste.Press(modifiers, keys.EndsWith("Insert") ? Key.Insert : Key.V);

        Assert.Equal(handled, e.Handled);
        Assert.Equal(taken, $"{paste.FilesTaken},{paste.TextPasted},{paste.ImagesTaken}");
        Assert.Equal(folded ? ALongPaste : null, paste.Folded);
    });

    private static readonly string ALongPaste = new('x', mTiles.Services.PastedNote.LongEnoughCharacters + 1);

    private sealed class Paste(IComposerClipboard clipboard, bool takesFiles = true, bool foldsLongText = false,
        bool foldThrows = false, bool takesImages = true)
    {
        public int FilesTaken { get; private set; }
        public int TextPasted { get; private set; }
        public int ImagesTaken { get; private set; }
        public string? Folded { get; private set; }

        /// <summary>Every answer of the fake is already complete, so the paste has run by the time this returns.</summary>
        public KeyEventArgs Press(KeyModifiers modifiers, Key key = Key.V)
        {
            var e = new KeyEventArgs { Key = key, KeyModifiers = modifiers };
            ComposerPaste.TryPaste(clipboard, e, () => TextPasted++,
                takesImages ? _ => ImagesTaken++ : null,
                takesFiles ? TakeFiles : null, foldsLongText ? TakeLongText : null);
            return e;
        }

        private Task TakeFiles(IReadOnlyList<IStorageItem> files)
        {
            FilesTaken++;
            return Task.CompletedTask;
        }

        private Task TakeLongText(string text)
        {
            if (foldThrows) throw new IOException("no room");
            Folded = text;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClipboard : IComposerClipboard
    {
        public IReadOnlyList<IStorageItem> Files { get; init; } = [];
        public string? Text { get; init; }
        public bool HasText => !string.IsNullOrEmpty(Text);
        public Bitmap? Image { get; init; }

        public Task<IReadOnlyList<IStorageItem>> FilesAsync() => Task.FromResult(Files);
        public Task<bool> HasTextAsync() => Task.FromResult(HasText);
        public Task<string?> TextAsync() => Task.FromResult(Text);
        public Task<Bitmap?> BitmapAsync() => Task.FromResult(Image);
    }

    /// <summary>A real file on this machine: a storage item is not implementable outside Avalonia, so it is
    /// asked of the platform's own provider, the one a pasted file comes from.</summary>
    private static IStorageItem AFile()
    {
        var window = new Avalonia.Controls.Window();
        return window.StorageProvider.TryGetFileFromPathAsync(typeof(ComposerPasteTests).Assembly.Location)
            .GetAwaiter().GetResult() ?? throw new InvalidOperationException("The platform offered no file.");
    }

    private static Bitmap Picture() =>
        new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);

}
