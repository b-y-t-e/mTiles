using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;
using mTiles.Services.Speech;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// A tile lets go of the application-scoped services it subscribed to.
/// </summary>
/// <remarks>
/// The dictation service lives as long as the process, so a tile still subscribed to its
/// <c>StateChanged</c> can never be collected — and it holds its content, its terminal and everything
/// those hold. Closing a single tile always went through <c>CloseAsync</c> and was fine; closing a whole
/// workspace disposed only the tiles' <em>content</em>, and leaked every tile in it.
/// </remarks>
public class LeafTileDisposalTests
{
    private sealed class SilentCapture : IAudioCapture
    {
        public bool IsAvailable => true;
        public bool IsRecording => false;
        public IReadOnlyList<string> GetInputDevices(bool rescan = false) => [];
        public void Start(string deviceName) { }
        public IRecordingHandle? Detach() => null;
        public float[] Finish(IRecordingHandle? detached) => [];
        public void Dispose() { }
    }

    private sealed class SilentEngine : ISpeechToTextEngine
    {
        public bool IsLoaded => false;
        public Task LoadAsync(string modelPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Unload() { }
        public Task<string> TranscribeAsync(float[] samples, TranscriptionOptions options,
            CancellationToken cancellationToken = default) => Task.FromResult("");
        public void Dispose() { }
    }

