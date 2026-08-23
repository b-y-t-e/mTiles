namespace mTiles.Services;

/// <summary>How an AI run ended, which is not the same question as what it returned.</summary>
internal enum GoalRunVerdict
{
    /// <summary>The tool answered. Carry on.</summary>
    Answered,

    /// <summary>Stopped on purpose — the user paused, or the tile was closed. The phase stays where it
    /// is so Resume, and a restart, can pick it up there.</summary>
    Cancelled,

    /// <summary>The tool ran and produced nothing. That ends the run and summarises it.</summary>
    Empty,

    /// <summary>The tool was launched and something went wrong with it — it would not start, the pipe
    /// broke, it died. Handled exactly like <see cref="NoTool"/>, and for the same reason: a process
    /// that failed once is a thing that may well work on the next click, so ending the goal over it
    /// throws away an approved plan for a transient fault.</summary>
    Failed,

    /// <summary>There was no tool to run. Not the goal failing and not the user stopping it, so it is
    /// neither summarised nor blamed on the run: the tile stays where it is, paused, and whoever
    /// installs the tool can click Resume. Summarising it instead cost the user an approved plan
    /// because a binary was not on PATH.</summary>
    NoTool,
}

/// <summary>
/// The two decisions the implement/review loop takes, apart from the loop that carries them out.
/// <para>Both were inline conditions, and both were wrong: a cancelled run was summarised, which moved
/// the tile into a phase Resume has no case for, and a resumed run opened a fresh attempt, so a run
/// stopped and continued a few times gave up while the user still had attempts left. Neither was
/// reachable by a test while it lived inside a loop that needs an AI process and a git worktree to
/// turn over once — the same reason <see cref="ChainPolicy"/> exists.</para>
/// </summary>
internal static class GoalLoopPolicy
{
    /// <summary>
    /// What a finished run means. Cancellation is asked about first and separately, because the tool
    /// returns nothing in both cases and the difference is the whole of whether the tile can be
    /// resumed afterwards.
    /// </summary>
    /// <param name="toolMissing">Nothing was launched at all. Asked before cancellation only because
    /// a run that never started cannot have been stopped.</param>
    /// <param name="failed">The tool was launched and threw. Asked after cancellation, because killing
    /// a process is a normal way to make it throw and the user's intent is the more important of the
    /// two facts.</param>
    public static GoalRunVerdict Judge(string? response, bool cancelled, bool toolMissing = false, bool failed = false) =>
        toolMissing ? GoalRunVerdict.NoTool
        : cancelled ? GoalRunVerdict.Cancelled
        : failed ? GoalRunVerdict.Failed
        : string.IsNullOrWhiteSpace(response) ? GoalRunVerdict.Empty
        : GoalRunVerdict.Answered;

    /// <summary>
    /// Which attempt the loop runs next, or <c>null</c> when the budget is spent and the run is over.
    /// </summary>
    /// <param name="spent">Attempts already begun — <see cref="GoalWorkflowEngine.IterationCount"/>.</param>
    /// <param name="max">The budget, <see cref="GoalWorkflowEngine.MaxIter"/>.</param>
    /// <param name="finishInterrupted">This lap finishes the attempt that was already under way rather
    /// than opening a new one. True only for the first lap after a resume: the interrupted attempt has
    /// been paid for once already, and it is still finishable when it was the last of the budget — which
    /// is why this is asked before the budget is, and not after.</param>
    public static int? NextAttempt(int spent, int max, bool finishInterrupted) =>
        // Clamped, because `spent` comes off disk and a file can say anything. Unclamped, a count above
        // the budget was resumed at its own number and the tile reported "attempt 99 of 5".
        finishInterrupted ? Math.Clamp(spent, 1, max)
        : spent < max ? spent + 1
        : null;
}
