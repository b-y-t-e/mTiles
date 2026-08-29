namespace mTiles.Models;

/// <summary>
/// What a tile's kind was called before kinds were objects.
/// </summary>
/// <remarks>
/// <b>Kept for one reason and closed for good: it is the exhaustive record of what is on people's
/// disks.</b> Nothing in the application switches on it any more, but <see cref="TileNode.ContentType"/>
/// still both reads it (through <see cref="TileKindIds.FromLegacy"/>) and <b>writes it back</b> (through
/// <see cref="TileKindIds.ToLegacy"/>), so every layout this build saves carries the old name beside the
/// new kind — rule 2 in <c>docs/TILES.md</c>, without which a build Velopack has rolled back reads every
/// leaf as an empty tile and saves the emptiness over the user's layout. Nothing should be added to it:
/// a seventh kind is a class and a line of registration, with an id of its own that was never a member
/// here, and a kind this enum never named simply writes no <c>ContentType</c> at all.
/// <para>Because it is exhaustive, it is also what the test that every historical layout still opens is
/// written against.</para>
/// </remarks>
public enum TileContentType
{
    Empty,
    Terminal,
    Note,
    Todo,
    Git,
    Database,
    Goal
}
