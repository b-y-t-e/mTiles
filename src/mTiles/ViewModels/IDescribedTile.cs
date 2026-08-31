namespace mTiles.ViewModels;

/// <summary>
/// A tile that can say, in a few words, what it is actually running.
/// </summary>
/// <remarks>
/// <para><b>Beside the name, never instead of it.</b> The name is the user's — typed, or generated as
/// <c>Agent#1</c> — and it is what they navigate by; this is the answer to a different question, which
/// the header could not answer at all: two tiles both called <c>Agent#N</c> may be Claude Code on a
/// subscription and Codex on OpenRouter, and nothing on screen distinguished them. So it is metadata in
/// the panel's own sense — plain, small, muted, and the first thing to give way when the header runs out
/// of room.</para>
/// <para>An interface rather than a member on <see cref="ITile"/>, which is deliberately three members
/// and stays that way: this is one more of the capabilities a tile announces by implementing, like
/// <c>IBusyTile</c> and <c>IProcessTile</c>. A kind with nothing useful to add simply does not implement
/// it, and the header shows the name alone — which is what every kind does today except the agent.</para>
/// <para>Changes are announced through <see cref="ITile"/>'s own change notification, so a tile
/// relaunched on a different instance updates its own header without the view knowing why.</para>
/// </remarks>
public interface IDescribedTile : ITile
{
    /// <summary>What this tile is running — <c>"Claude Code · glm-5.3-flash"</c> — or empty for a tile
    /// that has nothing to add to its own name.</summary>
    string HeaderNote { get; }
}
