using mTiles.Models;

namespace mTiles.Services.Agents.SessionLogs;

/// <summary>
/// What one CLI's own session store, on this machine, says about the conversations it holds for a
/// working directory.
/// </summary>
/// <remarks>
/// <para><b>A separate port rather than three more members on <see cref="IAiAgent"/>.</b> Two unrelated
/// questions are answered from one place here — which conversation this tile is really in, and how full
/// its context is — and both are read from files somebody else's CLI writes. An agent that keeps no
/// readable store answers <c>null</c> to <see cref="IAiAgent.SessionLog"/> and gains nothing it would
/// have to implement, which is the whole reason this is its own interface: the five agents that can
/// answer are not made to carry the shape of the sixth, and the sixth is not made to throw.</para>
/// <para><b>Why a store at all, when the tile already knows the id it launched with.</b> It does not,
/// after the first <c>/clear</c> or <c>/resume</c>: every one of these CLIs lets the user change
/// conversation from inside its own interface, at which point the id in the layout resumes something
/// nobody is looking at. Measured 2026-09-18, five of the six write enough to disk to tell —
/// claude, codex, opencode, pi and grok all file their sessions by working directory — and agy does
/// not, which is why it answers null and says so in its own class.</para>
/// <para><b>Reading, never writing.</b> Everything here is somebody else's file, and this application
/// has no business in it beyond looking: a store whose shape has moved makes these methods answer null,
/// which costs a gauge and a resume, never a tile.</para>
/// </remarks>
public interface IAgentSessionLog
{
    /// <summary>
    /// The newest conversation this CLI has open for <paramref name="workspaceDir"/>, written to no
    /// earlier than <paramref name="since"/>, or null when there is none it can vouch for.
    /// </summary>
    /// <remarks>
    /// <para><b><paramref name="since"/> is what keeps a tile from adopting a stranger's session</b>:
    /// these directories hold every conversation the user has ever had in this project, and one nobody
    /// has spoken in since the tile started is not the one it moved to. Compared with the last write
    /// rather than the start, because <c>/resume</c> to an older conversation goes on appending to it,
    /// and filtered by its start that move would never be seen.</para>
    /// <para><b><paramref name="isFree"/> is what keeps two tiles from adopting the same one.</b> Two
    /// tiles of one agent in one workspace write into the same directory, so "the newest here" is a
    /// question both of them answer identically. The caller decides what free means; a log that is
    /// handed nothing takes the newest it finds.</para>
    /// <para><b>Only a conversation held in the CLI's own interface is a candidate.</b> A headless run
    /// in the same working directory — a Goal tile's, an Agent tile's — files itself in the same
    /// directory and is never the one a terminal tile has moved to — which is why this is only worth
    /// asking of a store that <see cref="TellsHeadlessRunsApart"/>.</para>
    /// </remarks>
    Task<AgentSessionReading?> ReadLatestAsync(AiSignIn? signIn, string workspaceDir,
        DateTimeOffset since, Func<string, bool>? isFree = null, CancellationToken ct = default);

    /// <summary>
    /// Every conversation held in the CLI's own interface for <paramref name="workspaceDir"/> and written
    /// to no earlier than <paramref name="since"/> — listed, never read and never claimed.
    /// </summary>
    /// <remarks>What a tile asks while it <em>cannot</em> have moved: a conversation being written then is
    /// somebody else's — a <c>claude</c> in a terminal outside this application, a neighbouring tile — and
    /// is one it must not take later just because it goes on being written after an Enter here. The same
    /// filters as <see cref="ReadLatestAsync"/>, so only worth asking of a store that
    /// <see cref="TellsHeadlessRunsApart"/>.</remarks>
    Task<IReadOnlyList<string>> ListInteractiveAsync(AiSignIn? signIn, string workspaceDir,
        DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Whether the store marks a headless run as one, so that <see cref="ReadLatestAsync"/> can
    /// pass it over.</summary>
    /// <remarks>Measured 2026-09-18: Claude Code writes <c>entrypoint</c> on every message line and
    /// codex <c>source</c> on its first; pi, opencode and grok write nothing that tells <c>-p</c>,
    /// <c>run</c> or an RPC session apart from their own interface. A store that cannot tell is read by
    /// id alone — its gauge still follows, and a conversation changed inside the TUI is not adopted,
    /// since adopting a Goal tile's run instead would have the next restart resume that.</remarks>
    bool TellsHeadlessRunsApart { get; }

    /// <summary>What the store says about one conversation this tile already knows the id of.</summary>
    /// <remarks>Separate from <see cref="ReadLatestAsync"/> because the two are asked at different
    /// moments and cost differently: this is the gauge's question, asked whenever the file changes, and
    /// it needs no scan of the directory at all.</remarks>
    Task<AgentSessionReading?> ReadAsync(AiSignIn? signIn, string workspaceDir, string sessionId,
        CancellationToken ct = default);

    /// <summary>
    /// The directory to watch for this workspace's sessions, or null when there is nothing to watch.
    /// </summary>
    /// <remarks>Asked rather than derived, because the answer is the CLI's own filing scheme and no two
    /// agree: claude and pi slug the path into a directory name, grok url-encodes it, opencode hashes it
    /// and codex files by date and not by project at all. A watcher that had to know which would be a
    /// sixth copy of a table that already exists on each agent.</remarks>
    string? WatchDirectory(AiSignIn? signIn, string workspaceDir);
}
