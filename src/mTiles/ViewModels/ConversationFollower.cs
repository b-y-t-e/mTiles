using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Agents.SessionLogs;

namespace mTiles.ViewModels;

/// <summary>
/// Which conversation a tile is really in, followed out of the agent's own session store.
/// </summary>
/// <remarks>
/// <para><b>A collaborator rather than more of the tile</b>, for the reason
/// <see cref="ContextWindowFollower"/> is one: launching an agent and following what it writes down
/// change for different reasons, and the following is a little machine of its own — a watcher whose
/// lifetime is tied to the tile's identity, and the window in which a conversation that has appeared may
/// be put down to this tile at all. The tile keeps what only it can answer: which id it holds, whether
/// the agent may be resumed on a followed one, and writing that into the layout.</para>
/// <para><b>The adoption window lives here</b> because both halves of it answer one question: the store
/// says a conversation is new, never which process wrote it, so a <c>/clear</c> in a neighbouring tile —
/// or a <c>claude</c> started by hand in a terminal outside this application — is seen by every tile of
/// that agent in the workspace. Only the tile being typed into, and only just after an Enter in it, may
/// take one.</para>
/// <para><b>Nothing here knows about any CLI</b>: everything it watches goes through
/// <see cref="IAgentSessionLog"/>, and an agent that keeps no store gets a follower that never starts.
/// </para>
/// </remarks>
public sealed class ConversationFollower : IDisposable
{
    /// <summary>How long after a line is submitted in the tile a new conversation may still be put down
    /// to it.</summary>
    /// <remarks>A <c>/clear</c> or a pick in <c>/resume</c> is an Enter, and so is the first message
    /// after either — the one that makes the CLI write the conversation it has moved to. Short, because
    /// every minute this stays open is a minute in which a <c>claude</c> in another terminal writing into
    /// the same directory is taken for this tile's own.</remarks>
    public static readonly TimeSpan MoveWindow = TimeSpan.FromSeconds(30);

    private readonly IAgentSessionLog? _log;
    private readonly string _workspaceDir;
    private readonly Func<AiSignIn?> _signIn;
    private readonly Func<string, bool> _isFree;
    private readonly Func<string?> _knownSessionId;
    private readonly Action<AgentSessionReading> _report;
    private readonly Action<Action> _post;

    private AgentSessionWatcher? _watcher;
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private volatile bool _isActive;
    private long _lastSubmissionTicks;
    private bool _disposed;

    /// <param name="log">The agent's own reader; null for an agent that keeps no readable store.</param>
    /// <param name="signIn">Asked at every start rather than held, because switching the tile to an
    /// instance on another account moves the whole store.</param>
    /// <param name="isFree">Whether an id is one this tile may have — two tiles of one agent in one
    /// workspace watch the same directory.</param>
    /// <param name="knownSessionId">The conversation the tile believes it is in.</param>
    /// <param name="report">Called on the thread <paramref name="post"/> leads to, and only for the
    /// watcher this follower is on now.</param>
    public ConversationFollower(IAgentSessionLog? log, string workspaceDir, Func<AiSignIn?> signIn,
        Func<string, bool> isFree, Func<string?> knownSessionId, Action<AgentSessionReading> report,
        Action<Action> post)
    {
        _log = log;
        _workspaceDir = workspaceDir;
        _signIn = signIn;
        _isFree = isFree;
        _knownSessionId = knownSessionId;
        _report = report;
        _post = post;
    }

    /// <summary>Starts following the store.</summary>
    /// <remarks>Called from the tile's construction rather than from its first launch, so a tile restored
    /// from a layout draws its conversation's context before anybody types anything: the store has been
    /// sitting on disk since the last session and nothing is going to write to it until the next turn.
    /// <para>The claim rule is the capture's, asked of whatever holds it (<paramref name="isFree"/>):
    /// asked here as a test and taken by the tile when it adopts a reading, in one step there — a
    /// candidate claimed on the way to a read that then failed would stay held by a tile that never took
    /// it.</para></remarks>
    public void Start()
    {
        if (_log is null || _disposed) return;

        AgentSessionWatcher? watcher = null;
        watcher = new AgentSessionWatcher(_log, _signIn(), _workspaceDir,
            // A conversation begun before this tile is not this tile's to adopt, whatever it has stored:
            // the session it is showing is read by id below, and anything older than the tile — last
            // week's conversation from another terminal, an earlier Goal run — is a stranger's.
            since: _startedAt,
            isFree: _isFree,
            report: reading => _post(() => ReportFrom(watcher, reading)),
            // What the tile thinks it is in, for the case where nothing newer than it exists: a claude or
            // pi tile derives its session id from its own identity, so the conversation it resumes is
            // older than the tile by definition and the "not a stranger's" rule would otherwise leave it
            // with a bar it never draws.
            knownSessionId: _knownSessionId,
            lastSubmission: SubmissionThatMayMoveTheConversation);
        _watcher = watcher;
        watcher.Start();
    }

    /// <summary>Follows the store again after the tile's identity or account has changed.</summary>
    /// <remarks>A fresh watcher rather than a reset one: what has changed is the earliest conversation
    /// the tile may take, which the watcher is handed once and holds — and one object whose every field
    /// can be re-pointed is the shape where a half-applied change survives. The sign-in is re-read here
    /// too, so a tile switched to another account stops reading the directory of the one it has left.
    /// </remarks>
    public void Restart()
    {
        _watcher?.Dispose();
        _watcher = null;
        _startedAt = DateTimeOffset.UtcNow;
        Start();
    }

    /// <summary>Asks for a reading now, outside the store's own events.</summary>
    public void ReadNow() => _watcher?.ReadNow();

    /// <summary>Notes whether this is the tile the user is typing into, which is the only one that may
    /// take a conversation it did not start with.</summary>
    /// <remarks>Coming back to the tile is also the moment a move made in it just before the focus left
    /// is picked up, so it costs a reading.</remarks>
    public void OnActiveChanged(bool isActive)
    {
        _isActive = isActive;
        if (isActive) ReadNow();
    }

    /// <summary>Notes an Enter in the tile, which is what opens the window above.</summary>
    public void OnInputSubmitted() =>
        Interlocked.Exchange(ref _lastSubmissionTicks, DateTimeOffset.UtcNow.UtcTicks);

    /// <summary>The submission a new conversation in the store may be answering, or null when none may.
    /// </summary>
    /// <remarks>Read by the watcher off the UI thread, hence the volatile flag and the interlocked ticks.
    /// What the tile's own CLI does when it moves conversation is answer something typed into <em>this</em>
    /// tile, so only a recent Enter here opens the question — and the watcher closes it again when the
    /// conversation the tile knows has been written since, because then the process in this tile is still
    /// where it was.</remarks>
    private DateTimeOffset? SubmissionThatMayMoveTheConversation()
    {
        if (!_isActive) return null;

        var ticks = Interlocked.Read(ref _lastSubmissionTicks);
        if (ticks == 0) return null;

        var submittedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        return DateTimeOffset.UtcNow - submittedAt <= MoveWindow ? submittedAt : null;
    }

    /// <summary>A reading, passed on only from the watcher this follower is on now.</summary>
    /// <remarks>A read in flight when <see cref="Restart"/> replaced the watcher still arrives afterwards,
    /// and it names the conversation the user has just asked to leave — adopted, it would put the tile
    /// straight back into it and write that into the layout.</remarks>
    private void ReportFrom(AgentSessionWatcher? watcher, AgentSessionReading reading)
    {
        if (!_disposed && ReferenceEquals(watcher, _watcher)) _report(reading);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;
    }
}