    private static (DictationService Service, LeafTileNodeViewModel Tile) Build(TempSettings settings)
    {
        var service = new DictationService(settings.Service, new SilentCapture(), new SilentEngine(),
            new SpeechModelStore(Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"))),
            action => action());

        // With content, and that is load-bearing now: the microphone is offered to a tile whose content
        // has somewhere to type — an ITextInputTile — rather than to one whose kind happens to be
        // "terminal". A tile with no content at all has nowhere to put a sentence.
        var content = new TerminalTileViewModel("", null, settings.Service);
        var tile = new LeafTileNodeViewModel(TileKindIds.Terminal, content, "", new TileActivationScope())
        {
            Dictation = service,
        };
        return (service, tile);
    }

    /// <summary>Sanity: while it is alive, the tile does listen — otherwise the test below would pass
    /// against a tile that never subscribed at all.</summary>
    [Fact]
    public void A_live_tile_reacts_to_the_service()
    {
        using var settings = new TempSettings();
        var (service, tile) = Build(settings);
        using var _ = service;

        settings.Service.Settings.Speech.Enabled = false;
        settings.Service.NotifyChanged();
        Assert.False(tile.CanDictate);

        settings.Service.Settings.Speech.Enabled = true;
        settings.Service.NotifyChanged();
        Assert.True(tile.CanDictate);
    }

    [Fact]
    public void A_disposed_tile_stops_listening_to_the_service()
    {
        using var settings = new TempSettings();
        var (service, tile) = Build(settings);
        using var _ = service;

        tile.Dispose();

        settings.Service.Settings.Speech.Enabled = false;
        settings.Service.NotifyChanged();

        // Not merely "did not throw": with the subscription gone the tile no longer offers dictation,
        // because disposing it dropped the service it would have used.
        Assert.False(tile.CanDictate);
        Assert.Null(tile.Dictation);
    }

    /// <summary>
    /// A tile made by splitting is configured by the workspace, not by whatever its parent copied.
    /// </summary>
    /// <remarks>
    /// Splitting the root rebuilds the whole tree through the workspace and hid this for a while;
    /// splitting anything else took the other branch, so from the second split onwards a new tile had no
    /// dictation service — no microphone button, ever — and was invisible to the active-tile tracking
    /// the shortcut aims at.
    /// </remarks>
    [Theory]
    [InlineData(true)]      // splitting the root, which reconfigures the tree afterwards
    [InlineData(false)]     // splitting a tile that already has a parent, which does not
    public void A_tile_created_by_splitting_inherits_the_dictation_service(bool splitTheRoot)
    {
        using var settings = new TempSettings();
        var (service, tile) = Build(settings);
        using var _ = service;

        var configured = 0;
        tile.ConfigureNewLeaf = leaf => { leaf.Dictation = service; leaf.ConfigureNewLeaf = tile.ConfigureNewLeaf; configured++; };

        tile.SplitVerticalCommand.Execute(null);
        var first = Assert.IsType<SplitTileNodeViewModel>(tile.Parent);

        var subject = splitTheRoot ? tile : Assert.IsType<LeafTileNodeViewModel>(first.Second);
        subject.SplitHorizontalCommand.Execute(null);

        var newest = Assert.IsType<LeafTileNodeViewModel>(
            Assert.IsType<SplitTileNodeViewModel>(subject.Parent).Second);

        Assert.Same(service, newest.Dictation);
        Assert.Equal(2, configured);
    }

    /// <summary>
    /// A tile nobody configured passes what it has on, rather than handing out a tile with nothing.
    /// </summary>
    /// <remarks>
    /// <para>The other branch of the same split. In the application it is unreachable — every tile in a
    /// workspace is given a configurator — so this covers tiles built by hand, which in practice means
    /// tests: without it a split leaf has no <c>LayoutChanged</c> and quietly stops saving the layout it
    /// just changed, which is exactly the kind of failure nothing notices.</para>
    /// <para>Pinned here because it is a second list of callbacks, and a second list is how the first
    /// one drifts. If it has to exist, it has to be watched.</para>
    /// </remarks>
    [Fact]
    public void Splitting_an_unconfigured_tile_passes_on_what_it_has()
    {
        using var settings = new TempSettings();
        var (service, tile) = Build(settings);
        using var _ = service;

        var saved = 0;
        tile.LayoutChanged = () => saved++;
        Assert.Null(tile.ConfigureNewLeaf);

        tile.SplitVerticalCommand.Execute(null);
        var newest = Assert.IsType<LeafTileNodeViewModel>(
            Assert.IsType<SplitTileNodeViewModel>(tile.Parent).Second);

        Assert.Same(service, newest.Dictation);

        var before = saved;
        newest.LayoutChanged?.Invoke();
        Assert.Equal(before + 1, saved);
    }

    /// <summary>
    /// A tile promoted when its sibling is closed is configured by the workspace, not by a list of
    /// callbacks somebody wrote down in the drag-and-drop code.
    /// </summary>
    /// <remarks>
    /// The same shape as the split, and the second of the two places that had it: three callbacks copied
    /// by hand, so anything the workspace added afterwards — the dictation service, for one — was simply
    /// not there on a re-parented tile. Fixing one copy and leaving the other is how a bug comes back
    /// wearing a different hat.
    /// </remarks>
    [Fact]
    public void A_tile_promoted_when_its_sibling_closes_is_configured_by_the_workspace()
    {
        using var settings = new TempSettings();
        var (service, tile) = Build(settings);
        using var _ = service;

        var configured = 0;
        tile.ConfigureNewLeaf = leaf =>
        {
            leaf.Dictation = service;
            leaf.ConfigureNewLeaf = tile.ConfigureNewLeaf;
            configured++;
        };

        tile.SplitVerticalCommand.Execute(null);
        var split = Assert.IsType<SplitTileNodeViewModel>(tile.Parent);
        var sibling = Assert.IsType<LeafTileNodeViewModel>(split.Second);

        // The sibling arrives without the service, as a tile built by hand would.
        sibling.Dictation = null;
        configured = 0;

        Assert.True(Views.TileDragDrop.DetachFromTree(tile));

        Assert.Equal(1, configured);
        Assert.Same(service, sibling.Dictation);
    }

    /// <summary>
    /// Disposing a tile disposes what is inside it.
    /// </summary>
    /// <remarks>
    /// These were two calls that every teardown had to remember to make: closing a single tile made
    /// both, closing a whole workspace made only the content one, and every tile in that workspace
    /// leaked with its terminal still subscribed to services that outlive it. Repeated, because both
    /// callers still exist and a tile closed by a command can be torn down with its workspace a moment
    /// later.
    /// </remarks>
    [Fact]
    public void Disposing_a_tile_disposes_its_content_once()
    {
        using var settings = new TempSettings();
        var (service, _) = Build(settings);
        using var _s = service;

        var content = new CountingContent();
        var tile = new LeafTileNodeViewModel(TileKindIds.Note, content, "", new TileActivationScope());

        tile.Dispose();
        tile.Dispose();

        Assert.Equal(1, content.Disposals);
    }

    private sealed class CountingContent : ObservableObject, ITile
    {
        public string KindId => TileKindIds.Note;
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
}
