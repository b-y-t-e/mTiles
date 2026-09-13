using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;

namespace mTiles.ViewModels;

/// <summary>Where the open workspace is drawn, as a tile of the window's layout.</summary>
/// <remarks>It holds nothing. Which workspace is open, and the view kept for each one, belong to the
/// window and not to this tile: workspace views are cached so that switching does not end their shells,
/// and a cache owned by a tile would be emptied the first time the tile was rebuilt.</remarks>
public sealed class WorkspaceHostTileViewModel : ObservableObject, ITile
{
    public string KindId => TileKindIds.WorkspaceHost;

    public void Dispose() { }
}
