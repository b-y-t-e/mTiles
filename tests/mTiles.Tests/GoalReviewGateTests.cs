using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The pause between a review and the next attempt, and what a finding left unticked in it costs.
/// </summary>
/// <remarks>
/// Pure, so it is reachable at all: the loop these rules sit in needs an AI process and a git worktree
/// to turn over once, which is the same argument <see cref="GoalLoopPolicyTests"/> makes one rule over.
/// </remarks>
public class GoalReviewGateTests
{
    // The mode travels as its name for the reason GoalLoopPolicyTests spells out: the policy is
    // internal, a test class has to be public, and nameof keeps the compiler checking the cases.
    [Theory]
    // Off is off, whatever there is to look at.
    [InlineData(nameof(GoalReviewGateMode.Off), true, true, false)]
    [InlineData(nameof(GoalReviewGateMode.Off), false, true, false)]
    // The countdown exists to offer the pick, so with nothing to pick it is a wait charged for
    // nothing — a review of blockers alone, or one the parser could not structure.
    [InlineData(nameof(GoalReviewGateMode.Countdown), true, true, true)]
    [InlineData(nameof(GoalReviewGateMode.Countdown), false, true, false)]
    // Pause is somebody saying they want the lap to stop whatever it found. Skipping it because there
    // was nothing to tick would be the tile deciding it knew better.
    [InlineData(nameof(GoalReviewGateMode.Manual), false, true, true)]
    // And with the budget spent there is no next attempt for a pick to change: the loop is about to
    // summarise either way, so the gate would be a wait in front of an ending.
    [InlineData(nameof(GoalReviewGateMode.Countdown), true, false, false)]
    [InlineData(nameof(GoalReviewGateMode.Manual), true, false, false)]
    public void The_gate_opens_only_where_a_pick_could_change_something(
        string mode, bool pickable, bool hasNextAttempt, bool expected)
    {
        Assert.Equal(expected, GoalReviewGatePolicy.Opens(
            Enum.Parse<GoalReviewGateMode>(mode), pickable, hasNextAttempt));
    }

    [Fact]
    public void Touching_a_tick_stops_the_clock_and_it_does_not_start_again()
    {
        // The rule the whole feature was asked for. Somebody who has just decided one finding is not
        // worth fixing is still reading the rest of the list, and a clock running underneath them
        // re-implements the review they are in the middle of making up their mind about.
        var counting = GoalReviewGatePolicy.Initial(GoalReviewGateMode.Countdown);
        Assert.Equal(GoalGateState.Counting, counting);

        var stopped = GoalReviewGatePolicy.AfterEdit(counting);
        Assert.Equal(GoalGateState.Waiting, stopped);

        // Out of time and still waiting is still waiting: the clock is not restarted by a second tick
        // either, which would be a gate that can be held open only by fidgeting with it.
        Assert.False(GoalReviewGatePolicy.Expired(stopped, 0));
        Assert.False(GoalReviewGatePolicy.Expired(GoalReviewGatePolicy.AfterEdit(stopped), -5));
    }

    [Fact]
    public void A_manual_gate_never_counts_anything_down()
    {
        var state = GoalReviewGatePolicy.Initial(GoalReviewGateMode.Manual);

        Assert.Equal(GoalGateState.Waiting, state);
        Assert.False(GoalReviewGatePolicy.Expired(state, 0));
    }

    [Theory]
    [InlineData(15, 15)]
    // Zero is not a wait, it is Off wearing another name — and reaching it by typing in a box is how
    // somebody loses the feature without knowing they turned it off.
    [InlineData(0, GoalReviewGatePolicy.MinSeconds)]
    [InlineData(-30, GoalReviewGatePolicy.MinSeconds)]
    [InlineData(100_000, GoalReviewGatePolicy.MaxSeconds)]
    public void The_wait_is_bounded_where_it_is_used(int typed, int expected)
    {
        Assert.Equal(expected, GoalReviewGatePolicy.Seconds(typed));
    }

    [Fact]
    public void A_blocker_offers_no_tick()
    {
        // The one severity with no tolerance in the completion criteria either, and for the same
        // reason: a tick beside it would be that tolerance reached from somewhere else.
        Assert.False(GoalReviewGatePolicy.CanPick(GoalSeverity.Blocker));
        Assert.True(GoalReviewGatePolicy.CanPick(GoalSeverity.Error));
        Assert.True(GoalReviewGatePolicy.CanPick(GoalSeverity.Warning));
        Assert.False(GoalReviewGatePolicy.CanPick(GoalSeverity.Suggestion));
    }

