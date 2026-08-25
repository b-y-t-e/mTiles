using System.Diagnostics;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// One Goal tile's file: when it is written, when it is refused, and what happens on the way out.
/// <para>Separate from the view model because none of it is about a view. It is a debounce, a lock, a
/// disposal order and three flags that only ever say "do not write" — and while it lived among the
/// workflow it could only be exercised through a dispatcher, a headless Avalonia session and a full
/// run of the phase machine, for rules that have nothing to do with any of those.</para>
/// <para><b>Every rule here was a bug once.</b> The comments are the specification; the flags are the
/// scar tissue.</para>
/// </summary>
internal sealed class GoalStateStore(string filePath, GoalStatePersistence persistence) : IDisposable
{
    private readonly Lock _lock = new();
    private Timer? _debounceTimer;

    /// <summary>Set by <see cref="Dispose"/> under <see cref="_lock"/>. The workflow keeps unwinding
    /// after the tile is closed — the cancelled run still adds its message — and each of those asked
    /// for a save, arming a fresh timer after the final flush had already gone out: a write belonging
    /// to a tile that no longer exists, on a timer nobody would ever dispose.</summary>
    /// <remarks>Volatile because <see cref="Save"/> reads it outside the lock, on the timer's
    /// thread-pool thread, while it is written under the lock by <see cref="Dispose(bool)"/>. The two
    /// flags either side of it are volatile for the same reason and this one was not.</remarks>
    private volatile bool _disposed;

    /// <summary>
    /// Set when the file exists but could not be opened, which stops this store writing for the rest of
    /// its life. The file is almost certainly intact — locked, or on a disk having a bad moment — and
    /// the tile in front of it is empty, so a save would replace a real session with the blank one that
    /// failed to load it. Refusing to write costs the user this session; not refusing costs them the
    /// one already on disk.
    /// </summary>
    /// <remarks>Read and written without the lock, deliberately: a write that slips through the instant
    /// it is set is a write of the state the tile is already showing.</remarks>
    private volatile bool _refused;

    /// <summary>Said once. A store that cannot save says so, but one that cannot save is also likely to
    /// fail on every message after that, and a transcript is not a log.</summary>
    /// <remarks>Read and written without the lock, for the same reason as <see cref="_refused"/> and
    /// with a smaller consequence: the worst a race here can produce is the message said twice.</remarks>
    private volatile bool _failureReported;

    public string FilePath => filePath;

    /// <summary>Whether a session file exists yet. Asked before writing over nothing.</summary>
    public bool FileExists => File.Exists(filePath);

    /// <summary>
    /// Where the state comes from when it is time to write. Called on whichever thread is saving, so
    /// the view model hands one that marshals: the <em>whole</em> snapshot has to be taken on the UI
    /// thread, engine included, or it enumerates a live collection while the workflow adds to it.
    /// </summary>
    public required Func<GoalTileState> Snapshot { get; init; }

    /// <summary>How a write failure reaches the user. It is said out loud rather than logged, because
    /// the tile that cannot save is the one whose user most needs to know before they keep working in
    /// it.</summary>
    public required Action<string> Report { get; init; }

