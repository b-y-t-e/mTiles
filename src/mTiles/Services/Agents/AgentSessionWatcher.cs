using System.Diagnostics;
using mTiles.Models;
using mTiles.Services.Agents.SessionLogs;

namespace mTiles.Services.Agents;

/// <summary>
/// Watches one agent's own session store for one workspace, and says when the conversation has moved or
/// its context has changed.
/// </summary>
/// <remarks>
/// <para><b>One object serving two questions that turn out to be one.</b> "Which conversation is this
/// tile really in" and "how full is its context" are both answered by the same file changing, so they
/// are one watcher and one read rather than two of each. What the tile does with the answers is its own
/// business — a terminal agent tile writes the id into its layout and draws the gauge; an Agent tile,
/// which drives the protocol and is told both, needs none of it.</para>
/// <para><b>It knows nothing about any CLI.</b> Everything it does goes through
/// <see cref="IAgentSessionLog"/>: where to watch, what counts as a session, and what is in one. An
/// agent that answers <c>null</c> to <see cref="IAiAgent.SessionLog"/> gets a watcher that never starts,
/// which is why the tile needs no branch for agy.</para>
/// <para><b>A read is debounced and never runs twice at once.</b> These stores are appended to line by
/// line while the agent is talking, so a turn is dozens of events, and the read walks a file that can
/// run to megabytes. The window is short enough that the gauge moves while the user is watching it and
/// long enough that a turn is one read rather than fifty.</para>
/// <para><b>Nothing here throws.</b> The store belongs to somebody else and so does the file system: a
/// failure costs a reading, never the tile that owns this.</para>
/// </remarks>
public sealed class AgentSessionWatcher : IDisposable
{
    /// <summary>How long a run of writes is let settle before the store is read.</summary>
    /// <remarks>Shorter than <c>SkillChangePolicy.QuietWindow</c> and for the opposite reason: that one
    /// swallows a run of <em>user</em> clicks, where two seconds is imperceptible, while this follows a
    /// figure somebody is watching change.</remarks>
    public static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(400);

    /// <summary>How often a store that does not exist yet is looked for again.</summary>
    /// <remarks>A <c>Directory.Exists</c> and, for opencode, one listing of a small index — which only
    /// walks that index when it has changed, so asking for the life of a tile costs the same whether the
    /// user has one opencode project or fifty. Short enough that the first turn in a new workspace moves
    /// the bar within a few seconds.</remarks>
    public static readonly TimeSpan AttachRetryInterval = TimeSpan.FromSeconds(5);

    private readonly IAgentSessionLog? _log;
    private readonly AiSignIn? _signIn;
    private readonly string _workspaceDir;
    private readonly DateTimeOffset _since;
    private readonly Func<string, bool>? _isFree;
    private readonly Func<string?>? _knownSessionId;
    private readonly Func<DateTimeOffset?>? _lastSubmission;
    private readonly Action<AgentSessionReading> _report;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lifecycle = new();

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private Timer? _attachRetry;
    private string? _lastSessionId;
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private readonly HashSet<string> _strangers = new(StringComparer.Ordinal);
    private bool _disposed;
    private int _readAskedWhileBusy;

    /// <param name="log">The agent's own reader. The watcher is inert when this is null.</param>
    /// <param name="since">The moment this tile started. A conversation nobody has written to since is
    /// one the tile has no claim on — the rule <c>SessionCapture</c> already follows, restated here because a watcher
    /// that took the newest conversation in the directory would take last week's on its first tick.
    /// </param>
    /// <param name="isFree">Whether an id is one this tile may have, asked of whatever holds that
    /// answer. Two tiles of one agent in one workspace watch the same directory.</param>
    /// <param name="knownSessionId">The conversation the tile believes it is in, asked when nothing
    /// written since <paramref name="since"/> turned up. <b>Without it a resumed tile never draws a
    /// bar</b>: a claude or pi tile takes its session id from its own identity, so the file it resumes
    /// can be weeks old and is filtered out by the very rule that stops a tile adopting a stranger's
    /// conversation. Asked second rather than first, because a conversation the user has moved to since
    /// this tile started is the better answer when there is one.</param>
    /// <param name="lastSubmission">When the user last submitted something in the tile that may have
    /// moved it into another conversation, or null when nothing it did may have — asked before every look
    /// for one. <b>The store says a conversation is new, never which process wrote it</b>: a <c>/clear</c>
    /// in one tile, and a <c>claude</c> in a terminal outside this application, are seen by every tile of
    /// that agent watching the same directory. So a new conversation is taken only when it was written
    /// after that submission <em>and</em> the conversation the tile knows was not — a process still
    /// writing where it was has not moved. Not handed at all, any new conversation is taken.</param>
    /// <param name="report">Called with every reading, on a thread-pool thread. Getting onto the UI
    /// thread is the caller's business, because only the caller knows whether it has one.</param>
    public AgentSessionWatcher(IAgentSessionLog? log, AiSignIn? signIn, string workspaceDir,
        DateTimeOffset since, Func<string, bool>? isFree, Action<AgentSessionReading> report,
        Func<string?>? knownSessionId = null, Func<DateTimeOffset?>? lastSubmission = null)
    {
        _log = log;
        _signIn = signIn;
        _workspaceDir = workspaceDir;
        _since = since;
        _isFree = isFree;
        _knownSessionId = knownSessionId;
        _lastSubmission = lastSubmission;
        _report = report;
    }

