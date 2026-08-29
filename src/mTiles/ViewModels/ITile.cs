using System.ComponentModel;

namespace mTiles.ViewModels;

/// <summary>
/// Everything that can be the content of a tile.
/// </summary>
/// <remarks>
/// <para>Deliberately two inherited members and one property of its own. The thinness is the design: a
/// member half the implementations cannot honour is a signature that lies, and an empty body or a
/// <see cref="NotSupportedException"/> is the workaround this interface exists to remove. Three things
/// were considered for it and left out — <c>Refresh()</c> (a note has nothing to refresh),
/// <c>WorkingDirectory</c> (an argument to creation, not state of a tile: the markdown tiles take it to
/// compute a file path and then forget it) and <c>Title</c> (the name belongs to
/// <see cref="LeafTileNodeViewModel"/>, and putting it here as well gives one value two writers).</para>
/// <para>What is left costs nothing, because all six kinds already implement both: change notification,
/// so a view can follow the tile, and disposal, so <see cref="LeafTileNodeViewModel.Dispose"/> no longer
/// has to ask whether its content happens to clean up after itself.</para>
/// <para>Optional abilities are announced by the interfaces that extend this one —
/// <see cref="IBusyTile"/>, <see cref="IFileContent"/>, <see cref="ITileActions"/>,
/// <see cref="ITextInputTile"/>, <see cref="ICustomBackgroundTile"/>. Something earns one of its own
/// only when it is optional, varies while the tile is alive, and somebody has to ask "can you do
/// this?" — which is written <c>is</c> / <c>as</c>, and is what an interface is for.</para>
/// </remarks>
public interface ITile : INotifyPropertyChanged, IDisposable
{
    /// <summary>Which kind of tile this is — <c>"terminal"</c>, <c>"git"</c>, <c>"note"</c> — and what
    /// goes into the layout JSON. The same string the tile's <c>ITileKind</c> is registered under.</summary>
    string KindId { get; }
}
