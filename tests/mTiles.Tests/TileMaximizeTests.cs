using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using mTiles.Models;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// One tile filling the workspace, and finding the others again afterwards.
/// </summary>
/// <remarks>
/// <para>Maximizing is a way of looking at a layout and not a change to one, so what these tests hold
/// on to is that everything survives the round trip: the tree is the tree it was, the tile that filled
/// the screen is the same control it always was — its terminal is not rebuilt — and no route out of the
/// state leaves a split soloed on a child nothing can reach, which would be half a workspace invisible
/// for the rest of the session.</para>
/// <para>The exits are as much of the feature as the entrance, which is why closing and splitting have
/// tests of their own: both leave the maximized leaf pointing at parents it no longer has.</para>
/// </remarks>
public class TileMaximizeTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

    public TileMaximizeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private WorkspaceViewModel Build(TempSettings settings) =>
        new(new Workspace { Name = "test", DirectoryPath = _directory }, settings.Layouts,
            settings.Service, TestTiles.Catalog(settings.Service));

    /// <summary>Gives an empty tile content of that kind, the way the chooser does.</summary>
    private static void Make(LeafTileNodeViewModel leaf, string kindId)
    {
        leaf.SelectKindCommand.Execute(kindId);
        if (leaf.IsChoosingSetup)
            leaf.SelectSetupOptionCommand.Execute(leaf.SetupOptions.First());
    }

    /// <summary>A terminal, split down, with an empty tile in the second half.</summary>
    /// <remarks>What a tile can do depends on where it hangs as much as on what it holds, so anything
    /// asking about the offer asks it of a tile with a split above it — the arrangement every tile in a
    /// workspace of more than one is in.</remarks>
    private static (SplitTileNodeViewModel Split, LeafTileNodeViewModel Second) SplitTerminal(
        WorkspaceViewModel workspace)
    {
        var root = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
        Make(root, TileKindIds.Terminal);
        root.SplitVerticalCommand.Execute(null);

        var split = Assert.IsType<SplitTileNodeViewModel>(workspace.RootTile);
        return (split, Assert.IsType<LeafTileNodeViewModel>(split.Second));
    }

    /// <summary>A terminal, split down, with a note in the second half.</summary>
    private static (SplitTileNodeViewModel Split, LeafTileNodeViewModel Terminal, LeafTileNodeViewModel Note)
        TerminalAndNote(WorkspaceViewModel workspace)
    {
        var root = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
        Make(root, TileKindIds.Terminal);
        root.SplitVerticalCommand.Execute(null);

        var split = Assert.IsType<SplitTileNodeViewModel>(workspace.RootTile);
        var second = Assert.IsType<LeafTileNodeViewModel>(split.Second);
        Make(second, TileKindIds.Note);

        return (split, Assert.IsType<LeafTileNodeViewModel>(split.First), second);
    }

    /// <summary>The four kinds whose content is simply more of the same at a larger size say yes.
    /// </summary>
    /// <remarks>And the ones that lay themselves out in panes of their own say no — the list is the
    /// decision <see cref="IMaximizableTile"/> documents, so it is worth a test rather than being
    /// re-derived by whoever adds the ninth kind.</remarks>
    [Theory]
    [InlineData(TileKindIds.Terminal, true)]
    [InlineData(TileKindIds.Agent, true)]
    [InlineData(TileKindIds.Note, true)]
    [InlineData(TileKindIds.Todo, true)]
    [InlineData(TileKindIds.Git, false)]
    [InlineData(TileKindIds.Database, false)]
    [InlineData(TileKindIds.Goal, false)]
    [InlineData(TileKindIds.Usage, false)]
    public void Only_the_kinds_that_gain_from_the_room_offer_it(string kindId, bool expected)
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (_, leaf) = SplitTerminal(workspace);
        Make(leaf, kindId);

        Assert.Equal(expected, leaf.CanMaximize);
    }

    /// <summary>An empty tile has nothing to make full screen yet.</summary>
    [Fact]
    public void A_tile_with_no_content_does_not_offer_it()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (_, empty) = SplitTerminal(workspace);

        Assert.False(empty.CanMaximize);
    }

    /// <summary>The only tile in a workspace has no full screen to go to.</summary>
    /// <remarks>It already fills the workspace — which is the arrangement a first run opens with — so
    /// the gesture would change nothing on screen while lighting the button, turning the glyph into the
    /// way out and standing the split buttons down: a state the user is offered an exit from without
    /// ever having gone into one.</remarks>
    [Fact]
    public void The_only_tile_in_a_workspace_does_not_offer_it()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var lone = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
        Make(lone, TileKindIds.Terminal);

        Assert.False(lone.CanMaximize);

        lone.ToggleMaximizeCommand.Execute(null);

        Assert.False(lone.IsMaximized);
    }

    [Fact]
    public void Maximizing_solos_the_split_above_the_tile_and_restoring_clears_it()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (split, terminal, note) = TerminalAndNote(workspace);

        terminal.ToggleMaximizeCommand.Execute(null);

        Assert.Same(terminal, split.Solo);
        Assert.True(terminal.IsMaximized);
        Assert.False(note.IsMaximized);

        terminal.ToggleMaximizeCommand.Execute(null);

        Assert.Null(split.Solo);
        Assert.False(terminal.IsMaximized);
        // The tree is untouched either way: what moved was the way it is drawn.
        Assert.Same(terminal, split.First);
        Assert.Same(note, split.Second);
    }

    /// <summary>Every split between the root and the tile, not only the one just above it.</summary>
    [Fact]
    public void Maximizing_a_tile_deep_inside_solos_the_whole_path_to_the_root()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (outer, terminal, _) = TerminalAndNote(workspace);
        terminal.SplitHorizontalCommand.Execute(null);
        var inner = Assert.IsType<SplitTileNodeViewModel>(outer.First);

        terminal.ToggleMaximizeCommand.Execute(null);

        Assert.Same(inner, outer.Solo);
        Assert.Same(terminal, inner.Solo);
    }

    /// <summary>Maximizing a second tile puts the first one back rather than soloing two paths.</summary>
    [Fact]
    public void Only_one_tile_has_the_workspace_at_a_time()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (split, terminal, note) = TerminalAndNote(workspace);

        terminal.ToggleMaximizeCommand.Execute(null);
        note.ToggleMaximizeCommand.Execute(null);

        Assert.Same(note, split.Solo);
        Assert.False(terminal.IsMaximized);
        Assert.True(note.IsMaximized);
    }

    /// <summary>
    /// Closing the tile that is filling the workspace brings the rest of it back.
    /// </summary>
    /// <remarks>The failure this exists for is silent: the split stays soloed on a leaf that has been
    /// taken out of the tree, so the workspace draws a tile that no longer belongs to it and the tiles
    /// that do are unreachable.</remarks>
    [Fact]
    public async Task Closing_a_maximized_tile_puts_the_layout_back()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (split, terminal, _) = TerminalAndNote(workspace);
        terminal.ToggleMaximizeCommand.Execute(null);

        await terminal.CloseCommand.ExecuteAsync(null);

        Assert.Null(split.Solo);
    }

    /// <summary>A tile closed in the background leaves the maximized one filling the workspace.
    /// </summary>
    /// <remarks>Closing the only other tile lifts this one into the root's place, so the split it was
    /// soloed on is gone from the tree. The tile still has the whole workspace — it is the whole tree —
    /// and what goes with the split is the full-screen state itself: there is nothing left to exit, and
    /// a header still offering the way out would be offering it out of the only layout there is.
    /// </remarks>
    [Fact]
    public async Task Closing_another_tile_does_not_put_the_layout_back()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (split, terminal, note) = TerminalAndNote(workspace);
        terminal.ToggleMaximizeCommand.Execute(null);

        await note.CloseCommand.ExecuteAsync(null);

        Assert.Same(terminal, workspace.RootTile);
        Assert.Null(split.Solo);
        Assert.False(terminal.IsMaximized);
        Assert.False(terminal.CanMaximize);
    }

    /// <summary>
    /// A close that re-shapes the tree above the maximized tile solos the splits it has now.
    /// </summary>
    /// <remarks>The failure this exists for is a header that lies: the outer split falls out of the
    /// tree when its other child goes, so the view draws the surviving split in full — both tiles
    /// visible — while the tile still calls itself maximized and its first press does nothing.
    /// </remarks>
    [Fact]
    public async Task Closing_a_tile_the_maximized_one_was_drawn_through_re_solos_what_is_left()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (outer, terminal, note) = TerminalAndNote(workspace);
        terminal.SplitHorizontalCommand.Execute(null);
        var inner = Assert.IsType<SplitTileNodeViewModel>(outer.First);

        terminal.ToggleMaximizeCommand.Execute(null);
        Assert.Same(inner, outer.Solo);

        await note.CloseCommand.ExecuteAsync(null);

        Assert.Same(inner, workspace.RootTile);
        Assert.Same(terminal, inner.Solo);
        Assert.Null(outer.Solo);
        Assert.True(terminal.IsMaximized);
    }

    /// <summary>Splitting a maximized tile shows the layout again, with the new tile in it.</summary>
    /// <remarks>The new tile is created beside the maximized one, which is off screen by definition —
    /// so a split that kept the full-screen view would open a tile nobody can see. It is also what keeps
    /// the remembered path honest: the split inserted above this leaf is one the scope never soloed.
    /// </remarks>
    [Fact]
    public void Splitting_a_maximized_tile_puts_the_layout_back()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (split, terminal, _) = TerminalAndNote(workspace);
        terminal.ToggleMaximizeCommand.Execute(null);

        terminal.SplitVerticalCommand.Execute(null);

        Assert.Null(split.Solo);
        Assert.False(terminal.IsMaximized);
        Assert.Null(Assert.IsType<SplitTileNodeViewModel>(split.First).Solo);
    }

    /// <summary>
    /// On screen, a soloed split draws that child alone — and the same control it always held.
    /// </summary>
    /// <remarks>The identity is the point. A full-screen view built as a second view of the same tile
    /// would work for everything except the one thing a terminal cannot survive: its control lives in
    /// the view, so the tile would come back from full screen with an empty shell and a lost
    /// scrollback.</remarks>
    [Fact]
    public void The_maximized_tile_keeps_the_control_it_had_in_the_layout()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TileMaximizeTests).Assembly);
        session.Dispatch(() =>
        {
            var first = new LeafTileNodeViewModel(TileKindIds.None, null, "", new TileActivationScope());
            var second = new LeafTileNodeViewModel(TileKindIds.None, null, "", new TileActivationScope());
            var split = new SplitTileNodeViewModel(Orientation.Vertical, first, second)
            {
                Solo = null
            };
            first.Parent = split;
            second.Parent = split;

            var view = new TileNodeView { DataContext = split };
            var window = new Window { Content = view, Width = 400, Height = 300 };
            window.Show();

            var grid = Assert.IsType<Grid>(view.Content);
            var firstPane = Assert.IsType<TileNodeView>(grid.Children[0]);
            var leafView = firstPane.Content;

            split.Solo = first;

            Assert.Same(firstPane, view.Content);
            Assert.Same(leafView, firstPane.Content);

            split.Solo = null;

            var back = Assert.IsType<Grid>(view.Content);
            Assert.Same(firstPane, back.Children[0]);
            Assert.Same(leafView, firstPane.Content);

            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