    /// <summary>
    /// Asks for a save shortly after the last change rather than on the spot.
    /// <para>Used for messages, which arrive in bursts and cost the most: a save serialises the whole
    /// transcript, and doing that on the UI thread for every one of a hundred long answers is a hitch
    /// the user feels. Phase changes go through <see cref="SaveNow"/> instead, because those are what a
    /// restart has to have seen exactly, and they are rare.</para>
    /// <para>The timer's write is wrapped by <see cref="Save"/>: it runs on a thread-pool thread with
    /// nobody left to catch anything, and an unhandled exception there ends the process. The same
    /// reasoning, and the same shape, as <c>SettingsService.DebouncedSave</c>.</para>
    /// </summary>
    public void SaveSoon()
    {
        if (_refused) return;

        lock (_lock)
        {
            // Read inside the lock that arms the timer, and set by Dispose inside the lock that clears
            // it. Checked outside, a caller could pass the check, Dispose could run to completion, and
            // the caller could then arm a timer nobody is left to dispose.
            if (_disposed) return;

            _debounceTimer?.Dispose();
            // Save() catches everything, Snapshot() included: this callback runs with nobody left to
            // catch it, and an unhandled exception on a thread-pool thread ends the application.
            _debounceTimer = new Timer(_ => Save(), null, AppDefaults.SaveDebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Writes now, and stops the debounce from firing again.
    /// <para>Deliberately not described as cancelling a write already under way: <c>Timer.Dispose()</c>
    /// does not promise that a callback in flight will not run. It does not need to. Every writer
    /// serialises the state as it stands when it runs rather than when it was scheduled, and
    /// <see cref="GoalStatePersistence.Save"/> takes a lock, so a straggler writes content at least as
    /// new as this one's — never staler. Waiting for it instead would mean blocking the UI thread on a
    /// callback that may itself be waiting for the UI thread.</para>
    /// </summary>
    public void SaveNow()
    {
        lock (_lock)
        {
            // Refused after Dispose, which has already written the last word. The workflow keeps
            // unwinding afterwards and its phase changes still call through here, so without this a
            // closed tile could write again over the state it had just flushed.
            if (_disposed) return;

            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        Save();
    }

    /// <param name="evenIfDisposed">Only <see cref="Dispose(bool)"/> passes true: it has already set the
    /// flag and is writing the last word. Everything else is refused after disposal —
    /// <c>Timer.Dispose()</c> does not promise that a callback already in flight will not run, so a
    /// straggler armed a moment before the tile closed could otherwise write after the flush.</param>
    private void Save(bool evenIfDisposed = false)
    {
        if (_refused) return;
        if (_disposed && !evenIfDisposed) return;

        try
        {
            var state = Snapshot();

            // Never brings the file into existence for a tile with nothing in it. The rule used to live
            // in the callers — each asking about Messages.Count, or File.Exists, or both — which is why
            // Dispose() can default to flushing at all: `using var store = ...` around an untouched tile
            // would otherwise leave an empty session in the user's repository, and nothing here prunes
            // those. Updating a file that already exists is never refused: emptying a goal is something
            // the user can do, and having it come back on the next load would be the bug.
            //
            // "Nothing in it" is GoalTilePolicy.WorthConfirming's question, asked here so the two cannot
            // disagree: notes the tile wrote about itself are not a session. Counting messages instead
            // meant one line of "this tile cannot ask whether to discard..." created a file in the
            // user's repository that nothing would ever remove.
            if (!FileExists && state.OriginalGoal.Length == 0 && !GoalTilePolicy.WorthConfirming(state.Messages))
                return;

            persistence.Save(filePath, state);

            // A write that worked clears the flag, so a *later* failure is said out loud rather than
            // swallowed by a report about one that has since passed. The flag exists to stop a stream
            // of identical messages while a problem lasts, not to say the thing once per tile.
            _failureReported = false;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Failed to save goal state: {ex.Message}");
            if (_failureReported) return;

            _failureReported = true;
            Report($"This tile could not save its state ({ex.Message}). " +
                   "The conversation is on screen but will not survive a restart.");
        }
    }

    /// <summary>
    /// Reads the file, and decides what a failure to read it means for writing.
    /// <para>Returns null when there is nothing there. The two read failures are <b>not</b>
    /// interchangeable and are left to the caller as exceptions, because they need different things
    /// said in the transcript — but the consequence for writing is decided here, so no caller can get
    /// it wrong: a file that could not be <em>opened</em> is almost certainly intact and this store
    /// stops writing for good; a file that could not be <em>parsed</em> has already been set aside by
    /// the persistence layer, so starting fresh over the top of it is safe.</para>
    /// </summary>
    public GoalTileState? Load()
    {
        try
        {
            return persistence.Load(filePath);
        }
        catch (GoalStateUnavailableException)
        {
            _refused = true;
            throw;
        }
        catch (GoalStateUnreadableException)
        {
            throw;
        }
        catch
        {
            // The same refusal as an unreadable file, and for the same reason: whatever reached here is
            // by definition something nobody anticipated, and the caller is left half-populated — so
            // the next save would put that emptiness on top of the session it failed to read.
            _refused = true;
            throw;
        }
    }

    /// <summary>
    /// The last word, then nothing.
    /// </summary>
    /// <param name="flush">Whether to write on the way out. False for a tile that has nothing to save
    /// and no file to keep current: a Goal tile opened and closed without a word used to leave an empty
    /// session in the user's repository, and nothing here ever prunes those.</param>
    public void Dispose(bool flush)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // Directly, not through SaveNow, which now refuses — the flag is set above. Whatever the
        // debounce was still holding goes out here: a tile closed a moment after the tool answered must
        // not lose that answer to a timer that never got to fire.
        if (flush) Save(evenIfDisposed: true);

        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    public void Dispose() => Dispose(flush: true);
}
