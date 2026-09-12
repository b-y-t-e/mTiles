using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;

namespace mTiles.ViewModels;

public partial class SplitTileNodeViewModel : TileNodeViewModel
{
    [ObservableProperty]
    private Orientation _orientation;

    [ObservableProperty]
    private double _splitRatio = 0.5;

    [ObservableProperty]
    private TileNodeViewModel? _first;

    [ObservableProperty]
    private TileNodeViewModel? _second;

    /// <summary>The one child to draw at full size, or null to draw both with a splitter between them.
    /// </summary>
    /// <remarks>
    /// <para>How a maximized tile is shown: <see cref="TileMaximizeScope"/> sets this on every split
    /// between the root and that tile, so the view fills each of them with the child on the path and the
    /// leaf ends up with the whole workspace. Written by the scope alone.</para>
    /// <para>Deliberately not saved — <c>TileTreeSerializer</c> never reads it. It is a way of looking
    /// at a layout rather than part of one, and a workspace that reopened with half its tiles hidden and
    /// nothing on screen explaining why is the failure this is worth one sentence to avoid.</para>
    /// </remarks>
    [ObservableProperty]
    private TileNodeViewModel? _solo;

    /// <summary>Which child is held at <see cref="FixedExtent"/> pixels rather than at a share.</summary>
    /// <remarks>While a side is fixed, <see cref="SplitRatio"/> is not read: the fixed child has its
    /// pixels and the other takes the rest. The ratio is kept rather than cleared, so fixing a side and
    /// letting it go again puts the split back where it was.</remarks>
    [ObservableProperty]
    private SplitFixedSide _fixedSide;

    /// <summary>The fixed child's size along the split, in pixels. Meaningless while nothing is fixed.</summary>
    [ObservableProperty]
    private double _fixedExtent;

    /// <summary>Whether <paramref name="child"/> is the child held at a size in pixels.</summary>
    public bool IsFixed(TileNodeViewModel child) => FixedSide switch
    {
        SplitFixedSide.First => ReferenceEquals(First, child),
        SplitFixedSide.Second => ReferenceEquals(Second, child),
        _ => false
    };

    /// <summary>Whether <paramref name="extent"/> is a size a side can be held at.</summary>
    /// <remarks>The one rule for it: a layout read from disk, a drop asking for pixels and the hint drawn
    /// for that drop all ask here, so none of them can accept a size the others would refuse.</remarks>
    public static bool IsUsableExtent(double extent) => double.IsFinite(extent) && extent > 0;

    /// <summary>Holds one side at <paramref name="extent"/> pixels.</summary>
    /// <remarks>A size that is not usable fixes nothing: a pane held at zero pixels is a tile nobody can
    /// see, and it would be saved as a side that is read back as no fixed side at all.</remarks>
    public void Fix(SplitFixedSide side, double extent)
    {
        if (!IsUsableExtent(extent)) return;
        FixedExtent = extent;
        FixedSide = side;
    }

    public SplitTileNodeViewModel(Orientation orientation, TileNodeViewModel first, TileNodeViewModel second)
    {
        _orientation = orientation;
        _first = first;
        _second = second;
    }

    partial void OnSplitRatioChanged(double value) => NotifyLayoutChanged();
    partial void OnFixedSideChanged(SplitFixedSide value) => NotifyLayoutChanged();
    partial void OnFixedExtentChanged(double value) => NotifyLayoutChanged();
}
