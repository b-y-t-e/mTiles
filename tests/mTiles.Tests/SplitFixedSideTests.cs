using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// A split whose one side is held at a size in pixels while the other takes whatever is left.
/// </summary>
/// <remarks>
/// <para>What the window's own layout needs and a workspace never uses: a list of names wants the width
/// a name needs and a strip of tabs one row, whatever size the window is. So the first thing asserted is
/// that nothing about an ordinary split has moved — its file, its lengths, where its splitter comes to
/// rest — and only then what a fixed side does.</para>
/// </remarks>
public class SplitFixedSideTests
{
    private readonly TileActivationScope _scope = new();

    // ---- persistence -------------------------------------------------------------------------------

    /// <summary>A split with nothing fixed is written exactly as it was before the field existed.</summary>
    /// <remarks>Every workspace layout on somebody's disk is one of these, and a key appearing in all of
    /// them on the next save would be a change in every file for a feature none of them uses.</remarks>
    [Fact]
    public void An_ordinary_split_writes_no_fixed_side()
    {
        using var settings = new TempSettings();
        var serializer = Serializer(settings);

        var json = JsonSerializer.Serialize(serializer.Serialize(Split(Leaf(), Leaf())), JsonDefaults.Options);

        Assert.DoesNotContain(nameof(TileNode.FixedSide), json);
        Assert.DoesNotContain(nameof(TileNode.FixedExtent), json);
    }

    [Fact]
    public void A_fixed_side_survives_the_file()
    {
        using var settings = new TempSettings();
        var serializer = Serializer(settings);

        var split = Split(Leaf(), Leaf());
        split.SplitRatio = 0.3;
        split.Fix(SplitFixedSide.Second, 240);

        var json = JsonSerializer.Serialize(serializer.Serialize(split), JsonDefaults.Options);
        var node = JsonSerializer.Deserialize<TileNode>(json, JsonDefaults.Options)!;

        var saves = 0;
        var read = Assert.IsType<SplitTileNodeViewModel>(serializer.Deserialize(node, () => saves++).Root);

        Assert.Equal(SplitFixedSide.Second, read.FixedSide);
        Assert.Equal(240, read.FixedExtent);
        // The ratio beside it is kept, so letting the side go puts the split back where it was.
        Assert.Equal(0.3, read.SplitRatio);
        // Reading a layout is not a change to it.
        Assert.Equal(0, saves);
    }

    /// <summary>A side named with no usable size comes back as an ordinary split, not as a broken one.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-40.0)]
    [InlineData(double.NaN)]
    public void A_fixed_side_without_a_usable_size_is_read_as_none(double? extent)
    {
        using var settings = new TempSettings();
        var serializer = Serializer(settings);

        var node = serializer.Serialize(Split(Leaf(), Leaf()))!;
        node.FixedSide = SplitFixedSide.First;
        node.FixedExtent = extent;

        var read = Assert.IsType<SplitTileNodeViewModel>(serializer.Deserialize(node, () => { }).Root);

        Assert.Equal(SplitFixedSide.None, read.FixedSide);
    }

    /// <summary>A size no pane can be held at fixes nothing, so it cannot be saved as a side read back as none.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-40.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Fixing_a_side_at_an_unusable_size_fixes_nothing(double extent)
    {
        var split = Split(Leaf(), Leaf());

        split.Fix(SplitFixedSide.First, extent);

        Assert.Equal(SplitFixedSide.None, split.FixedSide);
    }

    /// <summary>A fixed side wider than the split leaves the pane beside it its minimum.</summary>
    [Theory]
    [InlineData(50, 50, 192, 142)]
    [InlineData(50, 108, 400, 292)]
    [InlineData(50, 50, 0, double.PositiveInfinity)]
    public void A_fixed_side_is_capped_so_the_other_pane_keeps_its_minimum(
        double fixedMinimum, double flexibleMinimum, double available, double expected)
    {
        Assert.Equal(expected, TileMinimumSize.FixedMaximum(fixedMinimum, flexibleMinimum, available));
    }

    // ---- the view -----------------------------------------------------------------------------------

    [Fact]
    public void The_fixed_side_is_laid_out_in_pixels_and_the_other_takes_the_rest()
    {
        var split = Split(Leaf(), Leaf());

        var (first, second) = TileNodeView.LengthsFor(split);
        Assert.Equal(new GridLength(0.5, GridUnitType.Star), first);
        Assert.Equal(new GridLength(0.5, GridUnitType.Star), second);

        split.Fix(SplitFixedSide.First, 240);
        (first, second) = TileNodeView.LengthsFor(split);
        Assert.Equal(new GridLength(240, GridUnitType.Pixel), first);
        Assert.Equal(new GridLength(1, GridUnitType.Star), second);

        split.Fix(SplitFixedSide.Second, 40);
        (first, second) = TileNodeView.LengthsFor(split);
        Assert.Equal(new GridLength(1, GridUnitType.Star), first);
        Assert.Equal(new GridLength(40, GridUnitType.Pixel), second);
    }

