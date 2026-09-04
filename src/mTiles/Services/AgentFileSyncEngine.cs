using System.Diagnostics;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// One workspace's live mirror between <c>CLAUDE.md</c> and <c>AGENTS.md</c>: whichever of the two
/// changes, its content is copied into the other.
/// </summary>
/// <remarks>
/// <para><b>Loop prevention is a cache, not a lock on the writer.</b> Every write this engine makes to
/// either file is followed immediately by re-stamping that path's cache entry with the mtime and
/// content the write actually produced. The next <see cref="FileSystemWatcher"/> event on that same
/// path — which the write itself causes — then reads back the same mtime it already has cached and is
/// a no-op. A real external edit is the only thing that leaves a path's mtime different from what the
/// cache remembers.</para>
/// <para><b>Deletion is not withdrawal.</b> While sync is active for this workspace, a file going
/// missing is read as damage to be repaired from the other one, not as the user opting out — opting out
/// is the context menu or Settings, never <c>rm</c>.</para>
/// <para><b>Both changing in the same debounce window</b> — a tool that writes both files itself —
/// is resolved by actual mtime: the later one propagates, "always from the newest".</para>
/// </remarks>
public sealed class AgentFileSyncEngine : IDisposable
{
    public const string ClaudeFileName = "CLAUDE.md";
    public const string AgentsFileName = WorkspaceAgentFiles.CanonicalInstructionFile;

    private static readonly string[] FileNames = [ClaudeFileName, AgentsFileName];

    private readonly string _workspaceDir;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Guards the three fields that say whether this engine is live — <see cref="_running"/>,
    /// <see cref="_watcher"/> and <see cref="_debounce"/>. <see cref="Stop"/> is called from the UI
    /// thread, from the watcher's own error callback and from the tail of <see cref="StartAsync"/> on a
    /// thread-pool thread, so without it two of them can both walk past a non-null watcher and the
    /// second one touches a disposed object; and a plain <c>bool</c> written on one thread and read on
    /// another has no barrier between the write and the read at all.</summary>
    private readonly Lock _lifecycle = new();

    /// <summary>How many times a reconcile re-arms itself while one of the two files cannot be read.
    /// Enough to outlast a scanner or a save that holds a file for a moment, and short enough that a
    /// file locked for good costs a handful of attempts rather than a timer for the whole session.
    /// </summary>
    private const int UnreadableRetries = 5;

    private readonly Dictionary<string, FileState> _cache = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private bool _running;

    /// <summary>Which run of this engine is the live one. Bumped by every <see cref="StartAsync"/>, so
    /// a reconcile queued under an earlier run recognises itself as stale and does nothing.
    /// <see cref="IsRunning"/> alone cannot tell the two apart: a stop immediately followed by a start —
    /// which is what an answer arriving for an already-live mirror does — puts the flag back to true
    /// before the queued callback looks at it, and that callback would then resolve the pair by mtime,
    /// overwriting the very file the user has just named as the current one. Read and written under
    /// <see cref="_lifecycle"/>.</summary>
    private long _epoch;

    /// <summary>How many attempts in a row have found one of the two files unreadable. Read and written
    /// only under <see cref="_gate"/>, and put back to zero by the first read that succeeds.</summary>
    private int _unreadableRetries;

    /// <summary>How often a failing watcher may be rebuilt before this engine gives up on the
    /// workspace: at most <see cref="MaxWatcherRebuildsPerWindow"/> within
    /// <see cref="WatcherRebuildWindow"/> — a rate over a window rather than a running total, the rule
    /// <c>RelaunchBudget</c> follows, so one overflow a month is still recovered however many months
    /// have passed. Read and written under <see cref="_lifecycle"/>.</summary>
    private const int MaxWatcherRebuildsPerWindow = 3;
    private static readonly TimeSpan WatcherRebuildWindow = TimeSpan.FromMinutes(10);
    private DateTime _watcherRebuildWindowStartUtc;
    private int _watcherRebuildsSpent;

    /// <summary>What one look at one of the two files found. <see cref="Unreadable"/> is a third
    /// answer and not a spelling of <see cref="Missing"/>: a file held open by an editor, an antivirus
    /// or a cloud-sync client answers with an <see cref="IOException"/>, and read as a deletion it is
    /// repaired from the other side — which overwrites the edit being made at that moment, silently.
    /// The same distinction is what <c>AgentFileSyncCoordinator.ContentsEqual</c> keeps when it refuses
    /// to call two unreadable files identical.</summary>
    private enum FileStateKind { Missing, Unreadable, Present }

