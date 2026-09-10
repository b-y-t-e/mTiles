using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Double-clicking the empty part of a tile's header fills the workspace with it, and again puts the
/// layout back.
/// </summary>
/// <remarks>
/// <para>The gesture is a second route to <see cref="LeafTileNodeViewModel.ToggleMaximizeCommand"/>,
/// which is what makes it worth testing at the view rather than at the view model: what can go wrong
/// here is not the toggling but <em>where the click landed</em>. Three things in that strip answer a
/// double-click of their own — the buttons, the name label with its rename box, and the editor that box
/// opens — and every one of them is an ancestor-bound handler away from also filling the screen.</para>
/// <para>The event is raised rather than clicked twice with the headless mouse, because what is under
/// test is the routing and the guards. A synthetic pair of clicks would additionally depend on the
/// platform's double-tap interval being interpreted by a headless clock, which is a second thing to be
/// wrong about and none of it ours.</para>
/// </remarks>
public class TileHeaderDoubleClickTests
{
    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TileHeaderDoubleClickTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>A tile that can be maximized: the right content, a split above it and a scope to ask.
    /// </summary>
    /// <remarks>All three are what <see cref="LeafTileNodeViewModel.CanMaximize"/> reads, and the last
    /// one is the workspace's. A tile assembled with any of them missing answers no, which is a test
    /// that passes for the wrong reason.</remarks>
    private static (LeafTileNodeViewModel Leaf, LeafTileView View) Build(bool maximizable = true)
    {
        ITile content = maximizable ? new Maximizable() : new Fixed();
        var leaf = new LeafTileNodeViewModel(TileKindIds.Note, content, "", new TileActivationScope())
        {
            MaximizeScope = new TileMaximizeScope(),
        };
        var sibling = new LeafTileNodeViewModel(TileKindIds.None, null, "", new TileActivationScope());
        var split = new SplitTileNodeViewModel(Orientation.Vertical, leaf, sibling);
        leaf.Parent = split;
        sibling.Parent = split;

        var view = new LeafTileView { DataContext = leaf };
        var window = new Window { Content = view, Width = 500, Height = 300 };
        window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://mTiles/Styles/"))
        {
            Source = new Uri("avares://mTiles/Styles/AppTheme.axaml"),
        });
        window.Show();

        return (leaf, view);
    }

    private static void DoubleClick(Control target) =>
        target.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));

    /// <summary>The gesture both ways: in on the first double-click, out on the second.</summary>
    /// <remarks>Out matters as much as in — it is the same rule the button follows, and a header that
    /// only maximizes leaves the mouse no way back from a view that has hidden every other tile.
    /// </remarks>
    [Fact]
    public void A_double_click_on_the_header_fills_the_workspace_and_gives_it_back() => OnUiThread(() =>
    {
        var (leaf, view) = Build();
        var toolbar = view.FindControl<Border>("TileToolbar")!;

        DoubleClick(toolbar);
        Assert.True(leaf.IsMaximized);

        DoubleClick(toolbar);
        Assert.False(leaf.IsMaximized);
    });

    /// <summary>A kind that lays itself out in panes of its own does not answer the gesture.</summary>
    /// <remarks>The same condition the button and the menu entry are hidden by, said at the one entry
    /// point that has nothing to hide: a header is always there to be double-clicked.</remarks>
    [Fact]
    public void A_tile_that_cannot_be_maximized_ignores_it() => OnUiThread(() =>
    {
        var (leaf, view) = Build(maximizable: false);

        DoubleClick(view.FindControl<Border>("TileToolbar")!);

        Assert.False(leaf.IsMaximized);
    });

    /// <summary>Double-clicking the name renames the tile and nothing else.</summary>
    /// <remarks>The gesture that was there first, and the one this feature could most easily take: the
    /// label sits inside the header, so its double-click reaches the header's handler on the way up
    /// unless the label claims it. Failing, this is a rename box opened over a tile that has just gone
    /// full screen.</remarks>
    [Fact]
    public void A_double_click_on_the_name_renames_rather_than_maximizing() => OnUiThread(() =>
    {
        var (leaf, view) = Build();

        DoubleClick(view.FindControl<TextBlock>("TileNameLabel")!);

        Assert.False(leaf.IsMaximized);
        Assert.True(view.FindControl<TextBox>("TileNameEditor")!.IsVisible);
    });

    /// <summary>A button's own second press is not a gesture on the header.</summary>
    /// <remarks>Restart shell and New session are among the most pressed things in the application, and
    /// pressing either twice quickly is ordinary. Without the guard the second press also fills the
    /// workspace.</remarks>
    [Fact]
    public void A_double_click_on_a_button_is_not_the_headers_gesture() => OnUiThread(() =>
    {
        var (leaf, view) = Build();

        DoubleClick(view.FindControl<Button>("RestartButton")!);

        Assert.False(leaf.IsMaximized);
    });

    private sealed class Maximizable : ObservableObject, IMaximizableTile
    {
        public string KindId => TileKindIds.Note;
        public void Dispose() { }
    }

    private sealed class Fixed : ObservableObject, ITile
    {
        public string KindId => TileKindIds.Git;
        public void Dispose() { }
    }
}
