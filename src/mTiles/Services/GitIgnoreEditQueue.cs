namespace mTiles.Services;

/// <summary>
/// Runs the application's <c>.gitignore</c> edits one after another, off the caller's thread, and lets
/// a shutdown wait briefly for the ones still queued.
/// </summary>
/// <remarks>
/// <para><b>Why a queue at all.</b> <see cref="GitIgnoreFile"/> is asynchronous and its callers are
/// not — and blocking on it is not open to them, since they are reached from the UI thread on every
/// layout change. A chain rather than a fire-and-forget task each: a line written and then withdrawn a
/// moment later must not survive because the two ran in the order the thread pool felt like.</para>
/// <para><b>One chain for the whole application</b>, which is what <see cref="GitIgnoreFile"/>'s own
/// gate already is: two workspaces editing two different files still serialise, which costs nothing —
/// these are single-line edits to a small file — and removes the one way two of them could interleave
/// on the same repository, which a workspace and a sub-workspace inside it can be.</para>
/// <para>Its own class rather than a static corner of whoever queues an edit: ordering, atomicity and
/// what happens at shutdown are one reason to change, shared by every caller, and none of them is
/// about the files any one workspace puts where its agents look.</para>
/// </remarks>
public static class GitIgnoreEditQueue
{
    private static Task _edits = Task.CompletedTask;
    private static readonly Lock Gate = new();

    /// <summary>Everything queued so far, so a test can see the lines land. Nothing in the application
    /// waits on it: an ignore entry is housekeeping, and no screen is held up for it.</summary>
    internal static Task Pending
    {
        get { lock (Gate) return _edits; }
    }

    /// <summary>Adds an edit to the end of the chain.</summary>
    /// <remarks>The edit is expected to answer its own failures: a chain that faulted would carry the
    /// fault into every edit queued after it.</remarks>
    public static void Enqueue(Func<Task> edit)
    {
        lock (Gate)
            _edits = _edits
                .ContinueWith(_ => edit(), TaskScheduler.Default)
                .Unwrap();
    }

    /// <summary>Waits, briefly, for the queued edits to finish.</summary>
    /// <remarks><b>Called while the application is closing, and that is the whole reason it exists.</b>
    /// The chain runs on the thread pool and nothing else waits on it, so an edit queued moments before
    /// the process ends could be abandoned in the middle of <see cref="GitIgnoreFile"/>'s write-and-move
    /// — leaving a <c>.gitignore.mtiles-tmp</c> in somebody's repository, which is the litter the atomic
    /// write exists to prevent, and an entry that outlives the skill it names. Bounded, because no
    /// shutdown is worth hanging on housekeeping: a wait that runs out leaves the edit where it already
    /// was.</remarks>
    public static void WaitForAll(TimeSpan timeout)
    {
        try
        {
            Pending.Wait(timeout);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "The .gitignore edits did not finish before shutdown: {0}", ex.Message);
        }
    }
}
