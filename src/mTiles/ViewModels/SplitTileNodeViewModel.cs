using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;

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

    public SplitTileNodeViewModel(Orientation orientation, TileNodeViewModel first, TileNodeViewModel second)
    {
        _orientation = orientation;
        _first = first;
        _second = second;
    }

    partial void OnSplitRatioChanged(double value) => NotifyLayoutChanged();
}
