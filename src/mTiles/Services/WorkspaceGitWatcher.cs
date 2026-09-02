using System.Diagnostics;

namespace mTiles.Services;

/// <summary>
/// One watcher over a workspace's working tree, shared by every tile in it that needs to know the
/// repository moved.
/// </summary>
/// <remarks>
/// <para>The Goal tile's detect buttons are about the uncommitted changes, and those are made in the
/// tiles next door or in an editor outside this application altogether. Asking git at named moments —
/// when the tile was built, when a run ended, when the tile became the active one — leaves the answer
/// wrong for as long as nobody clicks: the user edits two files beside the tile, looks at it, and the
/// buttons it should be offering are not there.</para>
/// <para>What was rejected before, and rightly, was <em>a watcher per Goal tile</em>: two goals and a
/// git tile in one workspace is three recursive <see cref="FileSystemWatcher"/> pairs over the same
/// tree. This is one per workspace, handed out by <see cref="Tiles.TileContext"/>, so the cost does not
/// grow with the tiles — and it is only paid while something is actually subscribed: the underlying
/// watcher starts with the first subscriber and is disposed with the last, so a workspace holding
/// neither kind of tile watches nothing.</para>
/// <para><b>The noise floor is this class's own, not a favour from whoever happens to be open.</b>
/// The ignored directories are the union of what the subscribers know <em>and</em> what the watcher
/// asks git for itself (<see cref="IIgnoredDirectorySource"/>). Only the git tile computes them, so
/// with the subscribers as the only source a workspace holding a Goal tile alone watched its tree with
/// an empty ignore list: every write into <c>obj/</c>, <c>bin/</c> or <c>node_modules/</c> reached the
/// subscribers, and a build in the terminal next door became a stream of <c>git status</c> processes
/// for the sake of two buttons. It is asked once when the watch starts and at most every
/// <see cref="IgnoreLifetime"/> afterwards, because the directories git ignores appear as work is done
/// rather than only at construction — and the subscribers' own answers, which cost nothing extra since
/// the git tile computes them for itself anyway, keep the union current between those.</para>
/// <para><b>Nothing shrinks the set until it is replaced by the same source.</b> A last-writer rule
/// would have a subscriber's list quietly reset by anyone else's, so the union is kept per source and
/// a subscriber's contribution leaves with it.</para>
/// <para><b>A workspace can become a repository after its tiles were built</b> — "Create repository" on
/// the workspace row, a clone into an empty folder — and <see cref="GitDirectoryWatcher.Start"/> is a
/// no-op without a <c>.git</c> directory and never retries by itself. With nothing watching there is no
/// filesystem event to retry on either, so the retry is a poll
/// (<see cref="RepositoryPollInterval"/>), running only while there are subscribers and only until the
/// watch takes. It is this class's job rather than a caller's: <c>UpdateIgnoredDirs</c> is called by the
/// git tile alone, so a workspace holding a Goal tile alone would otherwise have stayed deaf for the
/// rest of the session — exactly the gap this class exists to close.</para>
/// </remarks>
public sealed class WorkspaceGitWatcher : IDisposable
{
    /// <summary>How often an unstarted watch asks again whether the workspace is a repository yet.</summary>
    private static readonly TimeSpan RepositoryPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long the watcher's own answer about the ignored directories stays current.</summary>
    private static readonly TimeSpan IgnoreLifetime = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly List<Action> _subscribers = [];
    private readonly Dictionary<Subscription, HashSet<string>> _ignored = [];
    private readonly IIgnoredDirectorySource _ignoredDirectories;
    private HashSet<string> _ownIgnored = [];
    private DateTime _ownIgnoredAskedAt = DateTime.MinValue;
    private bool _askingIgnored;
    private GitDirectoryWatcher? _watcher;
    private Timer? _repositoryPoll;
    private bool _disposed;

    /// <param name="workingDirectory">The workspace's tree.</param>
    /// <param name="ignoredDirectories">Where to ask what git ignores in it. Defaults to git itself; a
    /// test hands in its own so the watcher can be driven without a repository.</param>
    public WorkspaceGitWatcher(string workingDirectory, IIgnoredDirectorySource? ignoredDirectories = null)
    {
        WorkingDirectory = workingDirectory;
        _ignoredDirectories = ignoredDirectories
            ?? new GitService(workingDirectory, GitService.ResolveGitPath(null));
    }

    public string WorkingDirectory { get; }

    /// <summary>
    /// Hear about every change to this workspace's tree, until the returned handle is disposed.
    /// </summary>
    /// <remarks>The callback arrives on <see cref="GitDirectoryWatcher"/>'s debounce timer — a
    /// thread-pool thread — so a subscriber that touches the UI marshals for itself, exactly as it does
    /// for its own git calls.</remarks>
    public Subscription Subscribe(Action onChanged)
    {
        var subscription = new Subscription(this, onChanged);
        bool askIgnored;

        lock (_gate)
        {
            if (_disposed) return subscription;

            _subscribers.Add(onChanged);
            askIgnored = EnsureWatching();
        }

        if (askIgnored) AskWhatGitIgnores();

        return subscription;
    }

