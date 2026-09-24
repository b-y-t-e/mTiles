using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The implement/review loop driven end to end, with the AI replaced through
/// <see cref="GoalTileViewModel.AiRunnerFactory"/>: this loop is where most Goal tile bugs landed, and
/// each needed a real process and a real worktree to reach. The seams are owned by
/// <see cref="GoalTileFixture"/>.
/// </summary>
[Collection(GoalSeamCollection.Name)]
public class GoalWorkflowLoopTests : GoalTileFixture
{
    /// <summary>Waits for the tile's file to appear — messages are written on a debounce.</summary>
    private static void WaitForFile(GoalTileViewModel vm)
    {
        var deadline = Environment.TickCount64 + AppDefaults.SaveDebounceMs * 100;
        while (!File.Exists(vm.FilePath) && Environment.TickCount64 < deadline)
            Thread.Sleep(AppDefaults.SaveDebounceMs / 5);
        Assert.True(File.Exists(vm.FilePath), "the goal file was never written");
    }

    private static IReadOnlyList<GoalFinding> LastFindings(GoalTileViewModel vm) =>
        vm.Messages.Last(m => m.Findings is { Count: > 0 }).Findings!;

    /// <summary>What one badge's popup would list.</summary>
    private static IEnumerable<string> Titles(GoalTileViewModel vm, GoalSeverity severity) =>
        vm.Badges.Single(b => b.Severity == severity).Findings.Select(f => f.Title);

    private const string TwoErrorsReview =
        "```json\n{\"goalMet\":false,\"findings\":[" +
        "{\"severity\":\"error\",\"title\":\"null deref\",\"file\":\"a.cs\"}," +
        "{\"severity\":\"error\",\"title\":\"race on save\",\"file\":\"b.cs\"}]}\n```";

    // ── The composer's history ──────────────────────────