    [Fact]
    public void The_line_says_which_of_the_two_states_the_gate_is_in()
    {
        // A block reading "continuing in 4 s" beside a clock that stopped two findings ago is worse
        // than one that says nothing, which is why the sentence comes from the state machine rather
        // than from the view.
        Assert.Contains("continuing in 4 s",
            GoalReviewGatePolicy.Line(GoalGateState.Counting, 4, fixing: 2, total: 3));
        Assert.Contains("paused",
            GoalReviewGatePolicy.Line(GoalGateState.Waiting, 4, fixing: 2, total: 3));

        // Never a negative: a tick can land after the wait is already spent.
        Assert.Contains("continuing in 0 s",
            GoalReviewGatePolicy.Line(GoalGateState.Counting, -2, fixing: 0, total: 1));
    }

    [Fact]
    public void A_dismissed_finding_stops_counting_against_the_goal()
    {
        // The promise the tick makes: unticked is not "kept out of the prompt and still blocking",
        // which is a run that spends every remaining attempt being refused for a defect nothing is
        // ever asked to fix.
        var error = new GoalFinding { Severity = GoalSeverity.Error, File = "a.cs", Title = "boom" };
        var review = new GoalReviewResult { GoalMet = true, WasStructured = true, Findings = [error] };
        var criteria = new GoalCompletionCriteria();

        Assert.False(GoalCompletionPolicy.IsMet(review, criteria));

        var accepted = GoalDismissals.Accepted(review, [error]);

        Assert.True(GoalCompletionPolicy.IsMet(accepted, criteria));
        Assert.Empty(accepted.Findings);

        // And the review itself is untouched, because the transcript draws it: a reader has to be able
        // to see what was dismissed as well as what was not.
        Assert.Single(review.Findings);
    }

    [Fact]
    public void Dismissing_a_finding_never_moves_the_reviewer_s_own_verdict()
    {
        // The verdict answers a different question from the findings, and the review prompt asks for
        // the two separately: a goalMet:false can rest on something no finding states — half a feature,
        // a case the goal named and the diff does not cover. There is no tick beside that, so a tick
        // must not reach it. Carried over with the findings, one unticked warning about a variable name
        // would finish a goal whose own review said the work was half done, and commit it.
        var warning = new GoalFinding
        {
            Severity = GoalSeverity.Warning, File = "a.cs", Title = "names the variable badly",
        };
        var review = new GoalReviewResult
        {
            GoalMet = false, WasStructured = true, Findings = [warning],
            RawText = "only the first page is implemented",
        };
        var criteria = new GoalCompletionCriteria();

        var accepted = GoalDismissals.Accepted(review, [warning]);

        Assert.Empty(accepted.Findings);
        Assert.False(accepted.GoalMet);
        Assert.False(GoalCompletionPolicy.IsMet(accepted, criteria));
    }

    [Fact]
    public void A_dismissal_is_matched_by_what_makes_two_findings_the_same_one()
    {
        // Severity, file, line and title — not the detail, which is an argument rather than an
        // identity and is rewritten on every run for the same defect.
        var dismissed = new GoalFinding
        {
            Severity = GoalSeverity.Warning, File = "A.cs", Title = "Leaks the handle", Line = 40,
            Detail = "the first review's words",
        };
        var again = new GoalFinding
        {
            Severity = GoalSeverity.Warning, File = "a.cs", Title = "leaks the handle", Line = 40,
            Detail = "the second review's completely different words",
        };
        var other = new GoalFinding
        {
            Severity = GoalSeverity.Warning, File = "A.cs", Title = "Leaks the lock", Line = 40,
        };

        // And the line is deliberately not part of it: the next implementation adds three lines above
        // this defect without fixing it, the reviewer reports it lower down, and a line-sensitive match
        // would let it back in ticked — counted against the criteria and back in the feedback, with the
        // user's decision undone in silence.
        var moved = new GoalFinding
        {
            Severity = GoalSeverity.Warning, File = "A.cs", Title = "Leaks the handle", Line = 43,
        };

        Assert.True(GoalDismissals.Contains([dismissed], again));
        Assert.False(GoalDismissals.Contains([dismissed], other));
        Assert.True(GoalDismissals.Contains([dismissed], moved));

        // One identity, shared with the review's own fingerprint, which compares two reviews taken
        // either side of the same implementation and is wrong in the same way if the line is in it.
        Assert.Equal(
            new GoalReviewResult { Findings = [dismissed] }.Fingerprint(),
            new GoalReviewResult { Findings = [moved] }.Fingerprint());
    }