    /// <summary>
    /// Start the watch if it is not running, and keep polling while there is nothing to watch yet.
    /// </summary>
    /// <returns>Whether the watcher's own ignore list should be asked for — answered here and acted on
    /// outside the lock, since the answer comes from a git process.</returns>
    /// <remarks>Called under the lock.</remarks>
    private bool EnsureWatching()
    {
        if (_subscribers.Count == 0) return false;

        if (_watcher is null)
        {
            _watcher = new GitDirectoryWatcher(WorkingDirectory);
            _watcher.Changed += Raise;
            ApplyIgnored();
        }

        _watcher.Start();

        if (!_watcher.IsWatching)
        {
            _repositoryPoll ??= new Timer(
                _ => PollForRepository(), null, RepositoryPollInterval, RepositoryPollInterval);
            return false;
        }

        StopPolling();
        return IsIgnoreListStale();
    }

    private void PollForRepository()
    {
        bool askIgnored;

        lock (_gate)
        {
            if (_disposed) return;
            askIgnored = EnsureWatching();
        }

        if (askIgnored) AskWhatGitIgnores();
    }

    /// <summary>Called under the lock.</summary>
    private void StopPolling()
    {
        _repositoryPoll?.Dispose();
        _repositoryPoll = null;
    }

    /// <summary>Called under the lock.</summary>
    private bool IsIgnoreListStale() =>
        !_askingIgnored && DateTime.UtcNow - _ownIgnoredAskedAt > IgnoreLifetime;

    /// <summary>
    /// Ask git what it ignores here and fold the answer into the union. Fire and forget: nothing waits
    /// on it, and a workspace that is not a repository — or a machine with no git — simply keeps the
    /// answer it had.
    /// </summary>
    private async void AskWhatGitIgnores()
    {
        lock (_gate)
        {
            if (_disposed || _askingIgnored) return;
            _askingIgnored = true;
            // Stamped before the call rather than after it, so a tree that answers with an error every
            // time cannot become one git process per notification.
            _ownIgnoredAskedAt = DateTime.UtcNow;
        }

        HashSet<string>? dirs = null;
        try
        {
            dirs = await _ignoredDirectories.GetIgnoredDirsAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                $"Could not read the ignored directories of {WorkingDirectory}: {ex.Message}");
        }

        lock (_gate)
        {
            _askingIgnored = false;
            if (_disposed || dirs is null) return;

            _ownIgnored = dirs;
            ApplyIgnored();
        }
    }

    /// <summary>What one subscriber knows is ignored, replacing whatever it said last.</summary>
    private void UpdateIgnoredDirs(Subscription owner, HashSet<string> dirs)
    {
        lock (_gate)
        {
            if (_disposed) return;

            _ignored[owner] = dirs;
            ApplyIgnored();
        }
    }

    /// <summary>Called under the lock.</summary>
    private void ApplyIgnored()
    {
        if (_watcher is null) return;

        HashSet<string> union = new(_ownIgnored, StringComparer.OrdinalIgnoreCase);
        foreach (var set in _ignored.Values)
            union.UnionWith(set);

        _watcher.UpdateIgnoredDirs(union);
    }

    private void Raise()
    {
        Action[] listeners;
        bool askIgnored;

        lock (_gate)
        {
            listeners = [.. _subscribers];
            askIgnored = _watcher is not null && IsIgnoreListStale();
        }

        // Before the listeners rather than after them: a directory git has only just started ignoring
        // is one this very notification is likely to be about, and the answer is wanted for the next
        // change rather than for this one.
        if (askIgnored) AskWhatGitIgnores();

        foreach (var listener in listeners)
        {
            // One subscriber throwing must not cost the others their notification: this runs on a timer
            // callback, where an unhandled exception ends the process.
            try { listener(); }
            catch (Exception ex) { Trace.TraceWarning($"Git watcher subscriber failed: {ex.Message}"); }
        }
    }

    private void Unsubscribe(Subscription subscription, Action onChanged)
    {
        GitDirectoryWatcher? stopped = null;

        lock (_gate)
        {
            _subscribers.Remove(onChanged);
            if (_ignored.Remove(subscription)) ApplyIgnored();

            if (_subscribers.Count == 0)
            {
                StopPolling();

                if (_watcher is { } watcher)
                {
                    watcher.Changed -= Raise;
                    _watcher = null;
                    stopped = watcher;
                }
            }
        }

        // Outside the lock: disposing a watcher waits for its callbacks.
        stopped?.Dispose();
    }

    public void Dispose()
    {
        GitDirectoryWatcher? stopped;

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _subscribers.Clear();
            _ignored.Clear();
            StopPolling();
            stopped = _watcher;
            _watcher = null;
        }

        if (stopped is not null)
        {
            stopped.Changed -= Raise;
            stopped.Dispose();
        }
    }

    /// <summary>One tile's place in the watcher: what it hears, and what it knows is ignored.</summary>
    public sealed class Subscription(WorkspaceGitWatcher owner, Action onChanged) : IDisposable
    {
        private bool _disposed;

        /// <summary>The directories this subscriber knows git ignores, replacing whatever it said
        /// last. Dropped from the union when the subscription is disposed.</summary>
        public void UpdateIgnoredDirs(HashSet<string> dirs)
        {
            if (_disposed) return;
            owner.UpdateIgnoredDirs(this, dirs);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.Unsubscribe(this, onChanged);
        }
    }
}