    /// <summary>Whether this agent keeps a store there is anything to watch.</summary>
    public bool CanWatch => _log is not null;

    /// <summary>The conversation the store last said this tile is in, or null before the first reading.
    /// </summary>
    public string? SessionId
    {
        get { lock (_lifecycle) return _lastSessionId; }
    }

    /// <summary>Starts watching, and takes one reading straight away.</summary>
    /// <remarks><b>The first reading is not optional.</b> A tile restored from a layout opens onto a
    /// conversation that has been sitting on disk since the last session, and nothing will write to it
    /// until the user says something — so a watcher that only reacted to events would show an empty
    /// gauge behind a full conversation until the next turn.</remarks>
    public void Start()
    {
        if (_log is null || _disposed) return;

        if (!TryAttach()) WaitForTheStore();
        Schedule();
    }

    /// <summary>Watches the store's directory if it exists yet.</summary>
    /// <returns>False while there is nothing to watch; true once a watcher has been attached, or
    /// attaching one failed in a way asking again would not mend.</returns>
    private bool TryAttach()
    {
        var directory = Safely(() => _log!.WatchDirectory(_signIn, _workspaceDir));
        if (directory is not { Length: > 0 } || !Directory.Exists(directory)) return false;

        Attach(directory);
        return true;
    }

    /// <summary>Looks for the store again until it appears.</summary>
    /// <remarks>The directory is the CLI's to make: a workspace it has never run in has none yet, and
    /// opencode has no project for it until its first session is indexed — so the first tile in a new
    /// workspace starts before there is anything to watch, and the conversation it then holds is written
    /// into a directory that appears seconds later. Watching the parent instead would mean watching the
    /// whole of somebody's <c>~/.claude</c>; asking again on a slow timer costs a <c>Directory.Exists</c>.
    /// </remarks>
    private void WaitForTheStore()
    {
        Trace.TraceInformation("{0} has no session directory for this workspace yet; looking again every "
            + "{1}s", _log!.GetType().Name, AttachRetryInterval.TotalSeconds);

        lock (_lifecycle)
        {
            if (_disposed) return;
            _attachRetry = new Timer(_ => RetryAttach(), null, AttachRetryInterval, AttachRetryInterval);
        }
    }

    private void RetryAttach()
    {
        if (_disposed || !TryAttach()) return;

        lock (_lifecycle)
        {
            _attachRetry?.Dispose();
            _attachRetry = null;
        }

        // The directory appeared because the CLI wrote a conversation into it, and that write happened
        // before anybody was watching.
        Schedule();
    }

    private void Attach(string directory)
    {
        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                // codex files by date rather than by project, so its store is a tree and a new
                // conversation appears in a subdirectory that may not exist yet. The others are flat,
                // where recursion costs one directory's worth of nothing.
                IncludeSubdirectories = true,
                // The size the workspace's git watcher and the instruction-file sync both carry: the
                // native buffer collects everything the tree produces, and an agent mid-turn writes
                // steadily.
                InternalBufferSize = 51200,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A store on a share, a path that has gone: the first reading still happens and the tile
            // keeps whatever it was launched with. Worth a line, because a gauge that never moves has no
            // explanation anywhere else.
            Trace.TraceWarning("Watching {0} failed, so this tile's session is read once and not "
                + "followed: {1}", directory, ex.Message);
            return;
        }

