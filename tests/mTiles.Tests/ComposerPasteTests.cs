using Avalonia;
using Avalonia.Headless;
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
    [Fact]
    public void Ctrl_V_attaches_copied_files_instead_of_pasting_their_paths() => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { Files = [AFile()], Text = "a word" });

        var e = paste.Press(KeyModifiers.Control);

        Assert.True(e.Handled);
        Assert.Equal(1, paste.FilesTaken);
        Assert.Equal(0, paste.TextPasted);
    });

    [Fact]
    public void Ctrl_V_pastes_text_when_there_are_no_files_and_leaves_the_image() => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { Text = "a word", Image = Picture() });

        var e = paste.Press(KeyModifiers.Control);

        Assert.True(e.Handled);
        Assert.Equal(1, paste.TextPasted);
        Assert.Equal(0, paste.ImagesTaken);
    });

    [Fact]
    public void Ctrl_V_takes_the_image_when_there_is_neither_files_nor_text() => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { Image = Picture() });

        paste.Press(KeyModifiers.Control);

        Assert.Equal(1, paste.ImagesTaken);
        Assert.Equal(0, paste.TextPasted);
    });

    [Fact]
    public void Ctrl_V_is_left_to_the_box_where_the_box_takes_no_files() => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { Text = "a word", Image = Picture() }, takesFiles: false);

        var e = paste.Press(KeyModifiers.Control);

        Assert.False(e.Handled);
        Assert.Equal(0, paste.TextPasted);
        Assert.Equal(0, paste.ImagesTaken);
    });

    [Fact]
    public void Alt_V_attaches_files_first_and_otherwise_the_image_over_any_text() => OnUiThread(() =>
    {
        var withFiles = new Paste(new FakeClipboard { Files = [AFile()], Text = "a word", Image = Picture() });
        var withImage = new Paste(new FakeClipboard { Text = "a word", Image = Picture() });

        Assert.True(withFiles.Press(KeyModifiers.Alt).Handled);
        Assert.True(withImage.Press(KeyModifiers.Alt).Handled);

        Assert.Equal((1, 0), (withFiles.FilesTaken, withFiles.ImagesTaken));
        Assert.Equal((0, 1), (withImage.FilesTaken, withImage.ImagesTaken));
        Assert.Equal(0, withImage.TextPasted);
    });

    [Fact]
    public void Shift_V_is_not_a_paste() => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { Files = [AFile()], Text = "a word" });

        var e = paste.Press(KeyModifiers.Shift);

        Assert.False(e.Handled);
        Assert.Equal((0, 0, 0), (paste.FilesTaken, paste.TextPasted, paste.ImagesTaken));
    });

    [Theory]
    [InlineData(Key.V, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(Key.Insert, KeyModifiers.Shift)]
    public void Every_paste_key_of_the_box_attaches_copied_files(Key key, KeyModifiers modifiers) => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { Files = [AFile()], Text = "a word" });

        var e = paste.Press(modifiers, key);

        Assert.True(e.Handled);
        Assert.Equal((1, 0, 0), (paste.FilesTaken, paste.TextPasted, paste.ImagesTaken));
    });


    [Fact]
    public void A_long_paste_is_folded_into_a_note_and_never_reaches_the_box() => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { Text = ALongPaste }, foldsLongText: true);

        var e = paste.Press(KeyModifiers.Control);

        Assert.True(e.Handled);
        Assert.Equal(ALongPaste, paste.Folded);
        Assert.Equal(0, paste.TextPasted);
    });

    [Fact]
    public void A_short_paste_is_still_the_boxs_own() => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { Text = "two words" }, foldsLongText: true);

        paste.Press(KeyModifiers.Control);

        Assert.Null(paste.Folded);
        Assert.Equal(1, paste.TextPasted);
    });

    /// <summary>Ctrl+Shift+V is the way past the folding, which is the one thing that tells the three
    /// paste keys apart.</summary>
    [Fact]
    public void Ctrl_Shift_V_pastes_a_long_text_as_it_stands() => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { Text = ALongPaste }, foldsLongText: true);

        paste.Press(KeyModifiers.Control | KeyModifiers.Shift);

        Assert.Null(paste.Folded);
        Assert.Equal(1, paste.TextPasted);
    });

    /// <summary>A note that could not be taken is still a paste: it goes into the box rather than nowhere.</summary>
    [Fact]
    public void A_fold_that_fails_falls_back_to_pasting_the_text() => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { Text = ALongPaste }, foldsLongText: true, foldThrows: true);

        paste.Press(KeyModifiers.Control);

        Assert.Equal(1, paste.TextPasted);
    });

    [Fact]
    public void Files_still_come_before_a_long_text() => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { Files = [AFile()], Text = ALongPaste }, foldsLongText: true);

        paste.Press(KeyModifiers.Control);

        Assert.Equal(1, paste.FilesTaken);
        Assert.Null(paste.Folded);
    });

    /// <summary>The Goal tile's plan field: no image, no files, and a long paste still folded.</summary>
    [Fact]
    public void A_box_that_takes_neither_files_nor_images_still_folds_a_long_paste() => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { Text = ALongPaste }, takesFiles: false, foldsLongText: true);

        var e = paste.Press(KeyModifiers.Control);

        Assert.True(e.Handled);
        Assert.Equal(ALongPaste, paste.Folded);
        Assert.Equal(0, paste.TextPasted);
    });

    /// <summary>And a key nothing there wants is left to the box, as it always was.</summary>
    [Fact]
    public void Alt_V_is_not_taken_from_a_box_that_takes_neither() => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { Image = Picture() }, takesFiles: false, foldsLongText: true,
            takesImages: false);

        Assert.False(paste.Press(KeyModifiers.Alt).Handled);
        Assert.Equal(0, paste.ImagesTaken);
    });

    /// <summary>A box that takes only files still gets them, so nothing decides that a second time.</summary>
    [Fact]
    public void A_box_that_takes_only_files_takes_them() => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { Files = [AFile()] }, takesImages: false);

        var e = paste.Press(KeyModifiers.Control);

        Assert.True(e.Handled);
        Assert.Equal(1, paste.FilesTaken);
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

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ComposerPasteTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }
}
