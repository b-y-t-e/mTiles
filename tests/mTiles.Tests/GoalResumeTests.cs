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
        // Labelled with who said it: the list is joined into the next Clarify prompt and into the
        // Plan prompt, and answers alone left a numbered reply with no question above it.
        Assert.Equal(["User: an answer"], reloaded.ClarificationHistory);
        Assert.Equal("step one, step two", reloaded.ApprovedPlan);
        Assert.Equal(2, reloaded.IterationCount);
        Assert.Equal("VERDICT: FAIL — the second step is missing", reloaded.LastReviewFeedback);
        Assert.Equal(GoalPhase.Review, reloaded.CurrentPhase);
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
        var state = engine.ToState([], "claude-instance", "");
        var reloaded = new GoalWorkflowEngine();
        reloaded.LoadFrom(state);
        return reloaded;
    }

    /// <summary>
    /// A tile waiting on questions is waiting, not interrupted.
    /// </summary>
    /// <remarks>
    /// The signal used to be read off the transcript — the tool having spoken last — and that stopped
    /// being true the moment structured questions moved out of the transcript and into a panel of their
    /// own. The last turn is then the user's goal, so every tile with questions on screen came back from
    /// a restart calling itself interrupted, offering Resume, and Resume asks the same round again.
    /// </remarks>
    [Fact]
    public void Questions_waiting_for_an_answer_are_not_an_interrupted_run()
    {
        var waiting = new GoalTileState
        {
            CurrentPhase = GoalPhase.Clarify,
            Messages = [new GoalMessage { Role = GoalMessageRole.User, Text = "a goal" }],
            PendingQuestions = [new GoalQuestion { Question = "Which file?" }],
        };

        Assert.False(GoalWorkflowEngine.WasInterrupted(waiting));

        // And the same state with nothing pending is interrupted, which is what makes the line above
        // load-bearing rather than incidental.
        var cutOff = new GoalTileState
        {
            CurrentPhase = GoalPhase.Clarify,
            Messages = [new GoalMessage { Role = GoalMessageRole.User, Text = "a goal" }],
        };

        Assert.True(GoalWorkflowEngine.WasInterrupted(cutOff));
    }
}