    [Fact]
    public void The_composer_history_outlives_the_goal_it_started()
    {
        Ui.Run(async () =>
        {
            AnswerWith(ThroughTheReview("VERDICT: PASS"));

            using var vm = NewTile();

            await Send(vm, "make the tile resumable");
            await Send(vm, "all of them");
            await vm.ApproveOrChangeCommand.ExecuteAsync(null); // an empty plan box approves as "ok"
            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);

            // A new goal clears the transcript; what was typed before it must still be one Up away.
            await Send(vm, "now make it pausable");

            Assert.Equal(["make the tile resumable", "all of them", "now make it pausable"], vm.SentFromComposer);
        });
    }

    [Fact]
    public void The_composer_history_survives_a_restart()
    {
        Ui.Run(async () =>
        {
            AnswerWith("Which files?");

            var first = NewTile();
            await Send(first, "make the tile resumable");

            using var second = Reopen(first);

            Assert.Equal(["make the tile resumable"], second.SentFromComposer);
        });
    }

    [Fact]
    public void Words_handed_back_by_a_stopped_run_are_not_remembered_as_sent()
    {
        Ui.Run(async () =>
        {
            using var vm = NewTile();

            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                if (asked == 4) vm.PauseCommand.Execute(null);
                return Task.FromResult<AiOutput>(UpToTheReview[Math.Min(asked, 4) - 1]);
            };

            await Send(vm, "a goal");
            await Send(vm, "all of it");
            await Send(vm, "ok");
            Assert.Equal(GoalPhase.Review, vm.CurrentPhase);

            await Send(vm, "also fix the tests");

            Assert.Equal("also fix the tests", vm.InputText);
            Assert.Equal(["a goal", "all of it", "ok"], vm.SentFromComposer);
        });
    }

    [Fact]
    public void A_detected_goal_is_not_something_the_user_sent_but_the_scope_beside_it_is()
    {
        Ui.Run(async () =>
        {
            AnswerWith("Finish the pairing flow.", "VERDICT: PASS");

            using var vm = NewTile();
            WriteFile("pairing.cs", "// changed");

            vm.InputText = "only the pairing";
            await vm.DetectGoalAndRunCommand.ExecuteAsync(null);

            Assert.Equal(["only the pairing"], vm.SentFromComposer);
        });
    }

    // ── A run to its summary ────────────────────────────

    [Fact]
    public void A_goal_runs_through_to_a_summary_when_the_review_passes()
    {
        Ui.Run(async () =>
        {
            AnswerWith(ThroughTheReview("VERDICT: PASS"));

            using var vm = NewTile();
            await RunToSummary(vm);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);

            // A finished run is not a paused one, whatever route it took there.
            Assert.False(vm.IsPaused);
            Assert.DoesNotContain("Resume", vm.PhaseLabel);
            Assert.False(vm.IsRunning);
        });
    }

    /// <summary>
    /// A run offers to commit only when it ends with no blockers or errors and a baseline to tell its
    /// work from the user's.
    /// </summary>
    [Theory]
    [InlineData(CleanReview, true, true)]    // clean and bounded: offered
    [InlineData(CleanReview, false, false)]  // no snapshot, so nothing can tell this run's work apart
    [InlineData(ErrorReview, true, false)]   // an error closes the offer
    public void A_run_offers_to_commit_only_when_it_ends_clean_over_a_baseline(
        string review, bool baseline, bool offered)
    {
        Ui.Run(async () =>
        {
            AnswerWith(ThroughTheReview(review));
            if (baseline) WithBaseline();

            using var vm = NewTile();
            await RunToSummary(vm);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.False(vm.IsRunning);
            Assert.Equal(offered, vm.CanCommit);
        });
    }

    /// <summary>Review judges the tree once (goal, then review — never an implementation) and offers
    /// Re-review and Continue.</summary>
    [Fact]
    public void Reviewing_judges_the_tree_once_and_offers_the_two_things_that_follow()
    {
        Ui.Run(async () =>
        {
            var prompts = Script("Finish the pairing flow.", WarningReview);
            WithBaseline();

            using var vm = NewTile();
            await vm.ReviewCommand.ExecuteAsync(null);

            Assert.Equal(2, prompts.Count);
            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Text.Contains("Reviewed the working tree"));
            Assert.True(vm.CanReReview);

            // No attempt has been spent, so the label counts what is already there rather than a `+`.
            Assert.True(vm.CanContinue);
            Assert.Equal($"Continue · {vm.Criteria.MaxIterations} left", vm.ContinueLabel);
            Assert.True(vm.HasFinishedRunActions);
        });
    }

    /// <summary>Falling out of the loop is the budget running out, and Continue can raise it; the
    /// sentence itself is pinned in GoalCompletionPolicyTests.</summary>
    [Fact]
    public void A_failing_review_is_re_implemented_until_the_budget_runs_out()
    {
        Ui.Run(async () =>
        {
            AnswerWith(ThroughTheReview("VERDICT: FAIL — not yet"));

            using var vm = NewTile();
            await RunToSummary(vm);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.True(vm.CanContinue);
            Assert.Equal(GoalStopReason.BudgetSpent, Saved(vm).LastStopReason);
        });
    }

    /// <summary>An attempt that writes nothing ends the run, but the attempts still owed stay reachable
    /// through Continue, and the button says how many.</summary>
    [Fact]
    public void An_attempt_that_changes_nothing_stops_the_run_but_leaves_the_budget_reachable()
    {
        Ui.Run(async () =>
        {
            TreeNeverMoves();
            AnswerWith(ThroughTheReview("VERDICT: FAIL"));

            using var vm = NewTile();
            await RunToSummary(vm);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.True(vm.CanContinue);
            Assert.Equal($"Continue · {vm.Criteria.MaxIterations - 1} left", vm.ContinueLabel);
            Assert.Equal(GoalStopReason.NoChange, Saved(vm).LastStopReason);
        });
    }

    /// <summary>The review of an unchanged tree is carried into the attempt that follows it.</summary>
    [Fact]
    public void The_findings_of_an_unchanged_tree_reach_the_attempt_that_continues()
    {
        Ui.Run(async () =>
        {
            TreeNeverMoves();
            var prompts = Script(ThroughTheReview(
                "Reviewed.\n\n```json\n{\"goalMet\":false,\"findings\":[" +
                "{\"severity\":\"warning\",\"title\":\"the leftover cast\"}]}\n```",
                "Implemented it"));

            using var vm = NewTile();
            await RunToSummary(vm);

            Assert.Contains(prompts,
                prompt => prompt.Contains("Fix these findings from the previous review")
                          && prompt.Contains("the leftover cast"));
        });
    }

    // ── Scope: @ paths and refs ─────────────────────────

    [Fact]
    public void Every_tree_read_of_a_goal_typed_with_at_paths_carries_its_scope()
    {
        Ui.Run(async () =>
        {
            // The scope set by Submit, threaded through every phase's read.
            var observed = new List<IReadOnlyList<string>?>();
            TreeNeverMoves();
            WorktreeReader.ReadObserved = scope => observed.Add(scope);
            AnswerWith(ThroughTheReview("VERDICT: PASS"));

            using var vm = NewTile();
            WriteFile("src/Agents/X.cs");
            await RunToSummary(vm, "napraw @src/Agents/X.cs");

            Assert.NotEmpty(observed);
            Assert.All(observed, scope => Assert.Equal(["src/Agents/X.cs"], scope));
        });
    }

    [Fact]
    public void A_narrowing_typed_beside_re_review_goes_to_that_review_and_not_into_the_goal()
    {
        Ui.Run(async () =>
        {
            var observed = new List<IReadOnlyList<string>?>();
            TreeNeverMoves();
            WorktreeReader.ReadObserved = scope => observed.Add(scope);
            var prompts = Script("Make the totals include discounts.", "VERDICT: PASS");

            using var vm = NewTile();

            // Detect & run with an @ path: the goal that comes out of it carries that scope.
            WriteFile("src/Resumable.cs");
            vm.InputText = "make the totals include discounts @src/Resumable.cs";
            await vm.DetectGoalAndRunCommand.ExecuteAsync(null);
            prompts.Clear();

            // Typed beside the button, and consumed by this review alone.
            vm.InputText = "tylko samewnegoryzacja metody";
            await vm.ReReviewCommand.ExecuteAsync(null);

            Assert.Contains("The user narrowed this review", prompts[0]);
            Assert.Equal("", vm.InputText);

            // One more look with nothing typed: the goal's own scope applies.
            await vm.ReReviewCommand.ExecuteAsync(null);
            Assert.DoesNotContain("The user narrowed this review", prompts[1]);
            Assert.Contains("src/Resumable.cs", observed[^1]!);
        });
    }

    /// <summary>
    /// A named range keeps its base end on every read and its head end nowhere the run writes (the
    /// plan included); only detection reads the range as typed.
    /// </summary>
    [Fact]
    public void A_named_range_is_read_to_the_working_tree_wherever_the_run_writes()
    {
        Ui.Run(async () =>
        {
            var bases = new List<GoalReadBase?>();
            GoalScopeRef.Factory = (token, _) => Task.FromResult<GoalReadBase?>(
                token == "master..HEAD" ? new GoalReadBase("master", "HEAD") : null);
            WorktreeReader.BaseObserved = read => bases.Add(read);
            AnswerWith(ThroughTheReview(CleanReview));

            using var vm = NewTile();
            await RunToSummary(vm, "napraw to @master..HEAD");

            Assert.NotEmpty(bases);
            Assert.All(bases, read => Assert.Equal("master", read?.Base));
            Assert.All(bases, read => Assert.Null(read?.Head));
        });
    }

    [Fact]
    public void A_detection_keeps_the_head_end_the_user_named()
    {
        Ui.Run(async () =>
        {
            var bases = new List<GoalReadBase?>();
            GoalScopeRef.Factory = (token, _) => Task.FromResult<GoalReadBase?>(
                token == "master..HEAD" ? new GoalReadBase("master", "HEAD") : null);
            WorktreeReader.BaseObserved = read => bases.Add(read);
            AnswerWith("Finish the cart");

            using var vm = NewTile();
            vm.InputText = "@master..HEAD";
            await vm.DetectGoalCommand.ExecuteAsync(null);

            Assert.Contains(bases, read => read is { Base: "master", Head: "HEAD" });
        });
    }

    /// <summary>A detection reads the ends its own composer names, never the ones of the goal it
    /// replaces.</summary>
    [Fact]
    public void A_detection_does_not_read_through_the_ref_of_the_goal_it_replaces()
    {
        Ui.Run(async () =>
        {
            var bases = new List<GoalReadBase?>();
            GoalScopeRef.Factory = (token, _) => Task.FromResult<GoalReadBase?>(
                token == "HEAD~3" ? new GoalReadBase("HEAD~3", null) : null);
            AnswerWith("Which files?", NoMoreQuestions);

            using var vm = NewTile();
            await Send(vm, "napraw to @HEAD~3");

            // Watched only from here: what the goal being replaced read is not the question.
            WorktreeReader.BaseObserved = read => bases.Add(read);

            vm.InputText = "";
            await vm.DetectGoalCommand.ExecuteAsync(null);

            Assert.NotEmpty(bases);
            Assert.All(bases, Assert.Null);
        });
    }

    /// <summary>A pause while git resolves an @ ref stops the run cleanly, on both the send and the
    /// detect path.</summary>
    [Fact]
    public void A_pause_while_a_ref_is_resolving_is_not_an_unexpected_error()
    {
        Ui.Run(async () =>
        {
            GoalScopeRef.Factory = (_, _) => throw new OperationCanceledException();
            AnswerWith("Which files?", NoMoreQuestions);

            using var vm = NewTile();

            await Send(vm, "napraw to @master..HEAD");
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("Unexpected error"));

            vm.InputText = "@master..HEAD";
            await vm.DetectGoalCommand.ExecuteAsync(null);
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("Unexpected error"));
        });
    }

    /// <summary>That same pause keeps the paths, which were known before git was asked anything.
    /// </summary>
    [Fact]
    public void A_pause_while_a_ref_is_resolving_keeps_the_paths_the_composer_already_named()
    {
        Ui.Run(async () =>
        {
            GoalScopeRef.Factory = (_, _) => throw new OperationCanceledException();
            AnswerWith("Which files?", NoMoreQuestions);

            using var vm = NewTile();
            WriteFile("src/Cart.cs");

            await Send(vm, "popraw koszyk @src/Cart.cs @master..HEAD");

            Assert.Equal(["src/Cart.cs"], Saved(vm).ScopePaths);
        });
    }

    // ── Attempts that change nothing ────────────────────

    /// <summary>An unchanged tree whose review found something new gets the next attempt rather than a
    /// button; the second unchanged attempt then stops as a dead end.</summary>
    [Fact]
    public void Findings_the_implementation_never_saw_buy_another_attempt_rather_than_a_button()
    {
        Ui.Run(async () =>
        {
            TreeNeverMoves();
            var prompts = Script("Which files?", NoMoreQuestions, "The plan",
                "Everything the plan asks for is already here.", WarningReview);

            using var vm = NewTile();
            await RunToSummary(vm);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);

            // Clarify, no-more, plan, implement, review — then the second attempt and its review.
            Assert.Equal(7, prompts.Count);
            Assert.Contains(vm.Messages, m => m.Text.Contains("Re-implementing with those findings (attempt 2)"));
            Assert.Contains("stale scope", prompts[5]);
            Assert.Contains(vm.Messages, m => m.Text.Contains("Stopped after 2 attempts: the agent changed no files"));
        });
    }

    [Fact]
    public void An_unchanged_tree_gets_its_verdict_and_the_stop_says_so_when_the_review_passes()
    {
        Ui.Run(async () =>
        {
            // The empty worktree goes to the reviewer once; met, and the stop is the one a finished goal
            // earns.
            TreeNeverMoves();
            AnswerWith(ThroughTheReview("VERDICT: PASS"));

            using var vm = NewTile();
            await RunToSummary(vm);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Text.StartsWith("Goal completed after 1 attempt"));
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("changed no files"));
        });
    }

    [Fact]
    public void A_refused_attempt_skips_the_verdict_review_because_it_would_answer_the_summary()
    {
        Ui.Run(async () =>
        {
            // An empty worktree because every tool call was refused needs the permission mode, not a
            // review of work nobody was allowed to do.
            TreeNeverMoves();
            var prompts = Script("Which files?", NoMoreQuestions, "The plan",
                ((AiOutput)"VERDICT: PASS") with { PermissionDenials = 3 }, "VERDICT: PASS");

            using var vm = NewTile();
            await RunToSummary(vm);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Equal(4, prompts.Count);
            Assert.Contains(vm.Messages, m => m.Text.Contains("refused permission"));
            Assert.DoesNotContain(vm.Messages, m => m.Text.StartsWith("Goal completed"));
        });
    }

    [Fact]
    public void A_verdict_review_refused_its_own_tool_calls_does_not_wear_the_permission_sentence()
    {
        Ui.Run(async () =>
        {
            // The permission sentence is about the implementation's refusals, captured before the
            // review runs and rewrites the field with its own.
            TreeNeverMoves();
            var prompts = Script(
                [.. UpToTheReview.Select(a => (AiOutput)a),
                 ((AiOutput)"VERDICT: FAIL") with { PermissionDenials = 3 }, "VERDICT: FAIL"]);

            using var vm = NewTile();
            await RunToSummary(vm);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Equal(5, prompts.Count);
            Assert.Contains(vm.Messages, m => m.Text.Contains("changed no files"));
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("refused permission"));
            Assert.DoesNotContain(vm.Messages, m => m.Text.StartsWith("Goal completed"));
        });
    }

    [Fact]
    public void Two_reviews_that_find_the_same_things_stop_the_run()
    {
        Ui.Run(async () =>
        {
            const string sameEveryTime =
                "```json\n{\"goalMet\":false,\"findings\":[" +
                "{\"severity\":\"error\",\"file\":\"src/X.cs\",\"title\":\"Still wrong\"}]}\n```";

            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                return Task.FromResult<AiOutput>(asked switch
                {
                    1 => "Which files?",
                    2 => NoMoreQuestions,
                    3 => "The plan",
                    _ => asked % 2 == 1 ? sameEveryTime : "Implemented it",   // 4,6 implement; 5,7 review
                });
            };

            using var vm = NewTile();
            await RunToSummary(vm);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Text.Contains("reached the same conclusion"));
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("attempt 4"));

            // The no-progress stop is not a budget, so there is nothing for Continue to raise.
            Assert.False(vm.CanContinue);
        });
    }

    [Fact]
    public void A_structured_review_is_shown_as_findings_and_counted_in_the_badges()
    {
        Ui.Run(async () =>
        {
            AnswerWith(ThroughTheReview(
                "Looks mostly fine.\n\n```json\n{\"goalMet\":true,\"findings\":[" +
                "{\"severity\":\"suggestion\",\"file\":\"src/X.cs\",\"line\":4," +
                "\"title\":\"Rename this\",\"detail\":\"x is not a name.\"}]}\n```"));

            using var vm = NewTile();
            await RunToSummary(vm);

            // A suggestion never blocks, so the goal is met on the first attempt.
            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Text.StartsWith("Goal completed"));

            // Rendered as a finding, not reprinted with the prose around the block.
            Assert.Contains(vm.Messages, m => m.Findings.Any(
                f => f.Severity == GoalSeverity.Suggestion && f.File == "src/X.cs" && f.Line == 4
                     && f.Title == "Rename this"));
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("Looks mostly fine"));

            Assert.Equal(["1S"], vm.Badges.Select(b => b.Text));
        });
    }

    // ── Questions ───────────────────────────────────────

    [Fact]
    public void The_rounds_already_spent_survive_a_restart()
    {
        Ui.Run(async () =>
        {
            AnswerWith("And another thing?");

            var first = NewTile();
            await Send(first, "a goal");
            await Send(first, "answered");

            // Otherwise closing the tile renews the budget, and a tool that keeps asking keeps asking.
            using var second = Reopen(first);
            await Send(second, "answered again");
            await Send(second, "and again");

            Assert.Equal(GoalPhase.Plan, second.CurrentPhase);
        });
    }

    [Fact]
    public void A_plan_the_tile_started_by_itself_comes_back_paused_when_it_is_interrupted()
    {
        Ui.Run(async () =>
        {
            var first = NewTile();

            // Clarify moves to Plan by itself, leaving a note of the tile's own last; the plan run is
            // then cut off.
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                if (asked == 3) first.PauseCommand.Execute(null);
                return Task.FromResult<AiOutput>(asked switch { 1 => "Which files?", 2 => NoMoreQuestions, _ => "The plan" });
            };

            await Send(first, "a goal");
            await Send(first, "all of it");

            using var second = Reopen(first);

            Assert.Equal(GoalPhase.Plan, second.CurrentPhase);
            Assert.True(second.IsPaused);
            Assert.Contains("Resume", second.PhaseLabel);
        });
    }

    [Fact]
    public void The_questions_go_into_the_history_the_next_round_and_the_plan_read()
    {
        Ui.Run(async () =>
        {
            var prompts = Script(
                "```json\n{\"questions\":[{\"question\":\"Which config file holds the port?\"}]}\n```",
                NoMoreQuestions);

            using var vm = NewTile();
            await Send(vm, "a goal");
            await Send(vm, "1. appsettings.json");

            // The next round is handed the answer and the question it answers, and so is the plan.
            Assert.Contains("Which config file holds the port?", prompts[1]);
            Assert.Contains("appsettings.json", prompts[1]);
            Assert.Contains("Which config file holds the port?", prompts[^1]);
        });
    }

    [Fact]
    public void Sending_back_only_the_numbering_does_not_spend_a_round()
    {
        Ui.Run(async () =>
        {
            var prompts = Script(
                "```json\n{\"questions\":[{\"question\":\"Which file?\"},{\"question\":\"Sync?\"}]}\n```");

            using var vm = NewTile();
            await Send(vm, "a goal");

            // The questions have a box each and the composer is not up.
            Assert.Equal(2, vm.Questions.Count);
            Assert.True(vm.ShowQuestions);
            Assert.False(vm.ShowComposer);
            Assert.Equal("", vm.InputText);

            // Sending with every box empty spends no round, and the questions stay.
            await vm.SendAnswersCommand.ExecuteAsync(null);

            Assert.Single(prompts);
            Assert.Contains(vm.Messages, m => m.Text.Contains("Answer at least one"));
            Assert.DoesNotContain(vm.Messages, m => m.Role == GoalMessageRole.User && m.Text.StartsWith("1."));
            Assert.Equal(2, vm.Questions.Count);
        });
    }

    [Fact]
    public void An_answer_that_is_only_the_numbering_does_not_spend_the_pause_either()
    {
        Ui.Run(async () =>
        {
            // Nothing at all after the questions, which pauses the tile in Clarify.
            Script("```json\n{\"questions\":[{\"question\":\"Which file?\"}]}\n```", "   ");

            using var vm = NewTile();
            await Send(vm, "a goal");
            await Send(vm, "an answer");

            Assert.True(vm.IsPaused);

            // Enter on the prefilled numbering starts nothing, so it must not clear the pause.
            await Send(vm, "1. ");

            Assert.True(vm.IsPaused);
            Assert.False(vm.IsRunning);
            Assert.Contains("Resume", vm.PhaseLabel);
            Assert.Equal("1.", vm.InputText);
        });
    }

    [Fact]
    public void What_the_tool_said_on_its_way_past_is_kept()
    {
        Ui.Run(async () =>
        {
            AnswerWith(
                "The goal is clear; I am assuming the API stays as it is.\n\n" +
                "```json\n{\"needsClarification\":false}\n```",
                "The plan");

            using var vm = NewTile();
            await Send(vm, "a goal");

            // What a round assumes is the last chance to disagree before a plan is written against it.
            Assert.Contains(vm.Messages, m => m.Text.Contains("assuming the API stays as it is"));
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("needsClarification"));
            Assert.Equal(GoalPhase.Plan, vm.CurrentPhase);
        });
    }

    [Fact]
    public void A_clarify_block_broken_past_repairing_is_asked_for_again_rather_than_shown_raw()
    {
        Ui.Run(async () =>
        {
            // A quote, a comma and then something shaped like the next pair: past what JsonRepair mends.
            var broken = "{\"needsClarification\":true,\"questions\":[{\"question\":" +
                         "\"Plan mówi \"a\", \"b\" — gdzie pytamy?\"}]}";
            var prompts = Script(broken,
                "```json\n{\"needsClarification\":true,\"questions\":[{\"question\":\"Gdzie pytamy?\"}]}\n```");

            using var vm = NewTile();
            await Send(vm, "a goal");

            // The round and the re-send of its own answer alone.
            Assert.Equal(2, prompts.Count);
            Assert.Contains("exactly the same JSON", prompts[1]);
            Assert.Contains(broken, prompts[1]);

            Assert.Contains(vm.Questions, q => q.Question.Contains("Gdzie pytamy?"));
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("needsClarification"));
        });
    }

    [Fact]
    public void A_round_that_asks_nothing_goes_straight_to_the_plan()
    {
        Ui.Run(async () =>
        {
            var prompts = Script("```json\n{\"needsClarification\":true,\"questions\":[]}\n```", "The plan");

            using var vm = NewTile();
            await Send(vm, "a goal");

            Assert.Equal(GoalPhase.Plan, vm.CurrentPhase);
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("needsClarification"));
            Assert.DoesNotContain(prompts[^1], "needsClarification");
        });
    }

    /// <summary>
    /// A kill while the tool holds the answers leaves a state that reads as interrupted, so it comes
    /// back offering Resume; the opposite reading is pinned in GoalResumeTests.
    /// </summary>
    [Fact]
    public void Answering_a_round_leaves_a_state_that_reads_as_interrupted_while_the_tool_works()
    {
        Ui.Run(async () =>
        {
            GoalTileViewModel? tile = null;
            List<GoalMessage>? midRun = null;
            var asked = 0;

            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                // The transcript while the tool holds the answers and has not replied.
                if (++asked == 2)
                    midRun = [..tile!.Messages];

                return Task.FromResult<AiOutput>(asked == 1
                    ? """{"questions":[{"question":"Which file?"}]}"""
                    : NoMoreQuestions);
            };

            using var vm = NewTile();
            tile = vm;

            await Send(vm, "a goal");
            vm.Questions[0].Answer = "appsettings.json";
            await vm.SendAnswersCommand.ExecuteAsync(null);

            Assert.NotNull(midRun);
            var killed = new GoalTileState { CurrentPhase = GoalPhase.Clarify, Messages = midRun! };
            Assert.EndsWith("appsettings.json", killed.Messages[^1].Text);
            Assert.True(GoalWorkflowEngine.WasInterrupted(killed));
        });
    }

    [Fact]
    public void Answers_go_back_numbered_and_the_questions_join_the_transcript_with_them()
    {
        Ui.Run(async () =>
        {
            var prompts = Script(
                """{"questions":[{"question":"Which file?"},{"question":"Sync or async?"}]}""",
                NoMoreQuestions);

            using var vm = NewTile();
            await Send(vm, "a goal");

            vm.Questions[0].Answer = "appsettings.json";
            vm.Questions[1].Answer = "async";
            await vm.SendAnswersCommand.ExecuteAsync(null);

            Assert.Contains("1. appsettings.json", prompts[1]);
            Assert.Contains("2. async", prompts[1]);

            // One message carrying the questions and the answer under each.
            var round = Assert.Single(vm.Messages, m => m.HasQuestions);
            Assert.Equal(["Which file?", "Sync or async?"], round.Questions.Select(q => q.Question));
            Assert.Equal(["appsettings.json", "async"], round.Questions.Select(q => q.Answer));

            // Its text is the whole round, for the clipboard and for a build that cannot draw the rows.
            Assert.Contains("Sync or async?", round.Text);
            Assert.Contains("async", round.Text);

            // No second copy of the answers as a message of the user's own.
            Assert.DoesNotContain(vm.Messages, m => m.Role == GoalMessageRole.User && m.Text.Contains("2. async"));

            Assert.Empty(vm.Questions);
            Assert.False(vm.ShowQuestions);
        });
    }

    [Fact]
    public void An_unanswered_question_is_left_out_rather_than_sent_empty()
    {
        Ui.Run(async () =>
        {
            var prompts = Script(
                """{"questions":[{"question":"Which file?"},{"question":"Sync or async?"}]}""",
                NoMoreQuestions);

            using var vm = NewTile();
            await Send(vm, "a goal");

            vm.Questions[1].Answer = "async";
            await vm.SendAnswersCommand.ExecuteAsync(null);

            Assert.Contains("2. async", prompts[1]);
            Assert.DoesNotContain("1. \n", prompts[1]);
        });
    }

    [Fact]
    public void Questions_come_back_with_the_tile_and_do_not_look_like_an_interrupted_run()
    {
        Ui.Run(async () =>
        {
            AnswerWith("""
                {"questions":[{"question":"Which file?","why":"Two candidates.",
                  "options":["appsettings.json","launchSettings.json"]}]}
                """);

            var first = NewTile();
            await Send(first, "a goal");
            Assert.Single(first.Questions);

            using var second = Reopen(first);

            var question = Assert.Single(second.Questions);
            Assert.Equal("Which file?", question.Question);
            Assert.Equal("Two candidates.", question.Why);
            Assert.Equal(2, question.Options.Count);

            // A tile waiting on the user is not a run that was cut off.
            Assert.False(second.IsPaused);
        });
    }

    [Fact]
    public void A_fresh_round_of_questions_replaces_the_one_on_screen()
    {
        Ui.Run(async () =>
        {
            Script("""{"questions":[{"question":"Which file?"}]}""",
                """{"questions":[{"question":"Which port?"},{"question":"Which host?"}]}""");

            using var vm = NewTile();
            await Send(vm, "a goal");

            vm.Questions[0].Answer = "appsettings.json";
            await vm.SendAnswersCommand.ExecuteAsync(null);

            Assert.Equal(2, vm.Questions.Count);
            Assert.Equal("Which port?", vm.Questions[0].Question);
            Assert.All(vm.Questions, q => Assert.Equal("", q.Answer));
        });
    }

    [Fact]
    public void Prose_questions_keep_the_composer_because_there_is_no_panel_to_build()
    {
        Ui.Run(async () =>
        {
            AnswerWith("Which file holds the port, and should it be async?");

            using var vm = NewTile();
            await Send(vm, "a goal");

            Assert.Empty(vm.Questions);
            Assert.True(vm.ShowComposer);
            Assert.Contains(vm.Messages, m => m.Text.Contains("holds the port"));
        });
    }

    /// <summary>After the round budget the tile plans with what it has, and the questions leave the
    /// screen so the approval panel can stand up.</summary>
    [Fact]
    public void Giving_up_on_questions_takes_them_off_the_screen_as_well()
    {
        Ui.Run(async () =>
        {
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) => Task.FromResult<AiOutput>(
                ++asked <= GoalWorkflowEngine.MaxClarifyRounds
                    ? """{"questions":[{"question":"Which file?"}]}"""
                    : "The plan");

            using var vm = NewTile();
            await Send(vm, "a goal");

            for (var round = 0; round < GoalWorkflowEngine.MaxClarifyRounds; round++)
            {
                Assert.Single(vm.Questions);
                vm.Questions[0].Answer = "appsettings.json";
                await vm.SendAnswersCommand.ExecuteAsync(null);
            }

            Assert.Equal(GoalPhase.Plan, vm.CurrentPhase);
            Assert.Empty(vm.Questions);
            Assert.False(vm.ShowQuestions);
            Assert.True(vm.ShowApproval);
            Assert.Contains(vm.Messages, m => m.Text.Contains("rounds of questions"));
        });
    }

    [Fact]
    public void The_completion_criteria_survive_a_restart()
    {
        Ui.Run(async () =>
        {
            AnswerWith("Which files?");

            var first = NewTile();
            await Send(first, "a goal");

            first.Criteria.MaxIterations = 9;
            first.Criteria.MaxWarnings = 2;
            first.Criteria.RequireGoalMet = false;
            first.Criteria.RequireTestsPass = false;

            using var second = Reopen(first);

            Assert.Equal(9, second.Criteria.MaxIterations);
            Assert.Equal(2, second.Criteria.MaxWarnings);
            Assert.False(second.Criteria.RequireGoalMet);

            // A switch turned off stays off, and the one beside it stays on.
            Assert.False(second.Criteria.RequireTestsPass);
            Assert.True(second.Criteria.RequireBuild);
        });
    }

    // ── The buttons under the composer ──────────────────

    /// <summary>
    /// What the composer's buttons say and offer, by phase, by whether anything is uncommitted, and by
    /// what is in the box.
    /// </summary>
    /// <remarks>Every row first holds a pointer at a commit, so a property that only ever widens would
    /// fail the rows that must close again.</remarks>
    [Theory]
    // phase, uncommitted, input → primary label, detect, set goal, set goal & run, typed goal, run label, run
    [InlineData(GoalPhase.Goal, false, "", "Set goal", false, true, false, false, "Detect & run", false)]
    [InlineData(GoalPhase.Goal, true, "", "Detect goal", true, true, false, false, "Detect & run", true)]
    // Typed words beside a detection are a scope narrowing it, so the detect entries stay open.
    [InlineData(GoalPhase.Goal, true, "a typed goal", "Set goal", true, true, true, true, "Set goal & run", true)]
    // Mid-conversation the box answers; the entries that name a goal are down.
    [InlineData(GoalPhase.Clarify, true, "a typed goal", "Send answer", false, false, false, true, "Set goal & run", false)]
    [InlineData(GoalPhase.Clarify, true, "", "Send answer", false, false, false, false, "Detect & run", false)]
    [InlineData(GoalPhase.Plan, true, "a typed goal", "Send", false, false, false, true, "Set goal & run", false)]
    [InlineData(GoalPhase.Summary, true, "a typed goal", "Set goal", true, true, true, true, "Set goal & run", true)]
    // Pointers alone are not a typed goal: the goal is still to be read from the changes.
    [InlineData(GoalPhase.Goal, false, "@src/Auth.cs @HEAD~1", "Detect goal", true, true, false, false, "Detect & run", true)]
    [InlineData(GoalPhase.Goal, false, "make logging stateless @src/Auth.cs", "Set goal", true, true, true, true, "Set goal & run", true)]
    // A named commit is something to read a goal from, and git status cannot see it.
    [InlineData(GoalPhase.Goal, false, "@HEAD~1", "Detect goal", true, true, false, false, "Detect & run", true)]
    public void The_buttons_under_the_composer_follow_the_phase_the_tree_and_the_box(
        GoalPhase phase, bool uncommitted, string input,
        string primary, bool detect, bool setGoal, bool setGoalAndRun, bool typed, string run, bool canRun)
    {
        Ui.Run(() =>
        {
            using var vm = NewTile();
            vm.HasUncommittedChanges = uncommitted;
            vm.CurrentPhase = phase;
            vm.InputText = "@HEAD~1";
            vm.InputText = input;

            Assert.Equal(primary, vm.PrimaryActionLabel);
            Assert.Equal(detect, vm.CanDetectGoal);
            Assert.Equal(setGoal, vm.CanSetGoal);
            Assert.Equal(setGoalAndRun, vm.CanSetGoalAndRun);
            Assert.Equal(typed, vm.HasTypedGoal);
            Assert.Equal(run, vm.RunActionLabel);
            Assert.Equal(canRun, vm.CanRun);
        });
    }

    /// <summary>A box holding nothing but a live path on a clean tree has nothing to detect from, and
    /// Submit refuses to adopt the path as the goal — keeping what was typed.</summary>
    [Fact]
    public void A_pointer_alone_is_refused_rather_than_adopted_as_the_goal()
    {
        Ui.Run(async () =>
        {
            WriteFile("src/Auth.cs", "class Auth;");

            using var vm = NewTile();
            Assert.False(vm.HasUncommittedChanges);

            vm.InputText = "@src/Auth.cs";

            Assert.False(vm.HasTypedGoal);
            Assert.False(vm.CanDetectGoal, "a live path on a clean tree is nothing to read a goal from");
            Assert.Equal("Set goal", vm.PrimaryActionLabel);

            await vm.PrimaryActionCommand.ExecuteAsync(null);

            Assert.Equal(GoalPhase.Goal, vm.CurrentPhase);
            Assert.DoesNotContain(vm.Messages, m => m.Role == GoalMessageRole.User);
            Assert.Contains(vm.Messages, m => m.Text.Contains("says where to look, not what to do"));
            Assert.Equal("@src/Auth.cs", vm.InputText);
        });
    }

    // ── Pausing and resuming ────────────────────────────

    [Fact]
    public void A_tile_closed_at_the_gate_comes_back_owing_the_implementation_and_not_the_review()
    {
        // The state is flushed before the loop moves the lap on, so Review lands on disk; read as "the
        // review is owed", Resume would run the reviewer a second time over an untouched tree.
        var engine = new GoalWorkflowEngine();
        engine.StartNewGoal("make it work");
        engine.RecordProposedPlan("the plan");
        Assert.True(engine.ApprovePlan());
        engine.IterationCount = 1;
        engine.CurrentPhase = GoalPhase.Review;
        engine.IsPaused = true;
        engine.PausedAtReviewGate = true;

        var finding = new GoalFinding { Severity = GoalSeverity.Warning, Title = "nit" };
        engine.LastReview = new GoalReviewResult { WasStructured = true, Findings = [finding] };
        engine.LastReviewFeedback = "the lap before's feedback";

        var path = Path.Combine(Dir, "closed-at-the-gate.json");
        new GoalStatePersistence().Save(path, engine.ToState(
            [new GoalMessage { Role = GoalMessageRole.Assistant, Text = "reviewed",
                Phase = GoalPhase.Review, Findings = [finding] }],
            "", ""));

        Ui.Run(() =>
        {
            using var vm = Open(path);

            Assert.True(vm.IsPaused);
            Assert.Equal(GoalPhase.Implement, vm.CurrentPhase);
            Assert.False(GoalTilePolicy.ResumesAtReview(vm.CurrentPhase));
        });

        // The implementation gets this review's feedback on Resume, not the lap before's.
        var feedback = new GoalStatePersistence().Load(path)?.LastReviewFeedback;
        Assert.NotNull(feedback);
        Assert.Contains("nit", feedback);
    }

    [Fact]
    public void A_pause_gate_over_a_review_with_nothing_to_pick_comes_back_the_same_way()
    {
        // A pause gate stands over every review, prose included; that one must not leave Resume owing
        // the review either.
        var engine = new GoalWorkflowEngine();
        engine.StartNewGoal("make it work");
        engine.RecordProposedPlan("the plan");
        Assert.True(engine.ApprovePlan());
        engine.IterationCount = 1;
        engine.CurrentPhase = GoalPhase.Review;
        engine.IsPaused = true;
        engine.PausedAtReviewGate = true;
        engine.ReviewGateMode = GoalReviewGateMode.Manual;
        engine.LastReview = new GoalReviewResult { RawText = "it looks unfinished to me" };

        var path = Path.Combine(Dir, "closed-at-an-empty-gate.json");
        new GoalStatePersistence().Save(path, engine.ToState(
            [new GoalMessage { Role = GoalMessageRole.Assistant, Text = "reviewed",
                Phase = GoalPhase.Review }],
            "", ""));

        Ui.Run(() =>
        {
            using var vm = Open(path);

            Assert.True(vm.IsPaused);
            Assert.True(vm.ShowReviewGate);
            Assert.Equal(GoalPhase.Implement, vm.CurrentPhase);
            Assert.False(GoalTilePolicy.ResumesAtReview(vm.CurrentPhase));
        });
    }

    [Fact]
    public void The_seconds_field_is_redrawn_when_it_is_left_after_a_failed_conversion()
    {
        // The gate's wait lives on the tile, not on the criteria editor, so the tile has to be told to
        // redraw it too.
        Ui.Run(() =>
        {
            using var vm = NewTile();
            vm.GateSeconds = 42;

            var notified = 0;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.GateSeconds)) notified++;
            };

            vm.RefreshNumberFields();

            Assert.True(notified > 0);
            Assert.Equal(42, vm.GateSeconds);
        });
    }

    /// <summary>How long a test lets the gate take to come up: the loop runs on stubs, so this is only
    /// a guard against a hang.</summary>
    private static readonly TimeSpan GateDeadline = TimeSpan.FromSeconds(10);

    [Fact]
    public void Pause_at_the_gate_stops_the_sentence_counting_down_as_well_as_the_clock()
    {
        Ui.Run(async () =>
        {
            AnswerWith(ThroughTheReview(WarningReview));

            using var vm = NewTile();
            await Send(vm, "a goal");
            await Send(vm, "all of it");

            // Long enough that nothing expires while the test presses the button.
            vm.GateSeconds = GoalReviewGatePolicy.MaxSeconds;

            vm.InputText = "ok";
            var run = vm.SubmitCommand.ExecuteAsync(null);

            await WhenTrue(vm, () => vm.GateOffersTheClock, GateDeadline);
            Assert.Contains("continuing in", vm.ReviewGateLine);

            vm.PauseAtGateCommand.Execute(null);
            await run;

            Assert.True(vm.GateOffersResume);
            Assert.DoesNotContain("continuing in", vm.ReviewGateLine);
            Assert.Contains("paused", vm.ReviewGateLine);
        });
    }

    [Fact]
    public void Unticking_the_last_error_at_the_gate_stops_the_clock_drops_it_from_the_feedback_and_finishes_the_goal()
    {
        // Three halves of one decision: the clock stops under the tick, the next implementation is not
        // handed the finding, and a review whose only error was dismissed is met on Resume.
        Ui.Run(async () =>
        {
            AnswerWith(ThroughTheReview(ErrorReview, "Summary"));

            using var vm = NewTile();
            vm.Criteria.RequireGoalMet = false;

            await Send(vm, "a goal");
            await Send(vm, "all of it");
            vm.GateSeconds = GoalReviewGatePolicy.MaxSeconds;

            vm.InputText = "ok";
            var run = vm.SubmitCommand.ExecuteAsync(null);

            await WhenTrue(vm, () => vm.GateOffersTheClock, GateDeadline);

            LastFindings(vm).Single(f => f.Title == "null deref").Fix = false;
            await run;

            Assert.True(vm.GateOffersResume);
            var paused = new GoalStatePersistence().Load(vm.FilePath);
            Assert.DoesNotContain("null deref", paused?.LastReviewFeedback ?? "");

            await vm.ResumeCommand.ExecuteAsync(null);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Equal(GoalStopReason.Met, Saved(vm).LastStopReason);
        });
    }

    // ── Picking findings after a review ─────────────────

    /// <summary>A review on its own offers the gate's choice before Continue, without becoming the gate.
    /// </summary>
    [Fact]
    public void A_review_on_its_own_lets_the_findings_be_narrowed_before_continue()
    {
        Ui.Run(async () =>
        {
            var prompts = Script("Finish the pairing flow.", TwoErrorsReview, "Implemented it", "VERDICT: PASS");
            WithBaseline();

            using var vm = NewTile();
            vm.Criteria.RequireGoalMet = false;
            await vm.ReviewCommand.ExecuteAsync(null);

            var findings = LastFindings(vm);
            Assert.All(findings, f => Assert.True(f.CanPick));
            Assert.False(vm.ShowReviewGate);
            Assert.False(vm.IsPaused);
            Assert.True(vm.CanContinue);

            findings.Single(f => f.Title == "null deref").Fix = false;
            await vm.ContinueRunCommand.ExecuteAsync(null);

            var implement = prompts.Single(p => p.Contains("Fix these findings from the previous review"));
            Assert.Contains("race on save", implement);
            Assert.DoesNotContain("null deref", implement);

            // The attempt closed the choice: it was acted on.
            Assert.All(findings, f => Assert.False(f.CanPick));
        });
    }

    /// <summary>Leaving everything the review found takes Continue away and ticking one back returns
    /// it — whether or not the reviewer's own verdict still counts.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // the default criteria: the verdict still says no, and Continue cannot fix a verdict
    public void Leaving_every_finding_of_a_review_takes_continue_away(bool requireGoalMet)
    {
        Ui.Run(async () =>
        {
            AnswerWith("Finish the pairing flow.", TwoErrorsReview);
            WithBaseline();

            using var vm = NewTile();
            vm.Criteria.RequireGoalMet = requireGoalMet;
            await vm.ReviewCommand.ExecuteAsync(null);
            Assert.True(vm.CanContinue);

            var findings = LastFindings(vm);
            foreach (var finding in findings) finding.Fix = false;
            Assert.False(vm.CanContinue);

            findings[0].Fix = true;
            Assert.True(vm.CanContinue);
        });
    }

    /// <summary>A suggestion cannot be ticked away and never refuses the goal, so leaving every error
    /// beside one still leaves nothing for Continue.</summary>
    [Fact]
    public void Leaving_every_error_takes_continue_away_even_beside_a_suggestion()
    {
        Ui.Run(async () =>
        {
            AnswerWith("Finish the pairing flow.",
                "```json\n{\"goalMet\":false,\"findings\":[" +
                "{\"severity\":\"error\",\"title\":\"null deref\",\"file\":\"a.cs\"}," +
                "{\"severity\":\"suggestion\",\"title\":\"rename it\",\"file\":\"b.cs\"}]}\n```");
            WithBaseline();

            using var vm = NewTile();
            await vm.ReviewCommand.ExecuteAsync(null);
            Assert.True(vm.CanContinue);

            LastFindings(vm).Single(f => f.Title == "null deref").Fix = false;
            Assert.False(vm.CanContinue);
        });
    }

    /// <summary>A clean review offers its suggestions unticked; ticking one is asking Continue to fix it.
    /// </summary>
    [Fact]
    public void Ticking_a_suggestion_after_a_review_asks_continue_to_fix_it()
    {
        Ui.Run(async () =>
        {
            const string onlyANit =
                "```json\n{\"goalMet\":true,\"findings\":[{\"severity\":\"suggestion\"," +
                "\"title\":\"rename x\",\"file\":\"a.cs\"}]}\n```";
            var prompts = Script("Finish the pairing flow.", onlyANit, "Implemented it", "VERDICT: PASS");
            WithBaseline();

            using var vm = NewTile();
            await vm.ReviewCommand.ExecuteAsync(null);

            var nit = LastFindings(vm).Single();
            Assert.True(nit.CanPick);
            Assert.False(nit.Fix);
            Assert.False(vm.CanContinue);

            nit.Fix = true;
            Assert.True(vm.CanContinue);

            await vm.ContinueRunCommand.ExecuteAsync(null);
            Assert.Contains(prompts, p => p.Contains("Fix these findings from the previous review")
                                          && p.Contains("rename x"));
        });
    }

    /// <summary>A tile closed over a reviewed summary comes back still offering the ticks, as they were
    /// left — including with every one left, which turned the summary Met.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reopening_a_reviewed_tile_offers_the_ticks_again(bool leaveEveryFinding)
    {
        Ui.Run(async () =>
        {
            AnswerWith("Finish the pairing flow.", TwoErrorsReview);
            WithBaseline();

            var first = NewTile();
            first.Criteria.RequireGoalMet = false;
            await first.ReviewCommand.ExecuteAsync(null);
            foreach (var finding in LastFindings(first))
                if (leaveEveryFinding || finding.Title == "null deref") finding.Fix = false;
            Assert.Equal(!leaveEveryFinding, first.CanContinue);

            using var second = Reopen(first);

            var findings = LastFindings(second);
            Assert.All(findings, f => Assert.True(f.CanPick));
            Assert.False(findings.Single(f => f.Title == "null deref").Fix);
            Assert.False(second.ShowReviewGate);
            Assert.Equal(!leaveEveryFinding, second.CanContinue);

            findings[0].Fix = true;
            Assert.True(second.CanContinue);
        });
    }

    [Fact]
    public void Reopening_a_tile_paused_at_the_gate_does_not_spend_an_attempt_each_time()
    {
        // The lap has already moved on to Implement with the gate open; moving it again at every
        // opening would walk a goal to its budget by being closed and reopened.
        var engine = new GoalWorkflowEngine();
        engine.StartNewGoal("make it work");
        engine.RecordProposedPlan("the plan");
        Assert.True(engine.ApprovePlan());
        engine.IterationCount = 1;
        engine.CurrentPhase = GoalPhase.Implement;
        engine.IsPaused = true;
        engine.PausedAtReviewGate = true;

        var finding = new GoalFinding { Severity = GoalSeverity.Warning, Title = "nit" };
        engine.LastReview = new GoalReviewResult { WasStructured = true, Findings = [finding] };

        var path = Path.Combine(Dir, "reopened-at-the-gate.json");
        new GoalStatePersistence().Save(path, engine.ToState(
            [new GoalMessage { Role = GoalMessageRole.Assistant, Text = "reviewed",
                Phase = GoalPhase.Review, Findings = [finding] }],
            "", ""));

        Ui.Run(() =>
        {
            for (var opening = 0; opening < 3; opening++)
            {
                var vm = Open(path);

                Assert.Equal(GoalPhase.Implement, vm.CurrentPhase);
                Assert.True(vm.ShowReviewGate);

                vm.Dispose();

                Assert.Equal(1, new GoalStatePersistence().Load(path)!.IterationCount);
            }
        });
    }

    // ── Detection ───────────────────────────────────────

    [Fact]
    public void A_detection_over_a_tree_nobody_could_read_never_reaches_the_tool()
    {
        Ui.Run(async () =>
        {
            var prompts = Script("some goal");

            // The real reader where git cannot answer: not null, a note saying so.
            WorktreeReader.Factory = null;

            using var vm = NewTile();
            await vm.DetectGoalCommand.ExecuteAsync(null);

            Assert.Empty(prompts);
            Assert.Contains(vm.Messages, m => m.Text.Contains("could not be read"));
        });
    }

    [Fact]
    public void A_failed_detection_does_not_point_at_a_button_that_cannot_help()
    {
        Ui.Run(async () =>
        {
            using var vm = NewTile();

            // Detection runs from the Goal phase, where Resume does nothing.
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) => throw new InvalidOperationException("boom");

            await vm.DetectGoalCommand.ExecuteAsync(null);

            Assert.Equal(GoalPhase.Goal, vm.CurrentPhase);
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("Resume"));
            Assert.Contains(vm.Messages, m => m.Text.Contains("ry again"));
        });
    }

    [Fact]
    public void A_detection_that_throws_says_so_rather_than_doing_nothing()
    {
        Ui.Run(async () =>
        {
            using var vm = NewTile();

            // Thrown from the tree read, outside the AI call's own catch.
            WorktreeReader.Factory = (_, _) => throw new InvalidOperationException("git is on fire");

            await vm.DetectGoalCommand.ExecuteAsync(null);

            Assert.Contains(vm.Messages, m => m.Text.Contains("git is on fire"));
        });
    }

    /// <summary>A detection that comes to nothing — a tree that turned out clean, or a tool that answered
    /// nothing — leaves the session it would have replaced alone.</summary>
    [Theory]
    [InlineData(true, "no uncommitted changes")]
    [InlineData(false, null)]
    public void A_detection_that_comes_to_nothing_leaves_the_session_alone(bool treeTurnsOutClean, string? note)
    {
        Ui.Run(async () =>
        {
            AnswerWith("Which files?");

            using var vm = NewTile();
            await Send(vm, "a goal worth keeping");

            if (treeTurnsOutClean)
                WorktreeReader.Factory = (_, _) => Task.FromResult<string?>(null);
            else
                AnswerWith("   ");

            await vm.DetectGoalCommand.ExecuteAsync(null);

            Assert.Contains(vm.Messages, m => m.Text.Contains("a goal worth keeping"));
            Assert.Equal("", vm.InputText);
            if (note is not null) Assert.Contains(vm.Messages, m => m.Text.Contains(note));
        });
    }

    /// <summary>Detecting a goal adopts it as the user's own turn and goes on to Clarify.</summary>
    [Fact]
    public void Detecting_a_goal_adopts_it_and_goes_on_to_clarify()
    {
        Ui.Run(async () =>
        {
            AnswerWith("Finish the pairing flow so a paired device survives a restart.");

            using var vm = NewTile();
            await vm.DetectGoalCommand.ExecuteAsync(null);

            Assert.Contains(vm.Messages,
                m => m.Role == GoalMessageRole.User && m.Text.Contains("survives a restart"));
            Assert.Equal(GoalPhase.Clarify, vm.CurrentPhase);
        });
    }

    /// <summary>Nothing a detection does writes over the composer — text typed before the click, or
    /// while the tool was working.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_detection_keeps_what_the_user_typed_into_the_composer(bool typedBeforeTheClick)
    {
        Ui.Run(async () =>
        {
            using var vm = NewTile();

            const string typed = "what I was writing";
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                if (!typedBeforeTheClick) vm.InputText = typed;
                return Task.FromResult<AiOutput>("Finish the pairing flow.");
            };
            if (typedBeforeTheClick) vm.InputText = typed;

            await vm.DetectGoalCommand.ExecuteAsync(null);

            Assert.Equal(typed, vm.InputText);
            Assert.Contains(vm.Messages, m => m.Text.Contains("Finish the pairing flow."));
        });
    }

    [Fact]
    public void Detect_and_run_starts_at_the_review_because_the_changes_are_already_on_disk()
    {
        Ui.Run(async () =>
        {
            var prompts = Script("Make the totals include discounts.", "VERDICT: PASS");

            using var vm = NewTile();
            await vm.DetectGoalAndRunCommand.ExecuteAsync(null);

            // The detection and the review; no implementation first.
            Assert.Equal(2, prompts.Count);
            Assert.Contains("Review the code changes", prompts[1]);
            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
        });
    }

    /// <summary>Detect &amp; run judges work already there, so it measures from HEAD and says so in the
    /// saved state for a Resume to keep doing it.</summary>
    [Fact]
    public void Detect_and_run_measures_its_diffs_from_head_and_says_so_in_the_saved_state()
    {
        Ui.Run(async () =>
        {
            AnswerWith("Finish the cart", "VERDICT: PASS");

            var vm = NewTile();
            vm.Criteria.MaxIterations = 1;
            await vm.DetectGoalAndRunCommand.ExecuteAsync(null);

            Assert.True(Saved(vm).ReviewsExistingWork,
                "the tile forgot that this goal is about work that was already in the tree");
        });
    }

    /// <summary>Words beside Detect &amp; run narrow the detection; its @ path stays on the goal it
    /// adopts, and the composer is cleared once they are spent.</summary>
    [Fact]
    public void A_composer_text_beside_detect_and_run_narrows_the_detection_and_the_goal_it_adopts()
    {
        Ui.Run(async () =>
        {
            var prompts = Script("Tylko parser odpowiedzi.", "VERDICT: PASS");

            using var vm = NewTile();
            WriteFile("src/mTiles/Services/GoalResponseParser.cs");
            vm.InputText = "skup sie tylko na @src/mTiles/Services/GoalResponseParser.cs";
            await vm.DetectGoalAndRunCommand.ExecuteAsync(null);

            Assert.Contains("The user narrowed this detection", prompts[0]);
            Assert.Contains("@src/mTiles/Services/GoalResponseParser.cs", prompts[0]);
            Assert.Equal("", vm.InputText);
            Assert.Equal(2, prompts.Count);
            Assert.DoesNotContain("The user narrowed this review", prompts[1]);

            Assert.Equal(["src/mTiles/Services/GoalResponseParser.cs"], Saved(vm).ScopePaths);
        });
    }

    // ── Failures ────────────────────────────────────────

    [Fact]
    public void A_run_that_dies_of_an_unexpected_exception_leaves_something_to_press()
    {
        Ui.Run(async () =>
        {
            using var vm = NewTile();

            // Straight to the loop: no questions, a plan, and the plan approved.
            AnswerWith(NoMoreQuestions, "1. Do the thing.");
            await Send(vm, "a goal");
            Assert.Equal(GoalPhase.Plan, vm.CurrentPhase);

            // Thrown from the tree read, which lands in the catch of last resort.
            WorktreeReader.Factory = (_, _) => throw new InvalidOperationException("git is on fire");
            await Send(vm, "ok");

            Assert.Contains(vm.Messages, m => m.Text.Contains("git is on fire"));

            // Paused, so Resume and the finished-run actions are there rather than only +.
            Assert.True(GoalWorkflowEngine.IsMidRun(vm.CurrentPhase));
            Assert.False(vm.IsRunning);
            Assert.True(vm.IsPaused);
            Assert.True(vm.ShowResume);
            Assert.True(vm.HasFinishedRunActions);
        });
    }

    [Fact]
    public void A_tool_that_says_it_failed_stops_the_run_instead_of_being_believed()
    {
        Ui.Run(async () =>
        {
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) => Task.FromResult(
                AiOutput.Failure("I got as far as renaming Cart.cs.\n\n[error] Credit balance is too low"));

            using var vm = NewTile();
            await Send(vm, "a goal");

            // Judged on the fact, not on the text — and the text is still shown, as the only account of
            // what a failed run may have written.
            Assert.True(vm.IsPaused);
            Assert.DoesNotContain(vm.Messages, m => m.Role == GoalMessageRole.Assistant);
            Assert.Contains(vm.Messages, m => m.Text.Contains("renaming Cart.cs"));
            Assert.Contains(vm.Messages, m => m.Text.Contains("reported a failure"));
        });
    }

    [Fact]
    public void A_claude_model_refusal_names_the_model_and_the_route_that_still_works()
    {
        Ui.Run(async () =>
        {
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) => Task.FromResult(
                AiOutput.Failure(
                    "[stderr] ⚠ claude.ai connectors are disabled because ANTHROPIC_API_KEY or another " +
                    "auth source is set and takes precedence over your claude.ai login\n" +
                    "[claude-code:unrecognized_model] {\"model\":\"z-ai/glm-5.3-flash\"," +
                    "\"query_source\":\"sdk\"}"));

            using var vm = NewTile();
            await Send(vm, "a goal");

            Assert.True(vm.IsPaused);
            Assert.Contains(vm.Messages, m => m.Text.Contains("reported a failure")
                && m.Text.Contains("verifies the model id against the provider"));
            Assert.Contains(vm.Messages, m => m.Text.Contains("agent tile"));
        });
    }

    [Fact]
    public void A_dropped_stream_is_retried_once_without_asking()
    {
        Ui.Run(async () =>
        {
            var prompts = Script(
                AiOutput.Failure("[error] API Error: stream closed before completion"),
                """{"questions":[{"question":"Which file?"}]}""");

            using var vm = NewTile();
            await Send(vm, "a goal");

            // The retry's answer is what the tile acts on; the loop never saw the failure.
            Assert.Equal(2, prompts.Count);
            Assert.False(vm.IsRunning);
            Assert.Single(vm.Questions);
            Assert.Contains(vm.Messages, m => m.Text.Contains("retrying this run on its own"));
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("reported a failure"));
        });
    }

    [Fact]
    public void A_second_dropped_stream_stops_and_waits_for_the_user()
    {
        Ui.Run(async () =>
        {
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) => Task.FromResult(
                AiOutput.Failure($"half a plan\n\n[error] API Error: stream closed, attempt {++asked}"));

            using var vm = NewTile();
            await Send(vm, "a goal");

            // One unasked retry, not a loop (the allowance is GoalTilePolicy.BrokenStreamRetries).
            Assert.Equal(1 + GoalTilePolicy.BrokenStreamRetries, asked);
            Assert.True(vm.IsPaused);
            Assert.Contains(vm.Messages, m => m.Text.Contains("retrying this run on its own"));
            Assert.Contains(vm.Messages, m => m.Text.Contains("reported a failure"));
            Assert.Contains(vm.Messages, m => m.Text.Contains("attempt 2"));
        });
    }

    [Fact]
    public void A_review_written_as_broken_json_is_salvaged_by_one_re_send_of_the_answer()
    {
        Ui.Run(async () =>
        {
            // Quoting JsonRepair cannot resolve, so only the tool itself can mend it.
            var broken = "```json\n{\"goalMet\": true, \"findings\": [{\"severity\": \"warning\", " +
                         "\"detail\": \"he said \"a\", \"b\" and the outer catch swallows it\"}]}\n```";
            var prompts = Script("Make the totals include discounts.", broken,
                "{\"goalMet\": true, \"findings\": []}");

            using var vm = NewTile();
            await vm.DetectGoalAndRunCommand.ExecuteAsync(null);

            // Detection, the broken review, and the salvage — which carries the answer alone.
            Assert.Equal(3, prompts.Count);
            Assert.Contains("exactly the same JSON", prompts[2]);
            Assert.DoesNotContain("Review the code changes", prompts[2]);
            Assert.Contains(broken, prompts[2]);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Text.Contains("Goal met"));
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("Not done:"));
            Assert.Contains(vm.Messages, m => m.Text.Contains("re-send the same block"));
        });
    }

    [Fact]
    public void A_salvage_that_also_fails_leaves_todays_behaviour_standing()
    {
        Ui.Run(async () =>
        {
            var broken = "{\"goalMet\": false, \"findings\": [{\"severity\": \"error\", " +
                         "\"title\": \"Swallowed \"a\", \"b\" here\"}]}";
            var prompts = Script("Make the totals include discounts.", broken, "VERDICT: FAIL",
                "the fix is applied", "VERDICT: PASS");

            using var vm = NewTile();
            await vm.DetectGoalAndRunCommand.ExecuteAsync(null);

            // One salvage round, then the re-implementation the original review asked for, then a pass.
            Assert.Equal(5, prompts.Count);
            Assert.Contains("exactly the same JSON", prompts[2]);
            Assert.Equal(1, prompts.Count(p => p.Contains("exactly the same JSON")));
            Assert.DoesNotContain("exactly the same JSON", prompts[3]);
            Assert.Contains(vm.Messages, m => m.Text.Contains("Not done:"));
        });
    }

    [Fact]
    public void A_salvage_hit_by_a_dropped_stream_retries_without_naming_a_phase_failure()
    {
        Ui.Run(async () =>
        {
            // The salvage is quiet by contract, and so is its unasked retry.
            var broken = "{\"goalMet\": true, \"findings\": [{\"severity\": \"warning\", " +
                         "\"detail\": \"he said \"a\", \"b\" and then stopped\"}]}";
            var prompts = Script("Make the totals include discounts.", broken,
                AiOutput.Failure("[error] API Error: stream closed before completion"),
                AiOutput.Failure("[error] API Error: stream closed again"),
                "the fix is applied", "VERDICT: PASS");

            using var vm = NewTile();
            await vm.DetectGoalAndRunCommand.ExecuteAsync(null);

            Assert.Equal(6, prompts.Count);
            Assert.Contains("exactly the same JSON", prompts[2]);
            Assert.Equal(2, prompts.Count(p => p.Contains("exactly the same JSON")));
            Assert.DoesNotContain("exactly the same JSON", prompts[4]);

            Assert.Equal(1, vm.Messages.Count(m => m.Text.Contains("retrying this run on its own")));
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("reported a failure"));
            Assert.Contains(vm.Messages, m => m.Text.Contains("Not done:"));
        });
    }

    /// <summary>An empty answer is not an answer, before the loop or inside it: nothing blank reaches
    /// the transcript and the tile pauses rather than ending the goal.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_empty_answer_pauses_and_puts_nothing_in_the_transcript(bool insideTheLoop)
    {
        Ui.Run(async () =>
        {
            using var vm = NewTile();
            if (insideTheLoop)
            {
                AnswerWith("Which files?", NoMoreQuestions, "The plan", "");
                await RunToSummary(vm);
            }
            else
            {
                AnswerWith("   \n  ");
                await Send(vm, "a goal");
            }

            Assert.NotEqual(GoalPhase.Summary, vm.CurrentPhase);
            Assert.True(vm.IsPaused);
            Assert.Contains(vm.Messages, m => m.Text.Contains("returned nothing"));
            Assert.DoesNotContain(vm.Messages,
                m => m.Role == GoalMessageRole.Assistant && string.IsNullOrWhiteSpace(m.Text));
        });
    }

    [Fact]
    public void A_tool_that_throws_leaves_the_goal_resumable_rather_than_finished()
    {
        Ui.Run(async () =>
        {
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
                throw new InvalidOperationException("the tool exploded");

            using var vm = NewTile();
            await Send(vm, "a goal");

            Assert.NotEqual(GoalPhase.Summary, vm.CurrentPhase);
            Assert.True(vm.IsPaused);
            // Named, because a goal can run two agents.
            Assert.Contains(vm.Messages, m => m.Text.Contains("Fake Tool failed: the tool exploded"));
        });
    }

    [Fact]
    public void A_pause_while_the_working_tree_is_being_read_is_a_pause_not_an_error()
    {
        Ui.Run(async () =>
        {
            using var vm = NewTile();

            WorktreeReader.Factory = (_, ct) =>
            {
                vm.PauseCommand.Execute(null);
                throw new OperationCanceledException(ct);
            };
            AnswerWith("Which files?", NoMoreQuestions, "The plan");

            await RunToSummary(vm);

            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("Unexpected error"));
            Assert.True(vm.IsPaused);
        });
    }

    [Fact]
    [Trait("Category", "Slow")] // the real reader is the point, so real git processes; 3 s on a Windows runner
    public void A_workspace_git_cannot_read_does_not_end_every_goal_after_one_attempt()
    {
        Ui.Run(async () =>
        {
            // The real reader against a directory that is not a repository.
            WorktreeReader.Factory = null;
            AnswerWith(ThroughTheReview("VERDICT: FAIL"));

            using var vm = NewTile();
            vm.Criteria.MaxIterations = 2;
            await RunToSummary(vm);

            // It ran out of attempts, which is the truth — not "the implementation changed nothing".
            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("changed no files"));
            Assert.Contains(vm.Messages, m => m.Text.Contains("without meeting the completion criteria"));
        });
    }

    // ── Continue ────────────────────────────────────────

    /// <summary>Drives a whole run with one warning per review, its title moving so the reviews never
    /// look identical; answers how many implementations ran.</summary>
    private static Func<int> LoopAnsweringWithOneWarning()
    {
        var before = 0;
        var reviews = 0;
        var implemented = 0;

        GoalTileViewModel.AiRunnerFactory = (_, prompt, _, _) =>
        {
            if (prompt.Contains("Implement the following goal"))
            {
                implemented++;
                return Task.FromResult<AiOutput>("Implemented it");
            }

            if (prompt.Contains("Review the code changes"))
            {
                reviews++;
                return Task.FromResult<AiOutput>(
                    "```json\n{\"goalMet\":false,\"findings\":[{\"severity\":\"warning\"," +
                    $"\"title\":\"W{reviews}\"}}]}}\n```");
            }

            return Task.FromResult<AiOutput>(before++ switch
            {
                0 => "Which files?",
                1 => NoMoreQuestions,
                _ => "The plan",
            });
        };

        return () => implemented;
    }

    [Fact]
    public void A_run_that_ran_out_of_attempts_can_be_given_more_without_losing_the_conversation()
    {
        Ui.Run(async () =>
        {
            var implemented = LoopAnsweringWithOneWarning();

            using var vm = NewTile();
            vm.Criteria.MaxIterations = 2;
            await RunToSummary(vm);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Equal(2, implemented());
            Assert.True(vm.CanContinue);
            Assert.Equal("Continue · +2", vm.ContinueLabel);

            // The summary names what stood in the way.
            Assert.Contains(vm.Messages, m => m.Text.Contains("1 warning"));

            var before = vm.Messages.Count;
            await vm.ContinueRunCommand.ExecuteAsync(null);

            Assert.Equal(4, implemented());
            Assert.Equal(4, vm.Criteria.MaxIterations);
            Assert.True(vm.Messages.Count > before);
            Assert.Contains(vm.Messages, m => m.Text == "a goal");
        });
    }

    /// <summary>
    /// The attempts Continue adds belong to that goal: the next one starts from the number the user
    /// chose, unless they have typed a new one since.
    /// </summary>
    [Theory]
    [InlineData(null, 2)]
    [InlineData(8, 8)]
    public void The_attempts_Continue_adds_belong_to_that_goal_and_not_to_the_tile(int? retyped, int expected)
    {
        Ui.Run(async () =>
        {
            var implemented = LoopAnsweringWithOneWarning();

            using var vm = NewTile();
            vm.Criteria.MaxIterations = 2;

            await RunToSummary(vm);
            Assert.True(vm.CanContinue);

            await vm.ContinueRunCommand.ExecuteAsync(null);
            Assert.Equal(4, vm.Criteria.MaxIterations);
            Assert.Equal(4, implemented());

            if (retyped is { } typed) vm.Criteria.MaxIterations = typed;

            await Send(vm, "a different goal");

            Assert.Equal(expected, vm.Criteria.MaxIterations);
        });
    }

    [Fact]
    public void A_pause_taken_between_two_answered_phases_does_not_start_the_next_one()
    {
        Ui.Run(async () =>
        {
            using var vm = NewTile();

            // Paused from inside the clarification answer, so it stands when the plan would be asked for.
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                if (asked == 2) vm.PauseCommand.Execute(null);
                return Task.FromResult<AiOutput>(asked == 1 ? "Which files?" : NoMoreQuestions);
            };

            await Send(vm, "a goal");
            await Send(vm, "all of it");

            Assert.Equal(2, asked);
            Assert.True(vm.IsPaused);
            Assert.DoesNotContain("creating a plan", vm.PhaseLabel);
            Assert.Contains("Resume", vm.PhaseLabel);
        });
    }

    [Fact]
    public void The_tiles_own_notes_do_not_make_a_waiting_tile_look_interrupted()
    {
        Ui.Run(async () =>
        {
            AnswerWith("```json\n{\"questions\":[{\"question\":\"Which file?\"}]}\n```");

            var first = NewTile();
            await Send(first, "a goal");

            // The tile's own aside, last in the transcript; counted, each restart would add one more.
            await Send(first, "1.");
            Assert.Equal(GoalMessageRole.System, first.Messages[^1].Role);

            using var second = Reopen(first);

            Assert.Equal(GoalPhase.Clarify, second.CurrentPhase);
            Assert.False(second.IsPaused);
        });
    }

    // ── Badges ──────────────────────────────────────────

    [Fact]
    public void The_badges_come_back_with_the_tile()
    {
        Ui.Run(async () =>
        {
            var review = "```json\n{\"goalMet\":false,\"findings\":[" +
                         "{\"severity\":\"blocker\",\"title\":\"Unacceptable\"}," +
                         "{\"severity\":\"suggestion\",\"title\":\"Rename\"}]}\n```";
            AnswerWith(ThroughTheReview(review));

            var first = NewTile();
            first.Criteria.MaxIterations = 1;
            await RunToSummary(first);

            Assert.Equal(["1B", "1S"], first.Badges.Select(b => b.Text));

            // A badge opens its own severity's findings and no other's.
            Assert.Equal(["Unacceptable"], Titles(first, GoalSeverity.Blocker));
            Assert.Equal(["Rename"], Titles(first, GoalSeverity.Suggestion));
            Assert.All(first.Badges, b => Assert.True(b.HasFindings));

            Assert.False(first.IsShowingFindings);
            first.OpenFindingsCommand.Execute(first.Badges.Single(b => b.IsBlocker));
            Assert.True(first.IsShowingFindings);
            Assert.Equal(["Unacceptable"], first.OpenBadge!.Findings.Select(f => f.Title));
            first.CloseFindingsCommand.Execute(null);
            Assert.False(first.IsShowingFindings);

            using var second = Reopen(first);

            // Only severities that found something, and their findings taken from the transcript.
            Assert.Equal(["1B", "1S"], second.Badges.Select(b => b.Text));
            Assert.DoesNotContain(second.Badges, b => b.Severity == GoalSeverity.Error);
            Assert.Equal(["Unacceptable"], Titles(second, GoalSeverity.Blocker));
            Assert.Equal(["Rename"], Titles(second, GoalSeverity.Suggestion));
        });
    }

    [Fact]
    public void A_badge_with_nothing_behind_it_does_not_open()
    {
        Ui.Run(async () =>
        {
            // Prose, not JSON: counted as a review with no findings.
            AnswerWith(ThroughTheReview("This is not done yet."));

            using var vm = NewTile();
            vm.Criteria.MaxIterations = 1;
            await RunToSummary(vm);

            Assert.Empty(vm.Badges);

            // A badge restored from a file written before findings were kept refuses to open.
            vm.OpenFindingsCommand.Execute(new GoalBadge { Severity = GoalSeverity.Error, Count = 2 });
            Assert.False(vm.IsShowingFindings);

            vm.OpenFindingsCommand.Execute(null);
            Assert.False(vm.IsShowingFindings);
        });
    }

    // ── Committing ──────────────────────────────────────

    /// <summary>The "commit when done" switch reaches the commit path only when it is on: in a scratch
    /// directory git cannot say what the run changed, and the tile says so only if it went looking.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_commit_switch_decides_whether_the_run_goes_looking_for_something_to_commit(bool on)
    {
        Ui.Run(async () =>
        {
            WithBaseline();
            AnswerWith(ThroughTheReview("VERDICT: PASS"));

            using var vm = NewTile();
            vm.Criteria.MaxIterations = 1;
            vm.Criteria.CommitWhenDone = on;
            await RunToSummary(vm);

            var wentLooking = vm.Messages.Any(m => m.Text.Contains("Git could not say what this run"));
            Assert.Equal(on, wentLooking);
        });
    }

    /// <summary>A review on its own never commits, whatever the switch says.</summary>
    [Fact]
    public void A_review_on_its_own_does_not_commit_even_with_the_switch_on()
    {
        Ui.Run(async () =>
        {
            WithBaseline();

            using var vm = NewTile();
            vm.Criteria.CommitWhenDone = true;

            AnswerWith("Finish the cart", "VERDICT: PASS");
            await vm.ReviewCommand.ExecuteAsync(null);

            Assert.DoesNotContain(vm.Messages,
                m => m.Text.Contains("Git could not say what this run"));
        });
    }

    /// <summary>A commit plan the last message did not carry is read out of the turn and said out loud;
    /// needs a real repository, because the commit scope refuses a directory git does not recognise.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")] // many real git processes; close to the budget on a Windows runner
    public void A_commit_plan_the_last_message_did_not_carry_is_read_out_of_the_turn()
    {
        Ui.Run(async () =>
        {
            // Three commits, so the baseline has a parent and a grandparent for ScopeAsync to read.
            using var repo = new GitTestRepo(prefix: "goal-commit-plan");
            foreach (var content in new[] { "a", "b", "c" })
            {
                repo.Write("seed.txt", content + "\n");
                repo.CommitAll(content);
            }
            var baseline = repo.Git("rev-parse HEAD").Trim();

            // The goal's own baseline, then no closing snapshot: the scope is read against the tree.
            var captures = 0;
            GoalBaseline.Factory = (_, _) => Task.FromResult(
                ++captures == 1 ? new GoalBaselineResult(baseline, false) : GoalBaselineResult.None);

            // The run's own work, left uncommitted as an implementation leaves it.
            repo.Write("Feature.cs", "class Feature { }\n");

            const string plan = "```json\n{\"commits\":[{\"type\":\"feat\",\"subject\":\"add feature\"," +
                                 "\"files\":[\"Feature.cs\"]}]}\n```";

            AnswerWithTurns(
                ("Which files?", null),
                (NoMoreQuestions, null),
                ("The plan", null),
                ("Implemented it", null),
                (CleanReview, null),
                (Epilogue, plan + "\n\n" + Epilogue));

            using var vm = new GoalTileViewModel(repo.Path, Settings) { ConfirmAction = _ => Task.FromResult(true) };
            await RunToSummary(vm);
            Assert.True(vm.CanCommit);

            await vm.CommitWorkCommand.ExecuteAsync(null);

            Assert.Contains("feat: add feature", repo.Git("log --format=%s -2"));
            Assert.Contains(vm.Messages, m =>
                m.Text.Contains("did not carry a usable commit plan")
                && m.Text.Contains("taken from an earlier message of the same run"));
        });
    }

    [Fact]
    public void Approving_a_new_plan_forgets_the_old_plans_reviews()
    {
        Ui.Run(async () =>
        {
            const string sameFinding =
                "```json\n{\"goalMet\":false,\"findings\":[" +
                "{\"severity\":\"error\",\"file\":\"a.cs\",\"title\":\"Still wrong\"}]}\n```";

            // Plan A's review, then a rejection back through clarify, then plan B's matching review.
            AnswerWith("Which files?", NoMoreQuestions, "Plan A", "Implemented A", sameFinding,
                NoMoreQuestions, "Plan B", "Implemented B", sameFinding);

            using var vm = NewTile();
            vm.Criteria.MaxIterations = 1;

            await RunToSummary(vm);                          // plan A runs its single attempt
            await Send(vm, "no, do it differently");         // → plan B
            await Send(vm, "ok");

            // Only one of the two matching reviews belongs to this plan.
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("reached the same conclusion"));
        });
    }

    // ── The approval panel and the working tile ─────────

    [Fact]
    public void The_plan_is_approved_by_a_button_and_changed_by_typing_into_the_same_box()
    {
        Ui.Run(async () =>
        {
            AnswerWith(NoMoreQuestions, "The plan", "Implemented it", "VERDICT: PASS");

            using var vm = NewTile();
            await Send(vm, "a goal");

            Assert.True(vm.ShowApproval);
            Assert.False(vm.ShowComposer);
            Assert.Equal("Approve plan", vm.ApprovalActionLabel);

            vm.InputText = "no, do it differently";
            Assert.Equal("Send changes", vm.ApprovalActionLabel);

            vm.InputText = "";
            await vm.ApproveOrChangeCommand.ExecuteAsync(null);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Role == GoalMessageRole.User && m.Text == "ok");
        });
    }

    [Fact]
    public void Nothing_can_be_typed_at_a_tile_that_is_working()
    {
        Ui.Run(async () =>
        {
            var gate = new TaskCompletionSource<AiOutput>();
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) => gate.Task;

            using var vm = NewTile();
            vm.InputText = "a goal";
            var running = vm.SubmitCommand.ExecuteAsync(null);

            Assert.True(vm.IsRunning);
            Assert.False(vm.ShowComposer);
            Assert.False(vm.ShowQuestions);
            Assert.False(vm.ShowApproval);

            gate.SetResult(NoMoreQuestions);
            await running;
        });
    }

    [Fact]
    public void A_status_line_arriving_after_the_run_is_not_shown()
    {
        Ui.Run(async () =>
        {
            AnswerWith("Which files?");

            using var vm = NewTile();
            await Send(vm, "a goal");
            Assert.False(vm.IsRunning);

            // The reader thread losing the race after the run cleared Activity.
            vm.SetActivityIfRunning("Read src/Cart.cs");

            Assert.Equal("", vm.ActivityText);
        });
    }

    [Fact]
    public void The_status_strip_stops_saying_what_the_tool_is_doing_when_it_stops_doing_it()
    {
        Ui.Run(async () =>
        {
            AnswerWith("Which files?");

            using var vm = NewTile();
            vm.ActivityText = "Read src/Cart.cs";

            await Send(vm, "a goal");

            Assert.False(vm.IsRunning);
            Assert.Equal("", vm.ActivityText);
        });
    }

    // ── The goal file ───────────────────────────────────

    [Fact]
    public void A_tile_nobody_used_leaves_no_file_behind_and_a_used_one_survives_being_closed()
    {
        Ui.Run(async () =>
        {
            AnswerWith("Which files?");

            var untouched = NewTile();
            var untouchedPath = untouched.FilePath;
            untouched.Dispose();
            Assert.False(File.Exists(untouchedPath));

            var used = NewTile();
            var usedPath = used.FilePath;
            await Send(used, "a goal");

            // Closing before the debounce fires is what proves the flush.
            used.Dispose();

            Assert.True(File.Exists(usedPath));
            Assert.Contains("a goal", File.ReadAllText(usedPath));
        });
    }

    /// <summary>
    /// A pause taken as the implementation answers owes the review, says so rather than claiming to be
    /// working, and survives the tile being closed: reopened, Resume runs the review and finishes.
    /// </summary>
    [Fact]
    public void A_paused_run_survives_being_closed_and_carries_on_from_the_review_when_reopened()
    {
        Ui.Run(async () =>
        {
            var first = NewTile();

            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                if (asked == 4) first.PauseCommand.Execute(null);
                return Task.FromResult<AiOutput>(UpToTheReview[Math.Min(asked, 4) - 1]);
            };

            await RunToSummary(first);

            Assert.True(first.IsPaused);
            Assert.False(first.IsRunning);
            Assert.Equal(GoalPhase.Review, first.CurrentPhase);
            Assert.True(GoalTilePolicy.ResumesAtReview(first.CurrentPhase));
            Assert.DoesNotContain("implementing", first.PhaseLabel);
            Assert.DoesNotContain("reviewing", first.PhaseLabel);
            Assert.Contains("Resume", first.PhaseLabel);

            using var second = Reopen(first);

            Assert.True(second.IsPaused);
            Assert.Equal(GoalPhase.Review, second.CurrentPhase);
            Assert.Contains("Resume", second.PhaseLabel);

            AnswerWith("VERDICT: PASS");
            await second.ResumeCommand.ExecuteAsync(null);

            Assert.Equal(GoalPhase.Summary, second.CurrentPhase);
            Assert.False(second.IsPaused);
        });
    }

    /// <summary>A goal file that cannot be opened is reported in the tile; that the store then never
    /// writes over it is pinned in GoalStateStoreTests.</summary>
    [Fact]
    public void A_goal_file_that_cannot_be_opened_is_reported()
    {
        Ui.Run(() =>
        {
            var path = Path.Combine(Dir, "held.json");
            File.WriteAllText(path, "{\"OriginalGoal\":\"a real session\"}");

            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                using var vm = Open(path);
                Assert.Contains(vm.Messages, m => m.Text.Contains("could not be opened"));
            }
        });
    }

    [Fact]
    public void Starting_a_fresh_goal_on_an_unused_tile_still_leaves_no_file()
    {
        Ui.Run(async () =>
        {
            using var vm = NewTile();
            var path = vm.FilePath;

            await vm.StartNewConversationAsync();

            Assert.False(File.Exists(path));
        });
    }

    [Fact]
    public void Starting_a_fresh_goal_on_a_used_tile_writes_the_reset_out()
    {
        Ui.Run(async () =>
        {
            AnswerWith("Which files?");

            using var vm = NewTile();
            await Send(vm, "a goal");
            WaitForFile(vm);

            Assert.Contains("a goal", await File.ReadAllTextAsync(vm.FilePath));

            await vm.StartNewConversationAsync();

            Assert.DoesNotContain("a goal", await File.ReadAllTextAsync(vm.FilePath));
        });
    }

    [Fact]
    public void A_new_goal_with_no_dialog_to_ask_in_keeps_the_current_one()
    {
        Ui.Run(async () =>
        {
            AnswerWith("Which files?");

            using var vm = NewTile();
            await Send(vm, "a goal");
            WaitForFile(vm);
            vm.ConfirmAction = null;

            await vm.StartNewConversationAsync();

            // An unanswered question is not a yes.
            Assert.Contains("a goal", await File.ReadAllTextAsync(vm.FilePath));
            Assert.Contains(vm.Messages, m => m.Text.Contains("cannot ask whether to discard"));
        });
    }

    // ── Plans ───────────────────────────────────────────

    [Fact]
    public void Approving_a_plan_that_was_never_proposed_leaves_the_tile_resumable()
    {
        Ui.Run(async () =>
        {
            // The plan run returns nothing, so the tile pauses in Plan with no plan in it.
            AnswerWith("Which files?", NoMoreQuestions, "");

            using var vm = NewTile();
            await Send(vm, "a goal");
            await Send(vm, "all of it");
            Assert.True(vm.IsPaused);

            await Send(vm, "ok");

            Assert.NotEqual(GoalPhase.Implement, vm.CurrentPhase);
            Assert.True(vm.IsPaused);
            Assert.Contains(vm.Messages, m => m.Text.Contains("no plan to approve"));
        });
    }

    /// <summary>A plan that was rejected, whose replacement answered nothing, cannot then be approved.
    /// </summary>
    [Fact]
    public void A_plan_that_was_rejected_cannot_be_approved_afterwards()
    {
        Ui.Run(async () =>
        {
            AnswerWith("Which files?", NoMoreQuestions, "PLAN A — rewrite everything", NoMoreQuestions, "");

            using var vm = NewTile();
            await Send(vm, "a goal");                    // → clarify
            await Send(vm, "all of it");                 // → plan A
            await Send(vm, "no, do it differently");     // rejected → a second planning run answers nothing

            Assert.Equal(GoalPhase.Plan, vm.CurrentPhase);
            Assert.True(vm.IsPaused);

            await Send(vm, "ok");

            Assert.NotEqual(GoalPhase.Implement, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Text.Contains("no plan to approve"));
        });
    }

    /// <summary>A remark about a plan reaches the next planning run together with the plan it was about.
    /// </summary>
    [Fact]
    public void A_remark_about_a_plan_carries_the_plan_it_was_about()
    {
        Ui.Run(async () =>
        {
            const string firstPlan = "Goal: tighten the cart.\nSteps:\n1. src/Cart.cs - apply discounts.";
            const string revised = "Changed step 1.\n\nGoal: tighten the cart.\nSteps:\n1. tests/CartTests.cs - cover it.";
            var prompts = Script(NoMoreQuestions, firstPlan, NoMoreQuestions, revised);

            using var vm = NewTile();
            await Send(vm, "tighten the cart");               // Goal -> Clarify -> Plan
            await Send(vm, "leave step 1 and add a test");    // the argument, then a second plan

            var replan = prompts.Last();
            Assert.Contains("The plan you proposed last time", replan);
            Assert.Contains("apply discounts", replan);
            Assert.Contains("leave step 1 and add a test", replan);
        });
    }

    /// <summary>What gets approved is the revised plan, without the sentence about revising it.</summary>
    [Fact]
    public void Approving_a_revision_adopts_the_plan_and_not_the_note_above_it()
    {
        Ui.Run(async () =>
        {
            const string firstPlan = "Goal: tighten the cart.\nSteps:\n1. src/Cart.cs - apply discounts.";
            const string revised = "Changed step 1, as you asked.\n\nGoal: tighten the cart.\nSteps:\n1. tests/CartTests.cs - cover it.";
            var prompts = Script(NoMoreQuestions, firstPlan, NoMoreQuestions, revised, "Implemented it", CleanReview);

            using var vm = NewTile();
            await Send(vm, "tighten the cart");
            await Send(vm, "leave step 1 and add a test");
            await Send(vm, "ok");

            var implement = prompts.First(p => p.Contains("Approved implementation plan"));
            Assert.Contains("cover it", implement);
            Assert.DoesNotContain("as you asked", implement);
        });
    }

    /// <summary>Arguing twice over one draft, with an empty replan between, still shows the tool the
    /// draft.</summary>
    [Fact]
    public void A_second_remark_after_a_replan_that_answered_nothing_keeps_the_draft()
    {
        Ui.Run(async () =>
        {
            const string firstPlan = "Goal: tighten the cart.\nSteps:\n1. src/Cart.cs - apply discounts.";
            var prompts = Script(NoMoreQuestions, firstPlan, NoMoreQuestions, "   ", NoMoreQuestions, firstPlan);

            using var vm = NewTile();
            await Send(vm, "tighten the cart");
            await Send(vm, "leave step 1 and add a test");
            await Send(vm, "and drop the logging while you are there");

            var replan = prompts.Last(p => p.Contains("You are planning the implementation"));
            Assert.Contains("The plan you proposed last time", replan);
            Assert.Contains("apply discounts", replan);
            Assert.Contains("drop the logging", replan);
        });
    }

    // ── The run's closing snapshot ──────────────────────

    /// <summary>Reaching a summary records where the run ended — the second capture, distinguishable
    /// from the baseline — on every route into one.</summary>
    [Theory]
    [InlineData(GoalStopReason.Met, "VERDICT: PASS")]
    [InlineData(GoalStopReason.BudgetSpent, "VERDICT: FAIL - still broken")]
    public void Every_route_into_a_summary_records_where_the_run_ended(
        GoalStopReason expected, string verdict)
    {
        Ui.Run(async () =>
        {
            CountingBaseline();
            AnswerWith(NoMoreQuestions, "The plan", "Implemented it", verdict);

            var vm = NewTile();
            vm.Criteria.MaxIterations = 1;
            await Send(vm, "make the tile resumable");
            await vm.ApproveOrChangeCommand.ExecuteAsync(null);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);

            var state = Saved(vm);
            Assert.Equal(expected, state.LastStopReason);
            Assert.Equal("ref-1", state.BaselineRef);
            Assert.Equal("ref-2", state.EndRef);
        });
    }

    /// <summary>A run paused into its summary (budget spent, on a cancelled token) still records where it
    /// stopped.</summary>
    [Fact]
    public void A_run_paused_into_its_summary_still_records_where_it_stopped()
    {
        Ui.Run(async () =>
        {
            CountingBaseline();

            var vm = NewTile();
            vm.Criteria.MaxIterations = 1;

            // Paused as the review answers, with the budget spent, so the loop summarises.
            var answers = new Queue<string>([NoMoreQuestions, "The plan", "Implemented it"]);
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                if (answers.Count > 0) return Task.FromResult<AiOutput>(answers.Dequeue());

                vm.PauseCommand.Execute(null);
                return Task.FromResult<AiOutput>("VERDICT: FAIL - still broken");
            };

            await Send(vm, "make the tile resumable");
            await vm.ApproveOrChangeCommand.ExecuteAsync(null);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.False(string.IsNullOrEmpty(Saved(vm).EndRef),
                "a paused run left nothing saying where it stopped, so a commit would claim "
                + "everything in the tree");
        });
    }

    /// <summary>An attempt that wrote nothing takes an end only where there was none, and never moves one
    /// that exists.</summary>
    [Fact]
    public void An_attempt_that_wrote_nothing_does_not_move_the_runs_upper_end()
    {
        Ui.Run(async () =>
        {
            CountingBaseline();
            TreeNeverMoves();

            using var vm = NewTile();
            vm.Criteria.MaxIterations = 1;
            var path = vm.FilePath;

            AnswerWith(NoMoreQuestions, "The plan", "Implemented it", "VERDICT: FAIL");

            await Send(vm, "make the tile resumable");
            await vm.ApproveOrChangeCommand.ExecuteAsync(null);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Text.Contains("changed no files"));

            var afterTheRun = new GoalStatePersistence().Load(path)!.EndRef;
            Assert.Equal("ref-2", afterTheRun);

            // Continue: this attempt writes nothing either, and must not claim what appeared since.
            Assert.True(vm.CanContinue);
            await vm.ContinueRunCommand.ExecuteAsync(null);

            Assert.Equal(afterTheRun, Saved(vm).EndRef);
        });
    }

    /// <summary>Re-review judges the tree and leaves the run's upper end where the implementation put it.
    /// </summary>
    [Fact]
    public void A_review_that_changes_nothing_does_not_move_the_runs_upper_end()
    {
        Ui.Run(async () =>
        {
            CountingBaseline();

            var vm = NewTile();
            vm.Criteria.MaxIterations = 1;
            var path = vm.FilePath;

            AnswerWith(NoMoreQuestions, "The plan", "Implemented it", "VERDICT: PASS");
            await Send(vm, "make the tile resumable");
            await vm.ApproveOrChangeCommand.ExecuteAsync(null);

            var afterTheRun = new GoalStatePersistence().Load(path)!.EndRef;
            Assert.Equal("ref-2", afterTheRun);

            AnswerWith("VERDICT: PASS");
            await vm.ReReviewCommand.ExecuteAsync(null);

            Assert.Equal(afterTheRun, Saved(vm).EndRef);
        });
    }

    /// <summary>A goal detected from the tree and reviewed records an upper end (it has no implementation
    /// to keep one from), and a second look does not move it.</summary>
    [Fact]
    public void A_goal_detected_from_the_tree_records_an_upper_end_and_a_second_look_keeps_it()
    {
        Ui.Run(async () =>
        {
            var captures = CountingBaseline();

            var vm = NewTile();

            AnswerWith("Finish the cart", "VERDICT: PASS");
            await vm.ReviewCommand.ExecuteAsync(null);

            // One for the baseline, one for the end this review established.
            Assert.Equal(2, captures());

            AnswerWith("VERDICT: PASS");
            await vm.ReReviewCommand.ExecuteAsync(null);

            // Counted: no snapshot was taken, not merely a matching value on disk.
            Assert.Equal(2, captures());

            var state = Saved(vm);
            Assert.True(state.ReviewsExistingWork);
            Assert.Equal("ref-2", state.EndRef);
        });
    }

    // ── Set goal & run, and Review over a typed goal ────

    /// <summary>"Set goal &amp; run" asks nothing and approves its own plan.</summary>
    [Fact]
    public void Set_goal_and_run_asks_nothing_and_approves_its_own_plan()
    {
        Ui.Run(async () =>
        {
            var prompts = Script(NoMoreQuestions, "The plan", "Implemented it", CleanReview, "Implemented it");

            using var vm = NewTile();
            vm.InputText = "a typed goal";

            Assert.True(vm.CanSetGoalAndRun);
            await vm.SetGoalAndRunCommand.ExecuteAsync(null);

            Assert.Contains(vm.Messages, m => m.Role == GoalMessageRole.User && m.Text == "a typed goal");
            Assert.Contains(prompts, prompt => prompt.Contains("will not stop for questions"));
            Assert.False(vm.ShowApproval);
            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Text.Contains("approved automatically"));
        });
    }

    /// <summary>Review over a typed goal adopts what was typed whole, image markers included.</summary>
    [Fact]
    public void Review_over_a_typed_goal_keeps_what_was_typed()
    {
        Ui.Run(async () =>
        {
            using var vm = NewTile();
            vm.HasUncommittedChanges = true;
            vm.InputText = $"match the design {GoalImageMarker.For(1)}";

            AnswerWith(CleanReview);
            await vm.ReviewCommand.ExecuteAsync(null);

            Assert.Contains(vm.Messages, m => m.Role == GoalMessageRole.User
                                              && m.Text.Contains(GoalImageMarker.For(1)));
        });
    }

    /// <summary>The summary of a review on its own points at Continue where there is one, and at the
    /// composer where there is not.</summary>
    [Theory]
    [InlineData(ErrorReview, true, "Continue carries on from here")]
    [InlineData(CleanReview, false, "Type a new goal")]
    public void A_review_on_its_own_points_at_what_comes_next(string review, bool canContinue, string sentence)
    {
        Ui.Run(async () =>
        {
            AnswerWith("finish the discount work", review);

            using var vm = NewTile();
            vm.InputText = "";
            await vm.ReviewCommand.ExecuteAsync(null);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Equal(canContinue, vm.CanContinue);
            if (canContinue)
                Assert.Equal($"Continue · {vm.Criteria.MaxIterations} left", vm.ContinueLabel);
            Assert.Contains(vm.Messages, m => m.IsRunSummary && m.Text.Contains(sentence));
        });
    }

    // ── A block the tool's last message did not carry ───

    /// <summary>What a tool said in one run: its last message, and the whole turn behind it.</summary>
    private static void AnswerWithTurns(params (string Answer, string? Turn)[] answers)
    {
        var asked = 0;
        GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
        {
            var (answer, turn) = answers[Math.Min(asked++, answers.Length - 1)];
            return Task.FromResult(AiOutput.Answered(answer) with { WholeTurn = turn ?? "" });
        };
    }

    /// <summary>A paragraph written after the review, which becomes the tool's last message.</summary>
    private const string Epilogue = "The review above is complete, there is nothing new.";

    /// <summary>
    /// The review is read out of the whole turn only when the last message carried none, the turn's
    /// last block wins, and a substitution is said out loud.
    /// </summary>
    [Theory]
    // The last message is an epilogue; the review is in the turn behind it.
    [InlineData(Epilogue, CleanReview + "\n\n" + Epilogue, true)]
    // The last message carried a block, so an earlier draft never overrides it.
    [InlineData(CleanReview, ErrorReview + "\n\n" + CleanReview, false)]
    // Within the turn, the review written last beats a shape echoed earlier.
    [InlineData(Epilogue, ErrorReview + "\n\n" + CleanReview + "\n\n" + Epilogue, true)]
    public void A_review_the_last_message_did_not_carry_is_read_out_of_the_turn(
        string answer, string turn, bool substituted)
    {
        Ui.Run(async () =>
        {
            AnswerWithTurns(
                ("Which files?", null),
                (NoMoreQuestions, null),
                ("The plan", null),
                ("Implemented it", null),
                (answer, turn));

            using var vm = NewTile();
            await RunToSummary(vm);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Text.StartsWith("Goal completed"));
            Assert.Equal(substituted,
                vm.Messages.Any(m => m.Text.Contains("taken from an earlier message of the same run")));
        });
    }

    // ── A typed goal and the paths beside it ────────────

    /// <summary>A typed goal implements first — clarify runs — unless what it points at already holds the
    /// change; pointing at something is not pointing at work that is already there.</summary>
    [Theory]
    [InlineData("add Caddy support to the installer", null, true)]  // nothing pointed at
    [InlineData("add dark mode @docs/spec.md", "docs/spec.md", true)] // a real path the change is not in
    [InlineData("add dark mode @src/Frontend", "src/Frontend/", false)] // a clean tree
    public void A_typed_goal_that_points_at_no_existing_work_implements_first(
        string goal, string? existing, bool treeHasChanges)
    {
        Ui.Run(async () =>
        {
            if (existing is not null)
            {
                if (existing.EndsWith('/')) Directory.CreateDirectory(Path.Combine(Dir, existing));
                else WriteFile(existing, "what it should do");
            }
            if (!treeHasChanges) WorktreeReader.Factory = (_, _) => Task.FromResult<string?>(null);

            var prompts = Script(NoMoreQuestions, "The plan", "Implemented it", CleanReview);

            var vm = NewTile();
            vm.InputText = goal;
            await vm.RunCommand.ExecuteAsync(null);

            Assert.Contains(prompts, p => p.Contains("clarif", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.False(Saved(vm).ReviewsExistingWork,
                "nothing already there to judge — measuring from HEAD would make the user's own "
                + "uncommitted work this run's subject");
        });
    }

    /// <summary>A typed goal pointing at a changed path starts at the review, so it measures from HEAD —
    /// but its @ only narrowed, so its commit may not claim the whole tree.</summary>
    [Fact]
    public void A_typed_goal_pointing_at_a_path_does_not_claim_the_tree_for_its_commit()
    {
        Ui.Run(async () =>
        {
            // The stub tree's one changed file.
            WriteFile("x", "");

            var vm = NewTile();
            AnswerWith(CleanReview);

            vm.InputText = "add dark mode @x";
            await vm.RunCommand.ExecuteAsync(null);

            var state = Saved(vm);
            Assert.True(state.ReviewsExistingWork,
                "a run that starts at the review has to measure from HEAD, or it judges an empty diff");
            Assert.False(state.GoalReadFromTheTree,
                "the goal was typed and the @ only narrowed, so the commit may not claim the whole tree");
        });
    }

    /// <summary>And a typed goal pointing at work already there is still clarified and planned, and the
    /// plan reaches the run.</summary>
    [Fact]
    public void A_typed_goal_pointing_at_work_already_there_is_still_planned()
    {
        Ui.Run(async () =>
        {
            WriteFile("x", "");
            var prompts = Script(NoMoreQuestions, "Goal: add dark mode.\nSteps:\n1. x - paint it.", CleanReview);

            var vm = NewTile();
            vm.InputText = "add dark mode @x";
            await vm.RunCommand.ExecuteAsync(null);

            Assert.Contains(prompts, p => p.Contains("clarif", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(prompts, p => p.Contains("You are planning the implementation",
                StringComparison.Ordinal));

            var state = Saved(vm);
            Assert.Contains("paint it", state.ApprovedPlan);
            Assert.True(state.ReviewsExistingWork,
                "the pointer named work already on disk, so the loop still opens with a review");
        });
    }

    /// <summary>A goal actually read out of the tree still claims it.</summary>
    [Fact]
    public void A_goal_read_from_the_tree_claims_it()
    {
        Ui.Run(async () =>
        {
            var vm = NewTile();
            AnswerWith("Finish the cart", CleanReview);

            await vm.ReviewCommand.ExecuteAsync(null);

            Assert.True(Saved(vm).GoalReadFromTheTree);
        });
    }
}
