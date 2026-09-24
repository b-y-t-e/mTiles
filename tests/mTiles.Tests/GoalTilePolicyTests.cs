using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The Goal tile's rules about the tile rather than about the loop. They lived inline in the view
/// model, which is why each of them was wrong at least once and none of the mistakes was catchable
/// here — the last few rounds of review landed on exactly these three conditions.
/// </summary>
public class GoalTilePolicyTests
{
    [Theory]
    [InlineData(GoalPhase.Goal, true)]
    [InlineData(GoalPhase.Clarify, true)]
    [InlineData(GoalPhase.Plan, true)]
    [InlineData(GoalPhase.Summary, true)]
    // Not in the working phases: there the composer has nothing to send and Resume is the way on.
    [InlineData(GoalPhase.Implement, false)]
    [InlineData(GoalPhase.Review, false)]
    public void Answering_spends_the_pause_wherever_the_composer_can_send(GoalPhase phase, bool clears)
    {
        // Leaving it standing meant the run started, happened, and was discarded at the first hand-over
        // that asks about a pause — a whole implementation spent on nothing.
        Assert.Equal(clears, GoalTilePolicy.AnsweringResumes(phase));
    }

    [Fact]
    public void Closing_a_working_tile_is_a_pause_and_closing_an_idle_one_is_not()
    {
        // Both halves have been wrong in turn. Never recording it left an unanswered Clarify looking
        // like one that had its answer, because the cancellation's own note became the last message.
        // Always recording it had every idle tile come back claiming to be paused, with Resume asking
        // its questions a second time.
        Assert.True(GoalTilePolicy.ClosingIsAPause(isRunning: true));
        Assert.False(GoalTilePolicy.ClosingIsAPause(isRunning: false));
    }

    [Fact]
    public void Pausing_a_review_then_restarting_resumes_the_review_and_spends_no_extra_attempt()
    {
        // The whole journey the last few rounds were about, joined up: a run stopped in Review, written
        // to disk, read back by a new tile, and handed to Resume. Each rule has a test of its own; this
        // is the one that fails if they stop agreeing with each other.
        var engine = new GoalWorkflowEngine();
        engine.StartNewGoal("make the tile resumable");
        engine.RecordProposedPlan("the plan");
        Assert.True(engine.ApprovePlan());
        engine.IterationCount = 2;
        engine.CurrentPhase = GoalPhase.Review;

        var reloaded = new GoalWorkflowEngine();
        reloaded.LoadFrom(engine.ToState(
            [new GoalMessage { Role = GoalMessageRole.Assistant, Text = "implemented", Phase = GoalPhase.Implement }],
            "claude-instance", ""));

        // The tile comes back offering to carry on rather than claiming to work.
        Assert.True(reloaded.IsPaused);

        // Resume goes to the review, not through the implementation that already finished...
        Assert.True(GoalTilePolicy.ResumesAtReview(reloaded.CurrentPhase));

        // ...and finishes the attempt already paid for rather than opening a third.
        Assert.Equal(2, GoalLoopPolicy.NextAttempt(reloaded.IterationCount, reloaded.MaxIter, finishInterrupted: true));
    }

    /// <summary>
    /// Discarding a transcript asks first only when something of the user's is in it — decided by
    /// content, not by phase, because a failed Clarify puts the phase back to Goal with the answers still
    /// on screen.
    /// </summary>
    [Theory]
    [InlineData(false, false)]   // an empty tile
    [InlineData(false, true)]    // only the tile's own notes
    [InlineData(true, true)]     // the user's goal beside a note
    public void Discarding_asks_only_when_the_transcript_holds_something_of_the_users(
        bool usersGoal, bool tilesNote)
    {
        var messages = new List<GoalMessage>();
        if (usersGoal)
            messages.Add(new() { Role = GoalMessageRole.User, Text = "make the tile resumable", Phase = GoalPhase.Goal });
        if (tilesNote)
            messages.Add(new() { Role = GoalMessageRole.System, Text = "AI returned an empty response. Try again." });

        Assert.Equal(usersGoal, GoalTilePolicy.WorthConfirming(messages.ToArray()));
    }

    [Theory]
    [InlineData("API Error: stream closed before completion\n\n[error] API Error: stream closed before completion", true)]
    [InlineData("half a plan\n\n[error] The stream was closed by the server", true)]
    [InlineData("STREAM CLOSED", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    // A refused flag exits with the same status and must not be retried unasked.
    [InlineData("error: unknown option '--effort'\n\n[stderr] claude: unknown option --effort", false)]
    public void A_dropped_stream_is_named_as_such_and_nothing_else_is(string? text, bool broken)
    {
        // Measured 2026-09-01 on Claude Code 2.1.251 over OpenRouter: the dropped stream exits non-zero
        // with these words, the same status a refused flag leaves — which is why the match is on the
        // words and not on the exit code, and why the negatives matter: a usage message is a decision
        // about the command line, and retrying it unasked would run the same refusal again.
        Assert.Equal(broken, GoalTilePolicy.LooksLikeBrokenStream(text));
    }
}
