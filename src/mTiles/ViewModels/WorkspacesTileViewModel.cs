using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;

namespace mTiles.ViewModels;

/// <summary>The list of workspaces, as a tile of the window's layout.</summary>
/// <remarks>A wrapper and nothing more: the list belongs to the window, which built it long before the
/// window had a layout, and outlives any one arrangement of it. Disposing the tile therefore disposes
/// nothing — a list torn down because its tile moved would take every workspace's row with it.</remarks>
public sealed class WorkspacesTileViewModel(WorkspacesPanelViewModel panel) : ObservableObject, ITile
{
    public string KindId => TileKindIds.Workspaces;

    /// <summary>The list itself.</summary>
    public WorkspacesPanelViewModel Panel { get; } = panel;

    public void Dispose() { }
}