    /// <remarks><b>Bytes, not text</b> — the same rule <c>GitIgnoreFile</c> follows. This engine never
    /// modifies what it carries, it only moves it from one file to the other, so decoding it costs
    /// something and buys nothing: read as text through the default UTF-8, a source BOM is dropped from
    /// the copy and a file written in UTF-16 (or in any encoding that is not UTF-8) comes out the other
    /// side as mangled replacement characters.</remarks>
    private readonly record struct FileState(FileStateKind Kind, DateTime Mtime, byte[]? Content)
    {
        public static readonly FileState Missing = new(FileStateKind.Missing, default, null);
        public static readonly FileState Unreadable = new(FileStateKind.Unreadable, default, null);

        public static FileState Present(DateTime mtime, byte[] content) =>
            new(FileStateKind.Present, mtime, content);

        public bool IsUnreadable => Kind == FileStateKind.Unreadable;
        public bool Exists => Kind == FileStateKind.Present;

        /// <summary>The bytes that were read. Asking a state that does not <see cref="Exists"/> is a bug
        /// here rather than a condition on disk, so it says so instead of writing an empty file over
        /// somebody's instructions — which is what a <c>Content ?? []</c> would do, and what a bare
        /// <c>Content!</c> leaves as nothing more than a convention between two branches.</summary>
        public byte[] Bytes => Kind == FileStateKind.Present
            ? Content!
            : throw new InvalidOperationException($"No content was read for a {Kind} file.");
    }

    public AgentFileSyncEngine(string workspaceDir) => _workspaceDir = workspaceDir;

    public bool IsRunning
    {
        get { lock (_lifecycle) return _running; }
    }

    /// <summary>Whether <paramref name="epoch"/> is still the live run. Expects
    /// <see cref="_lifecycle"/> to be held; <see cref="IsCurrentRunOutsideLock"/> is the same question
    /// for a caller that holds nothing.</summary>
    private bool IsCurrentRun(long epoch) => _running && _epoch == epoch;

    private bool IsCurrentRunOutsideLock(long epoch)
    {
        lock (_lifecycle) return IsCurrentRun(epoch);
    }

    private string PathOf(string fileName) => Path.Combine(_workspaceDir, fileName);

    /// <summary>Seeds the cache from disk and starts watching. Idempotent.</summary>
    /// <param name="authoritativeFileName">Which of the two files is the current one, where the caller
    /// already knows — the wizard's answer, or the side a legacy shim must not win against. It decides
    /// the seeding reconcile and nothing after it: once both sides agree, later edits are resolved by
    /// mtime like any other. Null is the ordinary case, "nobody said", and then the newest of the two
    /// wins here as well. The whole rule lives in <see cref="ReconcileAsync(string?)"/>, so seeding and
    /// mirroring cannot come to different conclusions about the same pair of files.</param>
    public async Task StartAsync(string? authoritativeFileName = null)
    {
        long epoch;
        lock (_lifecycle)
        {
            if (_running) return;
            _running = true;
            epoch = ++_epoch;
        }

        try
        {
            await SeedAndWatchAsync(authoritativeFileName, epoch);
        }
        catch (Exception ex)
        {
            // A workspace directory that has been renamed or deleted since the layout was saved makes
            // the watcher's own constructor throw. Left as it was, the engine would stay IsRunning with
            // nothing watching — so nothing would ever start it again — and the exception would escape
            // into a fire-and-forget task as an UnobservedTaskException. Put back down instead, so the
            // next call is free to try again.
            Stop();
            Trace.TraceWarning("Could not start agent file sync in '{0}': {1}", _workspaceDir, ex.Message);
        }
    }