    [Fact]
    public void What_is_dismissed_is_carried_back_to_the_next_reviewer()
    {
        // The half that actually works across laps. The reviewer is written from scratch each time and
        // phrases the same defect differently on every run, so a text match catches only the literal
        // repeat; what tells "dereferences null at 762" from "this can be null here" is the model
        // reading this list.
        var block = GoalDismissals.PromptBlock([
            new GoalFinding
            {
                Severity = GoalSeverity.Error, File = "A.cs", Line = 762, Title = "Dereferences null",
                Detail = "a page of argument nobody needs in order to recognise it again",
            },
        ]);

        Assert.Contains("error A.cs:762: Dereferences null", block);
        Assert.DoesNotContain("a page of argument", block);
        Assert.Equal("", GoalDismissals.PromptBlock([]));
    }

    [Fact]
    public void The_review_prompt_says_the_decision_has_already_been_taken()
    {
        var finding = new GoalFinding
        {
            Severity = GoalSeverity.Warning, File = "A.cs", Title = "Names the variable badly",
        };
        var engine = new GoalWorkflowEngine();
        engine.StartNewGoal("make it work");
        engine.SetFix(finding, fix: false);

        var prompt = engine.BuildReviewPrompt("diff");

        Assert.Contains("Names the variable badly", prompt);
        Assert.Contains("decided not to fix", prompt);

        // Never asked for separately, so no caller can build a review prompt that forgets it — which is
        // a reviewer raising, in fresh words, the finding the user unticked a minute ago.
        Assert.DoesNotContain("Names the variable badly", new GoalWorkflowEngine().BuildReviewPrompt("diff"));
    }

    [Fact]
    public void A_tick_taken_back_puts_the_finding_back()
    {
        var finding = new GoalFinding { Severity = GoalSeverity.Error, Title = "boom" };
        var engine = new GoalWorkflowEngine();

        Assert.True(engine.SetFix(finding, fix: false));
        // Saying the same thing twice is not a change, which is what keeps a reload from reading its
        // own restored ticks as the user moving them.
        Assert.False(engine.SetFix(finding, fix: false));
        Assert.Single(engine.Dismissed);

        Assert.True(engine.SetFix(finding, fix: true));
        Assert.Empty(engine.Dismissed);
        Assert.False(engine.SetFix(finding, fix: true));
    }

    [Fact]
    public void The_dismissals_travel_with_the_goal_and_not_with_the_review()
    {
        // They survive a restart, because the reviewer does not: it is run from scratch on every lap
        // and remembers nothing, so a dismissal kept on its own review would last exactly until the
        // next one was written.
        var engine = new GoalWorkflowEngine();
        engine.StartNewGoal("make it work");
        engine.ReviewGateMode = GoalReviewGateMode.Manual;
        engine.ReviewGateSeconds = 42;
        engine.SetFix(new GoalFinding { Severity = GoalSeverity.Warning, Title = "nit" }, fix: false);
        engine.LastReview = new GoalReviewResult { WasStructured = true, RawText = "as it came back" };

        var reopened = new GoalWorkflowEngine();
        reopened.LoadFrom(engine.ToState([], "", ""));

        Assert.Single(reopened.Dismissed);
        Assert.Equal(GoalReviewGateMode.Manual, reopened.ReviewGateMode);
        Assert.Equal(42, reopened.ReviewGateSeconds);
        Assert.Equal("as it came back", reopened.LastReview?.RawText);

        // A new goal takes them with it: they are answers about findings raised against work that is no
        // longer what is being asked for, and kept, they would quietly tell the next goal's reviewer
        // not to mention a defect nobody has looked at yet. How the tile is worked stays.
        reopened.StartNewGoal("something else entirely");

        Assert.Empty(reopened.Dismissed);
        Assert.Null(reopened.LastReview);
        Assert.Equal(GoalReviewGateMode.Manual, reopened.ReviewGateMode);
    }

    [Fact]
    public void A_goal_file_from_before_the_gate_existed_gets_the_countdown()
    {
        // Deliberate, and the whole point of the feature: a gate nobody has switched on is a gate
        // nobody uses. What it costs such a goal is a few seconds after each review, said on screen
        // while it costs them.
        var loaded = new GoalWorkflowEngine();
        loaded.LoadFrom(new GoalTileState());

        Assert.Equal(GoalReviewGateMode.Countdown, loaded.ReviewGateMode);
        Assert.Equal(GoalReviewGatePolicy.DefaultSeconds, loaded.GateSeconds);
    }
}
