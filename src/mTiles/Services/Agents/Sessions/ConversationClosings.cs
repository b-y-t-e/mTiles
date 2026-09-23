using System.Diagnostics;
using mTiles.AgentSessions.Hosting;

namespace mTiles.Services.Agents.Sessions;

/// <summary>
/// The conversations still closing after their tile has gone, so a shutdown can wait for them to be
/// written down.
/// </summary>
/// <remarks>
/// <para><b>A tile's <c>Dispose</c> is synchronous and a host's close is not.</b> Closing a host during a
/// turn ends that turn, takes the closing git checkpoint and only then drains the events into SQLite. An
/// application that leaves before that loses the turn's <c>CheckpointCaptured</c> — the diff and the Undo
/// button of a turn that changed files.</para>
/// <para><b>Bounded</b>, like <see cref="GitIgnoreEditQueue.WaitForAll"/>: a checkpoint has twenty
/// seconds of its own, and a close that outruns the wait loses what it would have lost anyway.</para>
/// </remarks>
public static class ConversationClosings
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<Task, string> Closing = [];

    /// <summary>How long the caller is blocked for in one go while it has work of its own to do.</summary>
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(50);

    /// <summary>Closes the host off the caller's thread and remembers the close until it finishes.</summary>
    public static void Close(AgentConversationHost host)
    {
        var close = Task.Run(async () => await host.DisposeAsync());
        lock (Gate) Closing.Add(close, host.ConversationId);
        close.ContinueWith(Forget, TaskScheduler.Default);
    }

    /// <summary>Completes once no host of this conversation is still closing.</summary>
    /// <remarks>A host numbers its events from what the store holds when it is built, so one opened while
    /// the previous host still writes its closing turn and checkpoint would take the same sequence
    /// numbers — and the store's <c>INSERT OR REPLACE</c> keeps only one of each pair.</remarks>
    public static Task WhenClosedAsync(string conversationId)
    {
        Task[] pending;
        lock (Gate)
            pending = [.. Closing.Where(entry => entry.Value == conversationId).Select(entry => entry.Key)];
        // A close that failed has still stopped writing, so its fault is not the opener's.
        return pending.Length == 0 ? Task.CompletedTask : Task.WhenAll(pending).ContinueWith(_ => { },
            TaskScheduler.Default);
    }

    /// <summary>Waits, briefly, for every close still in flight.</summary>
    /// <param name="timeout">The whole of what the wait may cost.</param>
    /// <param name="whileWaiting">Called between slices of the wait, for a caller that has to stay
    /// alive. The window closes on the UI thread, where a single blocking wait of twenty-five seconds
    /// is an application that has stopped redrawing and reads as hung; handing the dispatcher its
    /// pending work between slices keeps the window painting while the last checkpoints are written.
    /// Null waits in one go, which is what a caller with nothing to pump wants.</param>
    public static void WaitForAll(TimeSpan timeout, Action? whileWaiting = null)
    {
        Task[] pending;
        lock (Gate) pending = [.. Closing.Keys];
        if (pending.Length == 0) return;
        var spent = Stopwatch.StartNew();
        try
        {
            while (!Task.WaitAll(pending, NextWait(timeout - spent.Elapsed, whileWaiting)))
            {
                if (spent.Elapsed >= timeout)
                {
                    Trace.TraceWarning("{0} agent conversation(s) were still closing at shutdown.",
                        pending.Count(task => !task.IsCompleted));
                    return;
                }

                whileWaiting?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("An agent conversation failed to close: {0}", ex.Message);
        }
    }

    /// <summary>What is left of the budget, cut into slices for a caller that has to stay alive.</summary>
    private static TimeSpan NextWait(TimeSpan left, Action? whileWaiting)
    {
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        return whileWaiting is null || left < Slice ? left : Slice;
    }

    private static void Forget(Task close)
    {
        lock (Gate) Closing.Remove(close);
    }
}
