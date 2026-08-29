using mTiles.Models;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Which tile a shortcut acts on, and the deliberate difference between that and where focus lands.
/// </summary>
/// <remarks>
/// <para><c>FocusActiveTile</c> falls back to any tile because focus has to be somewhere; the shortcut's
/// <c>ActiveTile</c> must not, and the reason is not symmetry. The first leaf is an arbitrary tile the
/// user is not looking at, and with <c>AutoSubmitEnter</c> on, delivering a transcript there does not
/// paste a sentence — it <b>runs a command</b> in a terminal nobody chose. Null instead: the sink then
/// tries whatever text control has the keyboard, and failing that the transcript is reported
/// undeliverable and quoted back.</para>
/// <para>Two rules in two methods one line apart, told apart by a boolean, is exactly the shape that
/// gets tidied into one by somebody who has not read this.</para>
/// </remarks>
public class ActiveTileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

    public ActiveTileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private WorkspaceViewModel Build(TempSettings settings) =>
        new(new Workspace { Name = "test", DirectoryPath = _directory }, settings.Layouts, settings.Service,
            TestTiles.Catalog(settings.Service));

    [Fact]
    public void Before_anything_is_activated_the_shortcut_has_no_tile_but_focus_still_lands()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var root = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
        var focused = 0;
        root.FocusRequested += () => focused++;

        Assert.Null(workspace.ActiveTile);

        workspace.FocusActiveTile();
        Assert.Equal(1, focused);
    }

    [Fact]
    public void Once_a_tile_has_been_worked_in_it_is_the_one_the_shortcut_aims_at()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var root = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
        root.Activate();

        Assert.Same(root, workspace.ActiveTile);
    }

    /// <summary>
    /// A remembered tile that is no longer in the tree is not a tile to dictate into.
    /// </summary>
    /// <remarks>
    /// After a tile is closed or the layout is rebuilt, the remembered leaf can point at something
    /// detached whose content has been disposed — a terminal nobody can see.
    /// </remarks>
    [Fact]
    public void A_tile_that_has_left_the_tree_is_not_offered_to_the_shortcut()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var root = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
        root.Activate();
        Assert.Same(root, workspace.ActiveTile);

        var replacement = new LeafTileNodeViewModel(TileKindIds.None, null, _directory,
            workspace.ActivationScope);
        var focused = 0;
        replacement.FocusRequested += () => focused++;
        workspace.RootTile = replacement;

        Assert.Null(workspace.ActiveTile);

        // And focus still has somewhere to go: that is the whole of the difference.
        workspace.FocusActiveTile();
        Assert.Equal(1, focused);
    }
}