    private async Task SeedAndWatchAsync(string? authoritativeFileName, long epoch)
    {
        // The seeding is under the same gate a reconcile takes, because a reconcile left in flight by
        // the previous Stop is the one other thing that writes this cache.
        bool alreadyAgrees;
        await _gate.WaitAsync();
        try
        {
            _cache.Clear();
            // A new run gets its own budget of attempts. Left as it was, a previous run that gave up
            // on a file some editor held open would hand this one a counter already at its limit, and
            // the seeding reconcile below would give up without arming a single retry — leaving the
            // two apart until somebody saved one of them by hand.
            _unreadableRetries = 0;
            var seen = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in FileNames)
                seen[name] = await ReadAsync(PathOf(name));

            // The two can have been driven apart while nothing was watching — a pull, a checkout, an
            // edit in another tool. Seeding the cache with that state would make the disagreement the
            // engine's new idea of "unchanged", so nothing would propagate until somebody saved one
            // side, and that save would then overwrite the other side's offline changes silently. A
            // pair that disagrees is therefore left out of the cache and reconciled below, by the same
            // "always from the newest" rule every later edit takes.
            alreadyAgrees = Agree(seen[ClaudeFileName], seen[AgentsFileName]);
            if (alreadyAgrees)
                foreach (var (name, state) in seen)
                    _cache[name] = state;
        }
        finally
        {
            _gate.Release();
        }

        var watcher = new FileSystemWatcher(_workspaceDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            // The name filters are applied in managed code, so the native buffer collects every event
            // this directory produces — a large checkout at the workspace root is the ordinary way it
            // sees a burst. Sized to what the workspace's own git watcher carries for the same reason.
            InternalBufferSize = 51200,
        };
        watcher.Filters.Add(ClaudeFileName);
        watcher.Filters.Add(AgentsFileName);
        watcher.Changed += OnEvent;
        watcher.Created += OnEvent;
        watcher.Renamed += OnEvent;
        watcher.Deleted += OnEvent;
        watcher.Error += OnError;

        // Stop can have run while the seeding was in flight — it went past a watcher that did not exist
        // yet, so this one would be left raising events for a stopped engine with nobody to take it
        // down. Publishing it and abandoning it are the same decision, so they are one critical section.
        lock (_lifecycle)
        {
            if (!IsCurrentRun(epoch))
            {
                Detach(watcher);
                return;
            }

            _watcher = watcher;
            watcher.EnableRaisingEvents = true;
        }

