using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// The Goal tile's rules that are about the tile rather than about the loop: when a pause is spent,
/// when closing counts as one, and when throwing a transcript away is worth asking about.
/// <para>Here rather than inside the view model for the reason <see cref="GoalLoopPolicy"/> exists, and
/// with more evidence behind it: every one of these was a condition written inline, and every one of
/// them was wrong at least once — a pause left standing so the run it started was thrown away, a pause
/// applied to every idle tile so each came back claiming to be interrupted, and a dialog asked for by
/// phase rather than by content so a failed Clarify let the next thing typed wipe the session.</para>
/// </summary>
internal static class GoalTilePolicy
{
    /// <summary>
    /// Whether sending something from the composer also clears an outstanding pause.
    /// <para>Everywhere except the working phases, where the composer has nothing to send and Resume is
    /// the way on. Leaving the pause standing meant the run started, happened, and was discarded at the
    /// first hand-over that asks about a pause — a whole implementation spent on nothing.</para>
    /// </summary>
    public static bool AnsweringResumes(GoalPhase phase) =>
        !GoalWorkflowEngine.IsMidRun(phase);

    /// <summary>
    /// Whether closing the tile should record it as paused.
    /// <para>Only when something was actually running. It has to be recorded then, because a bare
    /// cancellation is reported as a system message that becomes the last line of the transcript, and
    /// an unanswered Clarify then looks like one that has its answer. Recording it always is worse than
    /// never: every idle tile came back claiming to be paused, and Resume asked its questions again.
    /// </para>
    /// </summary>
    public static bool ClosingIsAPause(bool isRunning) => isRunning;

    /// <summary>
    /// Whether resuming should go straight to the review rather than implementing first.
    /// <para>True exactly when the interrupted phase was the review: the implementation before it
    /// finished, its answer is in the transcript and its changes are on disk, so running it again asks
    /// the tool to redo work it can see it has done — usually a no-op, sometimes a duplicate, and
    /// always in the user's own worktree.</para>
    /// </summary>
    public static bool ResumesAtReview(GoalPhase phase) => phase == GoalPhase.Review;

    /// <summary>
    /// Whether starting a fresh goal over the top of this transcript is worth a confirmation.
    /// <para>Decided by what would be lost, not by which phase the tile is in: a Clarify that failed
    /// puts the engine back to Goal while the goal, the answers and the tool's replies are all still on
    /// screen. Notes the tile wrote about itself are not worth interrupting anybody over.</para>
    /// </summary>
    public static bool WorthConfirming(IEnumerable<GoalMessage> messages) =>
        messages.Any(m => m.Role != GoalMessageRole.System);
}