    /// <summary>Dragging the splitter of a fixed side moves its pixels and leaves the ratio alone.</summary>
    [Fact]
    public void The_splitter_of_a_fixed_side_stores_pixels()
    {
        var split = Split(Leaf(), Leaf());
        split.SplitRatio = 0.3;
        split.Fix(SplitFixedSide.First, 240);

        TileNodeView.StoreRest(split,
            new GridLength(310, GridUnitType.Pixel), new GridLength(1, GridUnitType.Star));

        Assert.Equal(310, split.FixedExtent);
        Assert.Equal(0.3, split.SplitRatio);
    }

    [Fact]
    public void The_splitter_of_an_ordinary_split_still_stores_a_ratio()
    {
        var split = Split(Leaf(), Leaf());

        TileNodeView.StoreRest(split,
            new GridLength(3, GridUnitType.Star), new GridLength(1, GridUnitType.Star));

        Assert.Equal(0.75, split.SplitRatio);
        Assert.Equal(SplitFixedSide.None, split.FixedSide);
    }

    /// <summary>A fixed side's minimum never overrules the size somebody chose for it.</summary>
    /// <remarks>A strip of tabs one row tall held at 40 px was drawn 50 tall, because the minimum that
    /// keeps a splitter from squeezing a tile away was applied to it as to any pane.</remarks>
    [Theory]
    [InlineData(50, 40, 40)]
    [InlineData(50, 240, 50)]
    [InlineData(50, 50, 50)]
    [InlineData(158, 40, 40)]
    [InlineData(50, 0, 50)]
    public void A_fixed_pane_is_never_held_above_its_own_pixels(double minimum, double extent, double expected) =>
        Assert.Equal(expected, TileMinimumSize.ForFixedSide(minimum, extent));

    // ---- the edits ----------------------------------------------------------------------------------

    /// <summary>A gutter drop beside a fixed side takes its room from the other side alone, and still takes
    /// a third of it.</summary>
    /// <remarks>Wrapped in with the fixed tile instead, the newcomer and that tile would be held at the
    /// one tile's pixels between them — a list 240 wide with a note squeezed into it.</remarks>
    [Theory]
    [InlineData(SplitFixedSide.First)]
    [InlineData(SplitFixedSide.Second)]
    public void A_gutter_drop_beside_a_fixed_side_goes_into_the_other_side(SplitFixedSide fixedSide)
    {
        var (list, rest, newcomer) = (Leaf(), Leaf(), Leaf());
        var holder = Split(Leaf(), newcomer);
        var split = fixedSide == SplitFixedSide.First ? Split(list, rest) : Split(rest, list);
        split.Fix(fixedSide, 240);
        _ = Split(split, holder);

        TileTreeEdits.ExecuteGutter(newcomer, split);

        Assert.Same(list, fixedSide == SplitFixedSide.First ? split.First : split.Second);
        Assert.Equal(fixedSide, split.FixedSide);
        Assert.Equal(240, split.FixedExtent);

        // The newcomer goes next to the gutter, inside the side that is not fixed.
        var inner = Assert.IsType<SplitTileNodeViewModel>(fixedSide == SplitFixedSide.First ? split.Second : split.First);
        if (fixedSide == SplitFixedSide.First)
        {
            Assert.Same(newcomer, inner.First);
            Assert.Same(rest, inner.Second);
            Assert.Equal(TileDropRatio.NewcomerShare, inner.SplitRatio, precision: 9);
        }
        else
        {
            Assert.Same(rest, inner.First);
            Assert.Same(newcomer, inner.Second);
            Assert.Equal(1 - TileDropRatio.NewcomerShare, inner.SplitRatio, precision: 9);
        }
    }

    [Fact]
    public void An_edge_drop_can_hold_the_dropped_tile_at_a_size_in_pixels()
    {
        var (a, b, c) = (Leaf(), Leaf(), Leaf());
        TileNodeViewModel? root = Split(a, Split(b, c));
        foreach (var leaf in new[] { a, b, c })
            leaf.RootReplaced = node => root = node;

        TileTreeEdits.ExecuteRootEdge(c, () => root, DropZone.Top, TileDropSize.InPixels(40));

        var top = Assert.IsType<SplitTileNodeViewModel>(root);
        Assert.Equal(Orientation.Horizontal, top.Orientation);
        Assert.Same(c, top.First);
        Assert.Equal(SplitFixedSide.First, top.FixedSide);
        Assert.Equal(40, top.FixedExtent);
    }