        // After the watcher is live, never before: a reconcile that wrote first would have its own
        // write land while nothing was listening, and the cache re-stamp inside it is what makes that
        // write a no-op rather than a loop.
        // The losing file is copied aside first, and only here: this is the one write that can
        // replace content nobody has looked at since the two were driven apart, and the user has just
        // been asked which of them is current. Every mirror after it is the sync they switched on.
        if (!alreadyAgrees)
            await ReconcileAsync(authoritativeFileName, epoch, backupOverwrittenTarget: true);
    }

    /// <summary>Whether the two files are already in the state this engine keeps them in. An
    /// unreadable one is not an agreement — it says nothing about what is in it, so the pair is
    /// reconciled, which is where the bounded retry lives.</summary>
    private static bool Agree(FileState claude, FileState agents)
    {
        if (claude.IsUnreadable || agents.IsUnreadable) return false;
        if (claude.Exists != agents.Exists) return false;
        if (!claude.Exists) return true;
        return claude.Bytes.AsSpan().SequenceEqual(agents.Bytes);
    }

    /// <summary>Stops watching. A later <see cref="StartAsync"/> re-seeds the cache from disk rather
    /// than trusting stale state — and that is where the cache is cleared, not here: Stop is called from
    /// the UI thread while a debounced reconcile may be reading and writing the cache on a thread-pool
    /// thread, and mutating a <see cref="Dictionary{TKey,TValue}"/> under it is a corrupted cache or an
    /// <see cref="InvalidOperationException"/> that only ever surfaces as a Trace warning.</summary>
    public void Stop()
    {
        FileSystemWatcher? watcher;
        Timer? debounce;
        lock (_lifecycle)
        {
            _running = false;
            debounce = _debounce;
            _debounce = null;
            watcher = _watcher;
            _watcher = null;
        }

        // Taken out of the fields first, so a second Stop finds nothing and this one owns the disposal
        // alone — two callers both disposing the same watcher is an ObjectDisposedException on a
        // thread-pool thread that nobody is waiting on.
        debounce?.Dispose();
        if (watcher != null) Detach(watcher);
    }

    private void Detach(FileSystemWatcher watcher)
    {
        watcher.EnableRaisingEvents = false;
        watcher.Changed -= OnEvent;
        watcher.Created -= OnEvent;
        watcher.Renamed -= OnEvent;
        watcher.Deleted -= OnEvent;
        watcher.Error -= OnError;
        watcher.Dispose();
    }

    public void Dispose() => Stop();

    private void OnEvent(object? sender, FileSystemEventArgs e) => ScheduleReconcile();

    /// <summary>Drives the watcher's error callback without a real failure on the filesystem — a
    /// buffer overflow is not something a test can arrange. Production reaches
    /// <see cref="OnError"/> only.</summary>
    internal void FailWatcher(ErrorEventArgs e) => OnError(this, e);

    private void OnError(object? sender, ErrorEventArgs e)
    {
        Trace.TraceWarning("Agent file sync watcher failed for '{0}': {1}", _workspaceDir,
            e.GetException().Message);

        // A native buffer overflow is lost events, not a dead mirror — and the name filters are
        // applied in managed code, so the buffer collects everything the directory produces and a
        // large checkout at the workspace root is enough. Stopping here would leave the two files
        // free to drift for the rest of the session while the config still reads enabled, which is
        // the one outcome this engine exists to prevent; the git tile answers the same event by
        // refreshing rather than resigning. The equivalent here is a rebuild: a fresh watcher to
        // replace one that may not have survived, and a seeding that is the same reconcile an
        // offline window gets. Bounded, because a filesystem whose watchers die as fast as they are
        // raised must not spin.
        if (!TrySpendWatcherRebuild())
        {
            Trace.TraceWarning(
                "Agent file sync in '{0}' gave up: its watcher has kept failing. The mirror is off for this session.",
                _workspaceDir);
            Stop();
            return;
        }

        Stop();
        _ = StartAsync();
    }

    /// <summary>Whether one more watcher rebuild is allowed right now, and spent if so. The window
    /// restarts with the first failure that arrives after it has run out — a total would stop
    /// recovering a workspace whose watcher overflows once a checkout, for ever.</summary>
    private bool TrySpendWatcherRebuild()
    {
        lock (_lifecycle)
        {
            var now = DateTime.UtcNow;
            if (now - _watcherRebuildWindowStartUtc >= WatcherRebuildWindow)
            {
                _watcherRebuildWindowStartUtc = now;
                _watcherRebuildsSpent = 0;
            }
            if (_watcherRebuildsSpent >= MaxWatcherRebuildsPerWindow) return false;
            _watcherRebuildsSpent++;
            return true;
        }
    }

    /// <param name="epoch">The run this reconcile belongs to, or null for "whichever run is live now",
    /// which is what a watcher event is: it describes the disk rather than a run.</param>
    private void ScheduleReconcile(long? epoch = null)
    {
        Timer? previous;
        lock (_lifecycle)
        {
            // An event that arrives after Stop must not queue a reconcile — and must not leave a timer
            // in a field Stop has already emptied. A retry armed by a superseded run is dropped for the
            // same reason: the run that replaced it has already read both files itself.
            if (!_running) return;
            if (epoch is { } armed && armed != _epoch) return;
            var scheduled = _epoch;
            previous = _debounce;
            _debounce = new Timer(
                _ => _ = ReconcileAsync(null, scheduled, backupOverwrittenTarget: false),
                null, AppDefaults.WatcherDebounceMs, Timeout.Infinite);
        }
        previous?.Dispose();
    }

    /// <summary>Reads both files' current state and makes the disk agree, propagating whichever side
    /// actually changed since the last time this engine looked.</summary>
    internal Task ReconcileAsync()
    {
        long epoch;
        lock (_lifecycle) epoch = _epoch;
        return ReconcileAsync(null, epoch, backupOverwrittenTarget: false);
    }

    /// <summary>Holds a reconcile between its reads and its decision — for the one test that has to
    /// keep it inside the gate while a stop and an answer-carrying restart run past it, a window of
    /// microseconds no repetition can hit. Production never sets this; the hook is the whole of the
    /// cost.</summary>
    internal Func<Task>? ReconcilePauseForTests { get; set; }

    /// <inheritdoc cref="ReconcileAsync()"/>
    /// <param name="authoritativeFileName">Which side wins when both have changed since this engine
    /// last looked — which, on a freshly seeded cache, is both of them. Null falls back to the later
    /// mtime.</param>
    /// <param name="epoch">The run this reconcile was queued under. A superseded one does nothing: it
    /// would resolve the pair by mtime against a cache the run that replaced it has already
    /// re-seeded — and, where that run carries the wizard's answer, against the user's own choice.
    /// </param>
    /// <param name="backupOverwrittenTarget">Whether the file about to be overwritten holds content
    /// this application has never mirrored, and so has to be copied aside before it goes.</param>
    private async Task ReconcileAsync(string? authoritativeFileName, long epoch,
        bool backupOverwrittenTarget)
    {
        if (!IsCurrentRunOutsideLock(epoch)) return;
        await _gate.WaitAsync();
        try
        {
            // Asked again on the way in: Stop — or the restart that carries the user's answer — can
            // have run while this reconcile was queued behind another, and a mirror that writes after
            // either is one that overwrites a decision without anybody seeing it happen.
            if (!IsCurrentRunOutsideLock(epoch)) return;

            var claude = await ReadAsync(PathOf(ClaudeFileName));
            var agents = await ReadAsync(PathOf(AgentsFileName));

            if (ReconcilePauseForTests is { } pause) await pause();

            // Asked once more, now that the reads are back — the checks above were taken before the
            // I/O, and Stop, or the restart that carries the user's answer, can have run while it was
            // in flight. Continuing from here would settle the pair by mtime behind a decision the
            // user has just made, and the seeding that follows would find the two already agreeing:
            // the answer lost without even the copy the seeding write keeps.
            if (!IsCurrentRunOutsideLock(epoch)) return;

            // A file that could not be read says nothing about what is in it, so there is nothing to
            // propagate in either direction and nothing to remember. The cache is left alone and this
            // reconcile asks again shortly: the event that led here has already been consumed, so
            // waiting for the next one means a file locked for a moment by an editor, an antivirus or a
            // cloud-sync client leaves the two sides quietly disagreeing until somebody saves again.
            // Bounded, because a file that is unreadable for good — permissions, a lock nobody
            // releases — would otherwise arm a timer every debounce for the life of the session.
            if (claude.IsUnreadable || agents.IsUnreadable)
            {
                RetryWhileUnreadable(epoch);
                return;
            }

            _unreadableRetries = 0;

            _cache.TryGetValue(ClaudeFileName, out var claudeCached);
            _cache.TryGetValue(AgentsFileName, out var agentsCached);

            var claudeChanged = Changed(claudeCached, claude);
            var agentsChanged = Changed(agentsCached, agents);

            if (!claudeChanged && !agentsChanged)
                return;

            // A file that vanished and the other one still exists is damage to repair, not a withdrawal.
            if (!claude.Exists && agents.Exists)
            {
                await MirrorAsync(AgentsFileName, agents, ClaudeFileName, backupOverwrittenTarget, epoch);
                return;
            }
            if (!agents.Exists && claude.Exists)
            {
                await MirrorAsync(ClaudeFileName, claude, AgentsFileName, backupOverwrittenTarget, epoch);
                return;
            }
            if (!claude.Exists && !agents.Exists)
            {
                _cache[ClaudeFileName] = claude;
                _cache[AgentsFileName] = agents;
                return;
            }

            // Both exist. Whichever changed propagates; if both did, the caller's answer decides where
            // there is one — that is the seeding case, where the user has just been asked which file is
            // current — and otherwise the later mtime does.
            var claudeWins = claudeChanged && agentsChanged && authoritativeFileName is { } chosen
                ? chosen.Equals(ClaudeFileName, StringComparison.OrdinalIgnoreCase)
                : claudeChanged && (!agentsChanged || claude.Mtime >= agents.Mtime);
            if (claudeWins)
                await MirrorAsync(ClaudeFileName, claude, AgentsFileName, backupOverwrittenTarget, epoch);
            else
                await MirrorAsync(AgentsFileName, agents, ClaudeFileName, backupOverwrittenTarget, epoch);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Could not reconcile CLAUDE.md/AGENTS.md in '{0}': {1}", _workspaceDir,
                ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Arms one more attempt after an unreadable read, up to
    /// <see cref="UnreadableRetries"/>. Called under <see cref="_gate"/>, which is the only thing that
    /// touches the counter.</summary>
    private void RetryWhileUnreadable(long epoch)
    {
        if (_unreadableRetries >= UnreadableRetries)
        {
            Trace.TraceWarning(
                "Gave up reconciling CLAUDE.md/AGENTS.md in '{0}': one of them stayed unreadable.",
                _workspaceDir);
            return;
        }

        _unreadableRetries++;
        ScheduleReconcile(epoch);
    }

    private static bool Changed(FileState cached, FileState current)
    {
        if (!cached.Exists && !current.Exists) return false;
        // A file that has appeared or gone has changed whatever its bytes are — without this, a file
        // created empty compares equal to one that was not there at all.
        if (cached.Exists != current.Exists) return true;
        if (cached.Mtime == current.Mtime) return false;
        return !cached.Bytes.AsSpan().SequenceEqual(current.Bytes);
    }

    private static async Task<FileState> ReadAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return FileState.Missing;
            var mtime = File.GetLastWriteTimeUtc(path);
            return FileState.Present(mtime, await File.ReadAllBytesAsync(path));
        }
        catch (FileNotFoundException)
        {
            // It went while this was reading it, which is a deletion like any other.
            return FileState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return FileState.Missing;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Could not read '{0}' for agent file sync: {1}", path, ex.Message);
            return FileState.Unreadable;
        }
    }

    /// <summary>Copies one side over the other and re-stamps both cache entries — the target's only
    /// when what comes back off disk is what was just written.</summary>
    /// <remarks><para>Caching that read unconditionally loses an edit that lands in the window between
    /// the write and the read: the user's own bytes would be remembered as this engine's own write,
    /// read as "unchanged" for ever after and never carried to the other side, leaving the two
    /// permanently apart while the contract here says they are identical. Left out of the cache
    /// instead, the very next reconcile sees that side as changed and propagates it — which is exactly
    /// what an edit at any other moment gets.</para>
    /// <para>Nothing is cached before the write, either: a write that throws would otherwise record the
    /// source as seen, so the difference between the two files would never be looked at again.</para>
    /// </remarks>
    private async Task MirrorAsync(string sourceName, FileState source, string targetName,
        bool backupOverwrittenTarget, long epoch)
    {
        var targetPath = PathOf(targetName);
        if (backupOverwrittenTarget && !await BackupAsync(targetPath)) return;

        // The backup is the long step, and Stop — or the restart that carries the user's answer — can
        // have run while it copied. Writing on would settle the pair without the choice, and the
        // seeding that follows would find the two already agreeing.
        if (!IsCurrentRunOutsideLock(epoch)) return;

        await WriteAtomicallyAsync(targetPath, source.Bytes);
        _cache[sourceName] = source;

        var written = await ReadAsync(targetPath);
        if (written.Exists && written.Bytes.AsSpan().SequenceEqual(source.Bytes))
            _cache[targetName] = written;
        else
            _cache.Remove(targetName);
    }

    /// <summary>Copies a file that is about to be overwritten to
    /// <c>&lt;name&gt;.pre-sync-&lt;timestamp&gt;</c> beside it, and says whether the overwrite may go
    /// ahead.</summary>
    /// <remarks><para>The same rule the layout migrations follow (<c>{id}.pre-kind.json</c>,
    /// <c>{id}.pre-agents.json</c>): the first mirror of a pair that disagreed replaces content this
    /// application has never carried, and the user is answering a question about which file is current
    /// rather than agreeing to lose the other one. A copy costs a file in their working tree; there is
    /// no other route back to an AGENTS.md they had not committed.</para>
    /// <para>A backup that cannot be written <b>stops the overwrite</b> and answers false. The two
    /// files are then left as they were, which is what they already were a moment ago; the engine goes
    /// on watching, so the user's next save of either side propagates normally. Overwriting anyway
    /// would be exactly the loss the copy exists to prevent, taken because the safeguard failed.</para>
    /// <para>The name carries no <c>.md</c>, so nothing looking for instruction files finds it and the
    /// timestamp keeps a second run from writing over the first copy.</para></remarks>
    private static async Task<bool> BackupAsync(string path)
    {
        if (!File.Exists(path)) return true;
        var backup = $"{path}.pre-sync-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            await Task.Run(() => File.Copy(path, backup, overwrite: true));
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Not overwriting '{0}': its backup could not be written: {1}", path,
                ex.Message);
            return false;
        }
    }

    /// <summary>Writes through a temporary file beside the real one and moves it into place — the same
    /// rule <c>GitIgnoreFile</c> follows, and for the same reason: these are files in somebody's own
    /// repository. Truncating one in place and then writing it leaves a half-written CLAUDE.md behind if
    /// the write is interrupted, and an agent reading it at that moment sees half the instructions.
    /// </summary>
    private static async Task WriteAtomicallyAsync(string path, byte[] content)
    {
        var temporary = path + ".mtiles-tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch { /* the original is untouched, which is the point */ }
            throw;
        }
    }
}
