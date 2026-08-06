namespace mTiles.Services;

/// <summary>
/// How often a launch chain may start something again before it stops believing in it: at most
/// <c>max</c> times within <c>window</c>, counted for the chain as a whole.
/// <para>A rate over a window, not a running total — that is the difference between "this is looping"
/// and "this has been used for weeks". A total would give up on a tool the user works in daily once its
/// fourth crash came round, however many months apart the four were; a window forgets, so only
/// relaunches that actually pile up count as a loop.</para>
/// <para>And for the chain as a whole, not per command, which is the part that has to be structural.
/// A per-command budget reset when the chain moved on, and a chain that walks to its fallback and back
/// to the top would renew it every lap — a bounded rule with an unbounded loop through the middle of
/// it. There is deliberately no way to reset this but the passing of time.</para>
/// </summary>
/// <param name="max">How many relaunches the window allows.</param>
/// <param name="window">Milliseconds the count is kept over.</param>
/// <param name="now">The clock, in milliseconds, monotonic. Injectable so the expiry rule can be
/// tested by moving time rather than by sleeping through it — a test that waits out a real window
/// either takes as long as the window or shrinks the window until it is testing the scheduler.</param>
internal sealed class RelaunchBudget(int max, int window, Func<long>? now = null)
{
    private readonly Func<long> _now = now ?? (() => Environment.TickCount64);

    /// <summary>When each relaunch still inside the window was spent, oldest first — never more than
    /// <c>max</c> of them. A sliding window rather than a counter reset every <c>window</c>
    /// milliseconds: with a tumbling one, a burst straddling the boundary spends the last of one
    /// window and the first of the next back to back, so the rule permits twice its own limit at
    /// exactly the moment something is going wrong fast.</summary>
    private readonly Queue<long> _spent = new();

    /// <summary>Takes one relaunch out of the budget. False when there is none left, in which case
    /// nothing is taken — the caller is expected to stop relaunching, not to keep asking.</summary>
    public bool TrySpend()
    {
        long now = _now();
        while (_spent.Count > 0 && now - _spent.Peek() > window)
            _spent.Dequeue();

        if (_spent.Count >= max)
            return false;

        _spent.Enqueue(now);
        return true;
    }
}