        watcher.Changed += OnEvent;
        watcher.Created += OnEvent;
        watcher.Renamed += OnEvent;
        watcher.Deleted += OnEvent;
        watcher.Error += OnWatcherError;

        lock (_lifecycle)
        {
            // Disposed meanwhile, or a retry that overlapped the one which attached first.
            if (_disposed || _watcher is not null)
            {
                Detach(watcher);
                return;
            }

            _watcher = watcher;
            watcher.EnableRaisingEvents = true;
        }
    }

    private void OnEvent(object? sender, FileSystemEventArgs e) => Schedule();

    /// <summary>A watcher that has died takes the following with it, so it is said out loud once.
    /// </summary>
    /// <remarks>Deliberately not rebuilt, which is the opposite of <c>AgentFileSyncEngine</c> and for a
    /// reason: there, lost events leave two files silently disagreeing for the rest of the session and
    /// nothing else would ever notice. Here the tile still reads the store at its next launch and still
    /// has the id it started with, so what is lost is freshness — and a watcher rebuilt in a loop on a
    /// filesystem that cannot keep one is a worse trade than a stale gauge.</remarks>
    private void OnWatcherError(object? sender, ErrorEventArgs e)
    {
        Trace.TraceWarning("The watcher on this tile's session store failed, so its reading will not "
            + "follow further changes: {0}", e.GetException().Message);
        Schedule();
    }

    /// <summary>Asks for a reading once the writes have stopped.</summary>
    /// <remarks>One timer, rearmed rather than a second one started, so a turn's worth of events is one
    /// read. Rearming a timer that has already fired simply arms it again, which is what makes "the last
    /// event wins" true without a queue.</remarks>
    private void Schedule()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _debounce ??= new Timer(_ => _ = ReadAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(QuietWindow, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Takes one reading and reports it, unless one is already being taken.</summary>
    /// <remarks><b>Folded into one more read rather than queued when a read is in flight.</b> Any number
    /// of requests arriving meanwhile are one question — what does the store say now — so they leave one
    /// mark, and the read in flight schedules a single further read as it finishes. Dropping them instead
    /// lost the last line of a turn written during a long read: no further event came to re-arm the
    /// debounce, and the gauge stayed on the reading before that turn until the next one.</remarks>
    private async Task ReadAsync()
    {
        if (_log is null || _disposed) return;
        if (!await _gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            Interlocked.Exchange(ref _readAskedWhileBusy, 1);
            return;
        }

        try
        {
            // The conversation the tile believes it is in. For a restored tile of an agent whose session
            // id comes from its own identity, the file it resumes is older than the tile by definition,
            // so this is the only reading there is.
            var known = await ReadKnownAsync().ConfigureAwait(false);
            var moveSince = EarliestMove(known);
            if (moveSince is null) await NoteStrangersAsync().ConfigureAwait(false);
            var reading = await ReadMovedToAsync(moveSince).ConfigureAwait(false) ?? known;

            if (reading is not null) Report(reading);
        }
        catch (OperationCanceledException)
        {
            // Disposed mid-read. Nothing to report and nobody to report it to.
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Reading this tile's session store failed: {0}", ex.Message);
        }
        finally
        {
            _gate.Release();
            if (Interlocked.Exchange(ref _readAskedWhileBusy, 0) == 1) Schedule();
        }
    }

    private async Task<AgentSessionReading?> ReadKnownAsync() =>
        _knownSessionId?.Invoke() is { Length: > 0 } known
            ? await _log!.ReadAsync(_signIn, _workspaceDir, known, _lifetime.Token).ConfigureAwait(false)
            : null;

    /// <summary>The conversation the tile has moved to, or null when it has not moved — or when this
    /// store cannot say.</summary>
    /// <remarks>A store that cannot tell a headless run from the interface is read by id alone: its
    /// newest conversation may be a Goal tile's run, and adopting that is worse than not following a
    /// <c>/clear</c>.</remarks>
    private async Task<AgentSessionReading?> ReadMovedToAsync(DateTimeOffset? earliestMove)
    {
        if (!_log!.TellsHeadlessRunsApart) return null;
        if (earliestMove is not { } since) return null;

        return await _log.ReadLatestAsync(_signIn, _workspaceDir, since, MayBeTaken, _lifetime.Token)
            .ConfigureAwait(false);
    }

    /// <summary>Whether a conversation may be taken as the one the tile moved to.</summary>
    /// <remarks>A stranger is refused before <c>isFree</c> is asked, because that one may claim the id.
    /// </remarks>
    private bool MayBeTaken(string sessionId)
    {
        lock (_lifecycle)
            if (_strangers.Contains(sessionId)) return false;

        return _isFree?.Invoke(sessionId) ?? true;
    }

    /// <summary>Remembers every interactive conversation being written while this tile could not have
    /// moved, as somebody else's.</summary>
    /// <remarks>
    /// <para><b>What the adoption window cannot tell on its own.</b> The window opens on an Enter in this
    /// tile, and an Enter that makes the tile's own transcript write nothing straight away — a menu choice,
    /// a permission accepted before a long tool run — leaves it open while a <c>claude</c> in another
    /// terminal, or the editor next door, goes on writing into the same directory. That conversation
    /// was already being written before the Enter, and nothing this tile does moves it into a conversation
    /// that was alive elsewhere, so what was seen written outside the window is never taken inside it.
    /// </para>
    /// <para>A conversation this tile has held is not a stranger: <c>/resume</c> back to the one a
    /// <c>/clear</c> left is a move the tile really makes. And a list taken across the moment a window
    /// opened is thrown away, since the conversation the tile is moving to may already be on it.</para>
    /// <para>Only asked where a move is followed at all, so the cost — a listing, and the first lines of the
    /// conversations written since the tile started — is paid only by a store that tells headless runs
    /// apart.</para>
    /// </remarks>
    private async Task NoteStrangersAsync()
    {
        if (!_log!.TellsHeadlessRunsApart || _lastSubmission is null || _lastSubmission() is not null) return;

        var written = await _log.ListInteractiveAsync(_signIn, _workspaceDir, _since, _lifetime.Token)
            .ConfigureAwait(false);
        if (_lastSubmission() is not null) return;

        var known = _knownSessionId?.Invoke();
        lock (_lifecycle)
        {
            foreach (var id in written)
                if (id != known && !_held.Contains(id))
                    _strangers.Add(id);
        }
    }

    /// <summary>The earliest write a conversation the tile moved to can have, or null when it cannot
    /// have moved.</summary>
    private DateTimeOffset? EarliestMove(AgentSessionReading? known)
    {
        if (_lastSubmission is null) return _since;
        if (_lastSubmission() is not { } submittedAt) return null;

        // Written since the submission: the process in the tile answered it where it already was.
        if (known is not null && known.UpdatedAt >= submittedAt) return null;

        return submittedAt > _since ? submittedAt : _since;
    }

    /// <summary>Hands a reading on, unless this watcher has been disposed meanwhile.</summary>
    /// <remarks>The test and the call are one step under the lock <see cref="Dispose"/> takes, so nothing
    /// is reported once it has returned: a read that passed a bare test just before would otherwise hand
    /// its caller a conversation the caller has already moved on from.</remarks>
    private void Report(AgentSessionReading reading)
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _lastSessionId = reading.SessionId;
            _held.Add(reading.SessionId);
            _report(reading);
        }
    }

    /// <summary>Takes a reading now rather than waiting for the store to change.</summary>
    /// <remarks>What a launch calls: the process that has just started may be resuming a conversation
    /// nobody has written to since yesterday, so there is no event coming to ask the question.</remarks>
    public void ReadNow() => Schedule();

    private T? Safely<T>(Func<T?> answer)
    {
        try
        {
            return answer();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Asking {0} where its sessions are failed: {1}",
                _log?.GetType().Name ?? "an agent", ex.Message);
            return default;
        }
    }

    private static void Detach(FileSystemWatcher watcher)
    {
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        FileSystemWatcher? watcher;
        Timer? debounce;
        Timer? attachRetry;

        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            watcher = _watcher;
            debounce = _debounce;
            attachRetry = _attachRetry;
            _watcher = null;
            _debounce = null;
            _attachRetry = null;
        }

        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        if (watcher is not null) Detach(watcher);
        debounce?.Dispose();
        attachRetry?.Dispose();
        // The token source and the gate are cancelled and left, never disposed: a read already in flight
        // still asks the one for its token and releases the other in its finally, and disposing either
        // under it turned closing the tile mid-read into an unobserved ObjectDisposedException. Neither
        // holds anything unmanaged unless a wait handle is asked for, and nothing here asks.
    }
}
