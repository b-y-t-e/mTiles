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
        var paste = new Paste(new FakeClipboard { Files = [AFile()], HasText = true });

        var e = paste.Press(KeyModifiers.Control);

        Assert.True(e.Handled);
        Assert.Equal(1, paste.FilesTaken);
        Assert.Equal(0, paste.TextPasted);
    });

    [Fact]
    public void Ctrl_V_pastes_text_when_there_are_no_files_and_leaves_the_image() => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { HasText = true, Image = Picture() });

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
        var paste = new Paste(new FakeClipboard { HasText = true, Image = Picture() }, takesFiles: false);

        var e = paste.Press(KeyModifiers.Control);

        Assert.False(e.Handled);
        Assert.Equal(0, paste.TextPasted);
        Assert.Equal(0, paste.ImagesTaken);
    });

    [Fact]
    public void Alt_V_attaches_files_first_and_otherwise_the_image_over_any_text() => OnUiThread(() =>
    {
        var withFiles = new Paste(new FakeClipboard { Files = [AFile()], HasText = true, Image = Picture() });
        var withImage = new Paste(new FakeClipboard { HasText = true, Image = Picture() });

        Assert.True(withFiles.Press(KeyModifiers.Alt).Handled);
        Assert.True(withImage.Press(KeyModifiers.Alt).Handled);

        Assert.Equal((1, 0), (withFiles.FilesTaken, withFiles.ImagesTaken));
        Assert.Equal((0, 1), (withImage.FilesTaken, withImage.ImagesTaken));
        Assert.Equal(0, withImage.TextPasted);
    });

    [Fact]
    public void Shift_V_is_not_a_paste() => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { Files = [AFile()], HasText = true });

        var e = paste.Press(KeyModifiers.Shift);

        Assert.False(e.Handled);
        Assert.Equal((0, 0, 0), (paste.FilesTaken, paste.TextPasted, paste.ImagesTaken));
    });

    [Theory]
    [InlineData(Key.V, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(Key.Insert, KeyModifiers.Shift)]
    public void Every_paste_key_of_the_box_attaches_copied_files(Key key, KeyModifiers modifiers) => OnUiThread(() =>
    {
        var paste = new Paste(new FakeClipboard { Files = [AFile()], HasText = true });

        var e = paste.Press(modifiers, key);

        Assert.True(e.Handled);
        Assert.Equal((1, 0, 0), (paste.FilesTaken, paste.TextPasted, paste.ImagesTaken));
    });

    private sealed class Paste(IComposerClipboard clipboard, bool takesFiles = true)
    {
        public int FilesTaken { get; private set; }
        public int TextPasted { get; private set; }
        public int ImagesTaken { get; private set; }

        /// <summary>Every answer of the fake is already complete, so the paste has run by the time this returns.</summary>
        public KeyEventArgs Press(KeyModifiers modifiers, Key key = Key.V)
        {
            var e = new KeyEventArgs { Key = key, KeyModifiers = modifiers };
            ComposerPaste.TryPaste(clipboard, e, () => TextPasted++, _ => ImagesTaken++,
                takesFiles ? TakeFiles : null);
            return e;
        }

        private Task TakeFiles(IReadOnlyList<IStorageItem> files)
        {
            FilesTaken++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClipboard : IComposerClipboard
    {
        public IReadOnlyList<IStorageItem> Files { get; init; } = [];
        public bool HasText { get; init; }
        public Bitmap? Image { get; init; }

        public Task<IReadOnlyList<IStorageItem>> FilesAsync() => Task.FromResult(Files);
        public Task<bool> HasTextAsync() => Task.FromResult(HasText);
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