    [Fact]
    public void A_tile_edge_drop_fixes_the_side_the_dropped_tile_is_on()
    {
        var (a, b, c) = (Leaf(), Leaf(), Leaf());
        _ = Split(a, Split(b, c));

        TileTreeEdits.Execute(c, a, DropZone.Right, TileDropSize.InPixels(240));

        var split = Assert.IsType<SplitTileNodeViewModel>(a.Parent);
        Assert.Same(c, split.Second);
        Assert.Equal(SplitFixedSide.Second, split.FixedSide);
        Assert.Equal(240, split.FixedExtent);
    }

    /// <summary>An edge drop on a fixed tile, across its fixed axis, is a gutter drop on its split.</summary>
    /// <remarks>Split in place, the list and the newcomer would share the list's pixels between them.</remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_edge_drop_on_a_fixed_tile_goes_into_the_flexible_side(bool leftEdge)
    {
        var zone = leftEdge ? DropZone.Left : DropZone.Right;
        var (list, rest, newcomer) = (Leaf(), Leaf(), Leaf());
        var split = Split(list, rest);
        split.Fix(SplitFixedSide.First, 240);
        _ = Split(split, Split(Leaf(), newcomer));

        Assert.Same(split, TileTreeEdits.FixedSplitAcross(list, zone));
        TileTreeEdits.Execute(newcomer, list, zone, TileDropSize.InPixels(100));

        Assert.Same(list, split.First);
        Assert.Equal(240, split.FixedExtent);
        var inner = Assert.IsType<SplitTileNodeViewModel>(split.Second);
        Assert.Same(newcomer, inner.First);
        Assert.Same(rest, inner.Second);
    }

    /// <summary>A tile inside a fixed pane, not the fixed child itself, is protected the same way.</summary>
    /// <remarks>A list and a tab strip stacked in the 240 px pane: split along the fixed axis, two tiles
    /// would share those pixels.</remarks>
    [Fact]
    public void An_edge_drop_on_a_tile_inside_a_fixed_pane_goes_into_the_flexible_side()
    {
        var (list, tabs, rest, newcomer) = (Leaf(), Leaf(), Leaf(), Leaf());
        var pane = new SplitTileNodeViewModel(Orientation.Horizontal, list, tabs);
        list.Parent = pane;
        tabs.Parent = pane;
        var split = Split(pane, rest);
        split.Fix(SplitFixedSide.First, 240);
        _ = Split(split, Split(Leaf(), newcomer));

        Assert.Same(split, TileTreeEdits.FixedSplitAcross(tabs, DropZone.Right));
        Assert.Null(TileTreeEdits.FixedSplitAcross(tabs, DropZone.Top));
        TileTreeEdits.Execute(newcomer, tabs, DropZone.Right);

        Assert.Same(pane, split.First);
        var inner = Assert.IsType<SplitTileNodeViewModel>(split.Second);
        Assert.Same(newcomer, inner.First);
        Assert.Same(rest, inner.Second);
    }

    /// <summary>A gutter inside a fixed pane, along the fixed axis, is the fixed split's gutter.</summary>
    [Fact]
    public void A_gutter_drop_inside_a_fixed_pane_goes_into_the_flexible_side()
    {
        var (left, right, rest, newcomer) = (Leaf(), Leaf(), Leaf(), Leaf());
        var pane = Split(left, right);
        var split = Split(pane, rest);
        split.Fix(SplitFixedSide.First, 240);
        _ = Split(split, Split(Leaf(), newcomer));

        Assert.Same(split, TileTreeEdits.GutterSplitFor(pane));
        TileTreeEdits.ExecuteGutter(newcomer, pane);

        Assert.Same(pane, split.First);
        Assert.Same(left, pane.First);
        Assert.Same(right, pane.Second);
        var inner = Assert.IsType<SplitTileNodeViewModel>(split.Second);
        Assert.Same(newcomer, inner.First);
        Assert.Same(rest, inner.Second);
    }

    /// <summary>Across the other axis the fixed tile is split as any tile is: the pair keeps its width.</summary>
    [Fact]
    public void An_edge_drop_along_a_fixed_tile_splits_it()
    {
        var list = Leaf();
        var split = Split(list, Leaf());
        split.Fix(SplitFixedSide.First, 240);

        Assert.Null(TileTreeEdits.FixedSplitAcross(list, DropZone.Top));
        Assert.Null(TileTreeEdits.FixedSplitAcross((LeafTileNodeViewModel)split.Second!, DropZone.Left));
    }

