using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What "continue where you left off" means for a Goal tile. The rule is an opinion — which phases a
/// restart can resume, and what the tile says while it waits to be told to — so it is argued here
/// rather than left to be re-derived from the view model, where it cannot be run without a window.
/// </summary>
public class GoalResumeTests
{
    [Theory]
    [InlineData(GoalPhase.Implement, true)]
    [InlineData(GoalPhase.Review, true)]
    [InlineData(GoalPhase.Goal, false)]
    [InlineData(GoalPhase.Clarify, false)]
    [InlineData(GoalPhase.Plan, false)]
    [InlineData(GoalPhase.Summary, false)]
    public void Only_the_phases_the_tool_works_in_are_resumable(GoalPhase phase, bool midRun)
    {
        // The others are waiting for the user, and a tile waiting for the user is not interrupted —
        // it is exactly where it was left, with the composer as the way on.
        Assert.Equal(midRun, GoalWorkflowEngine.IsMidRun(phase));
    }

    [Fact]
    public void A_run_reloaded_mid_flight_offers_to_carry_on_rather_than_claiming_to_be_working()
    {
        var engine = new GoalWorkflowEngine();
        engine.StartNewGoal("make the tile resumable");
        engine.RecordProposedPlan("the plan");
        Assert.True(engine.ApprovePlan());
        engine.IterationCount = 1;
        engine.CurrentPhase = GoalPhase.Implement;

        var reloaded = Reload(engine);

        // The whole of the feature, and LoadFrom does it rather than the caller: an interrupted run is
        // a pause nobody asked for. Without it the tile came back mid-run with nothing running and
        // Submit answered "AI is working, please wait" for ever.
        Assert.True(GoalWorkflowEngine.IsMidRun(reloaded.CurrentPhase));
        Assert.True(reloaded.IsPaused);

        Assert.Contains("Resume", reloaded.GetPhaseLabel());
        Assert.Contains("implement", reloaded.GetPhaseLabel(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_pause_while_waiting_for_the_user_still_reads_as_a_pause()
    {
        var engine = new GoalWorkflowEngine();
        engine.StartNewGoal("goal");
        engine.CurrentPhase = GoalPhase.Plan;
        engine.IsPaused = true;

        // The label says "stopped during X" only where there was an X to be stopped during.
        Assert.Equal("Paused. Click Resume to continue.", engine.GetPhaseLabel());
    }

    [Fact]
    public void The_plan_and_the_iteration_survive_the_round_trip()
    {
        // Resuming re-runs the implement/review loop from the top of an iteration, which is only
        // honest if what the loop reads was saved: the approved plan, how many attempts are spent,
        // and the review that asked for the next one.
        var engine = new GoalWorkflowEngine();
        engine.StartNewGoal("the goal");
        engine.RecordClarification("an answer");
        engine.RecordProposedPlan("step one, step two");
        Assert.True(engine.ApprovePlan());
        engine.IterationCount = 2;
        engine.RecordReviewFeedback("VERDICT: FAIL — the second step is missing");
        engine.CurrentPhase = GoalPhase.Review;

        var reloaded = Reload(engine);

        Assert.Equal("the goal", reloaded.OriginalGoal);
        Assert.Equal(["an answer"], reloaded.ClarificationHistory);
        Assert.Equal("step one, step two", reloaded.ApprovedPlan);
        Assert.Equal(2, reloaded.IterationCount);
        Assert.Equal("VERDICT: FAIL — the second step is missing", reloaded.LastReviewFeedback);
        Assert.Equal(GoalPhase.Review, reloaded.CurrentPhase);
    }

    [Fact]
    public void An_iteration_spent_before_the_interruption_stays_spent()
    {
        // The budget is five attempts at the goal, not five per launch: a tile closed and reopened
        // in a loop that is not converging must not get its attempts back each time.
        var engine = new GoalWorkflowEngine();
        engine.StartNewGoal("goal");
        engine.RecordProposedPlan("plan");
        Assert.True(engine.ApprovePlan());
        engine.IterationCount = engine.MaxIter;
        engine.CurrentPhase = GoalPhase.Implement;

        var reloaded = Reload(engine);

        Assert.Null(GoalLoopPolicy.NextAttempt(reloaded.IterationCount, reloaded.MaxIter, finishInterrupted: false));
    }

    [Fact]
    public void A_tile_waiting_for_an_answer_is_not_an_interrupted_one()
    {
        // Clarify and Plan each cover two situations with one value: asking the tool, and waiting for
        // the user. Calling both interrupted would have every tile ever closed at a question come back
        // claiming to be paused, and Resume would ask the same questions over again.
        var waiting = new GoalTileState
        {
            CurrentPhase = GoalPhase.Clarify,
            Messages = [new GoalMessage { Role = GoalMessageRole.Assistant, Text = "What should it do?" }]
        };

        Assert.False(GoalWorkflowEngine.WasInterrupted(waiting));
    }

    [Fact]
    public void A_question_that_was_never_answered_is_an_interrupted_one()
    {
        // The other half: the prompt went out and the application closed before the answer came back,
        // so the user's own message is still the last thing in the transcript. Without this the tile
        // came back saying "answer the questions above" with no questions above it.
        var cutOff = new GoalTileState
        {
            CurrentPhase = GoalPhase.Clarify,
            Messages = [new GoalMessage { Role = GoalMessageRole.User, Text = "make it faster" }]
        };

        Assert.True(GoalWorkflowEngine.WasInterrupted(cutOff));

        var engine = new GoalWorkflowEngine();
        engine.LoadFrom(cutOff);
        Assert.True(engine.IsPaused);
    }

    [Fact]
    public void A_fresh_tile_is_neither()
    {
        Assert.False(GoalWorkflowEngine.WasInterrupted(new GoalTileState()));
    }

    /// <summary>The save-and-load a restart puts the state through, without touching a disk.</summary>
    private static GoalWorkflowEngine Reload(GoalWorkflowEngine engine)
    {
        var state = engine.ToState([], "Claude Code");
        var reloaded = new GoalWorkflowEngine();
        reloaded.LoadFrom(state);
        return reloaded;
    }
}
