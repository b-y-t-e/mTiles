namespace mTiles.Models;

/// <summary>
/// How an agent's conversation is given an identity that survives a restart of mTiles.
/// </summary>
/// <remarks>
/// Three named strategies rather than a branch per agent, because what differs is one question — who
/// chooses the id — and the tile has to do something different in each case at a different moment.
/// Measured against Claude Code 2.1.251, codex-cli 0.141.0, opencode 1.18.18, pi 0.84.3 and agy 1.1.22.
/// </remarks>
public enum SessionStrategy
{
    /// <summary>We name it: the tile's own <c>TileId</c> is the whole of the bookkeeping.
    /// <c>pi --session-id &lt;id&gt;</c> creates the session if it is missing and resumes it if it is
    /// not; Claude Code splits the two (<c>--resume</c> continues, <c>--session-id</c> creates and
    /// refuses an id already in use), which is why <c>ClaudeAgent</c> runs the continuing command
    /// first and the creating one as its fallback.</summary>
    Fixed,

    /// <summary>We create the session ourselves, then name it. <c>opencode --session</c> only ever
    /// <em>continues</em> one, so <c>opencode import</c> writes a document carrying the id we want
    /// first — see <c>OpenCodeSession</c>.</summary>
    ImportedFixed,

    /// <summary>The agent names it and we find out afterwards — agy by answering with its
    /// <c>conversation_id</c>, codex by leaving a <c>rollout-*.jsonl</c> behind. The tile's session id
    /// is therefore writable and its layout has to be saved at the moment the id is captured, which is
    /// the one thing this strategy costs that the other two do not.</summary>
    CapturedAfterStart,
}
