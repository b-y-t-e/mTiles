using System.ComponentModel;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The signal a tile gets when it becomes the active one — what closes the Goal tile's stale-buttons
/// gap without a watcher over the worktree.
/// </summary>
/// <remarks>
/// Three things fail separately and are tested separately: it has to arrive at all, it has to arrive
/// <em>once</em> per transition (clicking a tile that is already active re-Activates it, and a git call
/// per click is exactly what the design rejected), and it must not arrive on the way out.
/// </remarks>
public class ActivatableTileTests
{
    private sealed class CountingTile : IActivatableTile
    {
        public int Activations { get; private set; }
        public string KindId => "counting";
        // Never raised here: the leaf subscribes to it, and an auto event nothing raises is a warning.
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public void OnActivated() => Activations++;
        public void Dispose() { }
    }

    private sealed class PlainTile : ITile
    {
        public string KindId => "plain";
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public void Dispose() { }
    }

    private static LeafTileNodeViewModel Leaf(TileActivationScope scope, ITile content) =>
        new("counting", content, workingDirectory: ".", scope);

    [Fact]
    public void Becoming_active_tells_the_content()
    {
        var scope = new TileActivationScope();
        var content = new CountingTile();
        var leaf = Leaf(scope, content);

        leaf.Activate();

        Assert.True(leaf.IsActive);
        Assert.Equal(1, content.Activations);
    }

    [Fact]
    public void Activating_the_tile_that_is_already_active_says_nothing()
    {
        var scope = new TileActivationScope();
        var content = new CountingTile();
        var leaf = Leaf(scope, content);

        leaf.Activate();
        leaf.Activate();
        leaf.Activate();

        Assert.Equal(1, content.Activations);
    }

    [Fact]
    public void Losing_the_active_tile_says_nothing_and_getting_it_back_says_it_again()
    {
        var scope = new TileActivationScope();
        var content = new CountingTile();
        var first = Leaf(scope, content);
        var second = Leaf(scope, new PlainTile());

        first.Activate();
        second.Activate();

        Assert.False(first.IsActive);
        Assert.Equal(1, content.Activations);

        first.Activate();
        Assert.Equal(2, content.Activations);
    }

    [Fact]
    public void Content_that_asked_for_nothing_is_asked_nothing()
    {
        var scope = new TileActivationScope();
        var leaf = Leaf(scope, new PlainTile());

        leaf.Activate();

        Assert.True(leaf.IsActive);
    }

    [Fact]
    public void A_disposed_tile_is_not_told()
    {
        var scope = new TileActivationScope();
        var content = new CountingTile();
        var leaf = Leaf(scope, content);

        leaf.Dispose();
        leaf.Activate();

        Assert.Equal(0, content.Activations);
    }
}