    /// <summary>Without a size, both edge drops give exactly what they always gave.</summary>
    [Fact]
    public void An_edge_drop_without_a_size_fixes_nothing()
    {
        var (a, b, c) = (Leaf(), Leaf(), Leaf());
        _ = Split(a, Split(b, c));

        TileTreeEdits.Execute(c, a, DropZone.Left);

        var split = Assert.IsType<SplitTileNodeViewModel>(a.Parent);
        Assert.Equal(SplitFixedSide.None, split.FixedSide);
        Assert.Equal(0.5, split.SplitRatio);
    }

    // ---- the hints ----------------------------------------------------------------------------------

    /// <summary>An edge hint is the fixed size when there is one, and a fixed size wider than the room
    /// leaves is drawn at the cap the layout puts on it.</summary>
    [Theory]
    [InlineData(900, 600, "Left", null, 0, 0, 300, 600)]
    [InlineData(900, 600, "Left", 240.0, 0, 0, 240, 600)]
    [InlineData(900, 600, "Right", 240.0, 660, 0, 240, 600)]
    [InlineData(900, 600, "Bottom", 40.0, 0, 560, 900, 40)]
    [InlineData(300, 200, "Left", 280.0, 0, 0, 242, 200)]
    [InlineData(300, 200, "Right", 280.0, 58, 0, 242, 200)]
    public void The_edge_hint_is_the_fixed_size_capped_as_the_layout_caps_it(double width, double height,
        string zone, double? pixels, double x, double y, double hintWidth, double hintHeight)
    {
        TileDropSize? size = pixels is { } p ? TileDropSize.InPixels(p) : null;

        RectAssert.Close(new Rect(x, y, hintWidth, hintHeight),
            TileDropGeometry.EdgeBand(new Size(width, height), Enum.Parse<DropZone>(zone), size, 8, 50));
    }

    /// <summary>A gutter hint beside a fixed side covers a third of the other side, after the gutter.</summary>
    [Fact]
    public void The_gutter_hint_beside_a_fixed_side_leaves_the_fixed_tile_out()
    {
        var room = new Rect(0, 0, 908, 600);
        var split = Split(Leaf(), Leaf());

        split.Fix(SplitFixedSide.First, 240);
        RectAssert.Close(new Rect(248, 0, 220, 600), TileDropGeometry.GutterBand(room, split, gap: 8));

        split.Fix(SplitFixedSide.Second, 240);
        RectAssert.Close(new Rect(440, 0, 220, 600), TileDropGeometry.GutterBand(room, split, gap: 8));
    }

    /// <summary>A fixed size wider than the split is drawn at the cap the grid lays it out at.</summary>
    [Fact]
    public void The_gutter_hint_beside_an_oversized_fixed_side_uses_the_capped_size()
    {
        var room = new Rect(0, 0, 300, 600);
        var split = Split(Leaf(), Leaf());
        split.Fix(SplitFixedSide.First, 400);

        // 292 to share, the flexible side needs 50 + 8 + 50 once the newcomer is in: the fixed side gets 184.
        RectAssert.Close(new Rect(192, 0, 36, 600), TileDropGeometry.GutterBand(room, split, gap: 8));
    }

    /// <summary>Two fixed panes nested along one axis: the drop and its hint both go to the outer one.</summary>
    [Fact]
    public void A_gutter_drop_inside_nested_fixed_panes_goes_to_the_outermost()
    {
        var (a, b, c, rest) = (Leaf(), Leaf(), Leaf(), Leaf());
        var innermost = Split(a, b);
        var middle = Split(innermost, c);
        middle.Fix(SplitFixedSide.First, 200);
        var outer = Split(middle, rest);
        outer.Fix(SplitFixedSide.First, 400);

        Assert.Same(outer, TileTreeEdits.GutterSplitFor(innermost));
        Assert.Same(outer, TileTreeEdits.GutterSplitFor(TileTreeEdits.GutterSplitFor(innermost)));
    }

    [Fact]
    public void The_gutter_hint_of_an_ordinary_split_is_unchanged()
    {
        var room = new Rect(0, 0, 900, 600);
        var split = Split(Leaf(), Leaf());

        RectAssert.Close(new Rect(300, 0, 300, 600), TileDropGeometry.GutterBand(room, split, gap: 8));
    }

    // ---- building blocks ----------------------------------------------------------------------------

    /// <summary>Bands are thirds of a size, and a third is not exact in floating point.</summary>
    private LeafTileNodeViewModel Leaf() => new(TileKindIds.None, null, "", _scope);

    private static SplitTileNodeViewModel Split(TileNodeViewModel first, TileNodeViewModel second)
    {
        var split = new SplitTileNodeViewModel(Orientation.Vertical, first, second);
        first.Parent = split;
        second.Parent = split;
        return split;
    }

    private TileTreeSerializer Serializer(TempSettings settings) =>
        new(TestTiles.Catalog(settings.Service), new TileContext(settings.Directory, settings.Service),
            _ => "name", _ => { }, _scope);
}
