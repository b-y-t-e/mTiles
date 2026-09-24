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

    /// <summary>
    /// Whether a saved tile was cut off mid-run. Clarify covers both asking the tool and waiting for the
    /// user, so the transcript and the pending questions decide which.
    /// </summary>
    [Theory]
    // The tool spoke last: waiting for the user.
    [InlineData("assistant last", false)]
    // The user's own message is last: the prompt went out and nothing came back.
    [InlineData("user last", true)]
    // Structured questions leave the transcript, so the user's goal is last and yet the tile is waiting.
    [InlineData("questions pending", false)]
    [InlineData("fresh", false)]
    public void A_tile_is_interrupted_only_when_the_tool_owes_an_answer(string shape, bool interrupted)
    {
        var state = shape switch
        {
            "assistant last" => new GoalTileState
            {
                CurrentPhase = GoalPhase.Clarify,
                Messages = [new GoalMessage { Role = GoalMessageRole.Assistant, Text = "What should it do?" }],
            },
            "user last" => new GoalTileState
            {
                CurrentPhase = GoalPhase.Clarify,
                Messages = [new GoalMessage { Role = GoalMessageRole.User, Text = "a goal" }],
            },
            "questions pending" => new GoalTileState
            {
                CurrentPhase = GoalPhase.Clarify,
                Messages = [new GoalMessage { Role = GoalMessageRole.User, Text = "a goal" }],
                PendingQuestions = [new GoalQuestion { Question = "Which file?" }],
            },
            _ => new GoalTileState(),
        };

        Assert.Equal(interrupted, GoalWorkflowEngine.WasInterrupted(state));

        // Loading an interrupted state is what pauses it.
        var engine = new GoalWorkflowEngine();
        engine.LoadFrom(state);
        Assert.Equal(interrupted, engine.IsPaused);
    }

    /// <summary>A file written before "reviews existing work" and "read from the tree" were two facts
    /// reads the second off the first: absent means "ask the older field", never false.</summary>
    [Fact]
    public void A_state_written_before_the_split_reads_its_claim_off_the_older_flag()
    {
        var engine = new GoalWorkflowEngine();

        engine.LoadFrom(new GoalTileState
        {
            OriginalGoal = "Finish the cart",
            ReviewsExistingWork = true,
            GoalReadFromTheTree = null,
        });

        Assert.True(engine.GoalReadFromTheTree);
    }

    /// <summary>The save-and-load a restart puts the state through, without touching a disk.</summary>
    private static GoalWorkflowEngine Reload(GoalWorkflowEngine engine)
    {
        var state = engine.ToState([], "claude-instance", "");
        var reloaded = new GoalWorkflowEngine();
        reloaded.LoadFrom(state);
        return reloaded;
    }
}
