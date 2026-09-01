using System.Text.RegularExpressions;
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
internal static partial class GoalTilePolicy
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
    /// Whether Resume can actually pick this phase up.
    /// <para>The working phases and the two the user answers. <b>Not Goal or Summary</b> — there is no
    /// run to continue there, and <c>ResumeAsync</c>'s own <c>default:</c> case does nothing but write
    /// the cleared pause. Every "click Resume to try again" the tile prints has to ask this first: on
    /// the detection path, which runs from Goal, the advice pointed at a button that either was not
    /// there or did nothing when pressed.</para>
    /// </summary>
    public static bool CanResume(GoalPhase phase) =>
        phase is GoalPhase.Implement or GoalPhase.Review or GoalPhase.Clarify or GoalPhase.Plan;

    /// <summary>
    /// Whether starting a fresh goal over the top of this transcript is worth a confirmation.
    /// <para>Decided by what would be lost, not by which phase the tile is in: a Clarify that failed
    /// puts the engine back to Goal while the goal, the answers and the tool's replies are all still on
    /// screen. Notes the tile wrote about itself are not worth interrupting anybody over.</para>
    /// </summary>
    public static bool WorthConfirming(IEnumerable<GoalMessage> messages) =>
        messages.Any(m => m.Role != GoalMessageRole.System);

    /// <summary>
    /// How many times a run broken by a dropped stream is retried without asking.
    /// <para>Raised once, deliberately — the number, not the mechanism, is what changes if a provider
    /// proves flakier than this. Every retry costs the tool's whole turn again, so one is what is
    /// honest today; raising it is a one-line decision, and the view model reads the answer from here
    /// rather than owning a constant of its own.</para>
    /// </summary>
    public const int BrokenStreamRetries = 1;

    /// <summary>
    /// Whether a failed run's text is the provider dropping the stream rather than the tool refusing
    /// to work.
    /// <para>A gateway (OpenRouter, a local server) can end a long turn mid-stream — Claude Code
    /// reports it as <c>API Error: stream closed before completion</c> and exits non-zero — which is a
    /// network fault, not a verdict on the work. Matched on the words rather than the exit code,
    /// because a broken pipe and a refused flag leave the same exit status and only one of them is
    /// worth retrying unasked.</para>
    /// </summary>
    public static bool LooksLikeBrokenStream(string? text) =>
        text is not null && BrokenStream().IsMatch(text);

    [GeneratedRegex(@"stream\s+(was\s+)?closed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BrokenStream();
}
