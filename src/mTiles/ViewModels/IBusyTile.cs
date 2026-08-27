using System.ComponentModel;

namespace mTiles.ViewModels;

/// <summary>
/// Tile content that can say whether it is working right now.
/// </summary>
/// <remarks>
/// The whole of what a workspace needs from a tile in order to light its row in the panel — deliberately
/// one property and its change notification, so a tile that has no notion of being busy (a note, a todo
/// list) implements nothing and the workspace asks nothing of it.
/// </remarks>
public interface IBusyTile : INotifyPropertyChanged
{
    bool IsBusy { get; }
}
