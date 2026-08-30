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

    [Fact]
    public void An_empty_tile_is_discarded_without_a_dialog()
    {
        Assert.False(GoalTilePolicy.WorthConfirming([]));
    }

    [Fact]
    public void Notes_the_tile_wrote_about_itself_are_not_worth_interrupting_anybody_over()
    {
        GoalMessage[] onlyNotes =
        [
            new() { Role = GoalMessageRole.System, Text = "AI returned an empty response. Try again." }
        ];

        Assert.False(GoalTilePolicy.WorthConfirming(onlyNotes));
    }

    [Fact]
    public void A_transcript_with_anything_of_the_users_in_it_is_asked_about()
    {
        // Decided by content, not by phase: a Clarify that failed puts the engine back to Goal while
        // the goal, the answers and the tool's replies are all still on screen, and asking by phase let
        // the next thing typed wipe them without a word.
        GoalMessage[] afterAFailedClarify =
        [
            new() { Role = GoalMessageRole.User, Text = "make the tile resumable", Phase = GoalPhase.Goal },
            new() { Role = GoalMessageRole.System, Text = "AI returned an empty response. Try again." }
        ];

        Assert.True(GoalTilePolicy.WorthConfirming(afterAFailedClarify));
    }
}
