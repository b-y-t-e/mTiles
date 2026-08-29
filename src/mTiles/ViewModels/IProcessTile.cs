namespace mTiles.ViewModels;

/// <summary>
/// Tile content that runs a child process of its own.
/// </summary>
/// <remarks>
/// One property, so a tile that runs nothing (a note, a todo list) implements nothing and nobody asks it
/// anything — the same bargain <see cref="IBusyTile"/> makes. The id is the <em>root</em> of what the
/// tile started: everything the shell went on to spawn hangs off it, and finding those is the job of
/// whoever reads the machine's process table, not of the tile.
/// <para><c>null</c> when nothing is running, which is the honest answer while a tile is between
/// sessions — a stale id is a number that now belongs to somebody else's process.</para>
/// </remarks>
public interface IProcessTile : ITile
{
    int? ChildProcessId { get; }
}
