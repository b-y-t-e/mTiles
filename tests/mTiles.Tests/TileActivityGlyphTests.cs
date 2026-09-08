using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;
using Material.Icons.Avalonia;
using mTiles.Models;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The mark on a tile's own header: what it is doing, in the slot its kind's icon usually has.
/// </summary>
/// <remarks>
/// The same class of failure <c>DictationIndicatorTests</c> was written for — a glyph that never
/// changes, or one that stays turning after the work has stopped, both compile and both leave a tile
/// lying about itself. The workspace row already had its mark tested one layer up; this is the half a
/// user actually looks at while deciding whether to go back to a tile.
/// </remarks>
public class TileActivityGlyphTests
{
    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TileActivityGlyphTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static (Content Tile, MaterialIcon Glyph) Build()
    {
        var content = new Content();
        var leaf = new LeafTileNodeViewModel(TileKindIds.Note, content, "", new TileActivationScope());
        var view = new LeafTileView { DataContext = leaf };
        var window = new Window { Content = view, Width = 400, Height = 300 };

        window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://mTiles/Styles/"))
        {
            Source = new Uri("avares://mTiles/Styles/AppTheme.axaml"),
        });
        window.Show();

        return (content, view.FindControl<MaterialIcon>("TileTypeGlyph")!);
    }

    /// <summary>An idle tile wears its kind, which is the state it is in nearly all the time.</summary>
    [Fact]
    public void A_tile_with_nothing_to_report_shows_its_kind() => OnUiThread(() =>
    {
        var (tile, glyph) = Build();

        tile.Report(TileActivity.Idle);

        Assert.NotEqual(MaterialIconKind.Loading, glyph.Kind);
        Assert.DoesNotContain("spinning", glyph.Classes);
    });

    /// <summary>Working turns; blocked does not, and says so in the danger colour.</summary>
    /// <remarks>Two treatments because they mean opposite things: a turning arc says work is in
    /// progress and the tile can be left alone, while a still mark says nothing is moving and will not
    /// until somebody answers. Folded into one glyph they would report "something is happening" for
    /// both.</remarks>
    [Fact]
    public void Working_turns_and_blocked_stands_still() => OnUiThread(() =>
    {
        var (tile, glyph) = Build();

        tile.Report(TileActivity.Working);
        Assert.Equal(MaterialIconKind.Loading, glyph.Kind);
        Assert.Contains("spinning", glyph.Classes);

        tile.Report(TileActivity.Blocked);
        Assert.Equal(MaterialIconKind.AlertCircleOutline, glyph.Kind);
        Assert.DoesNotContain("spinning", glyph.Classes);
    });

    /// <summary>And it goes back to the kind when the work stops.</summary>
    /// <remarks>The half that rots quietly: a mark that arrives is noticed the first time it is wrong,
    /// while one that never leaves looks like a tile that is still busy — which is exactly the state
    /// nobody goes back to check.</remarks>
    [Fact]
    public void The_mark_goes_when_the_work_does() => OnUiThread(() =>
    {
        var (tile, glyph) = Build();

        tile.Report(TileActivity.Working);
        Assert.Contains("spinning", glyph.Classes);

        tile.Report(TileActivity.Idle);

        Assert.NotEqual(MaterialIconKind.Loading, glyph.Kind);
        Assert.NotEqual(MaterialIconKind.AlertCircleOutline, glyph.Kind);
        Assert.DoesNotContain("spinning", glyph.Classes);
    });

    /// <summary>Content whose activity the test drives.</summary>
    private sealed class Content : ObservableObject, IBusyTile
    {
        private TileActivity _activity = TileActivity.Idle;

        public string KindId => TileKindIds.Note;
        public TileActivity Activity => _activity;

        public void Report(TileActivity activity)
        {
            _activity = activity;
            OnPropertyChanged(nameof(Activity));
        }

        public void Dispose() { }
    }
}
