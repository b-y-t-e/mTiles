using Avalonia.Headless;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The implement/review loop driven end to end, with the AI replaced through
/// <see cref="GoalTileViewModel.AiRunnerFactory"/> — the same trick the launch-chain tests play with
/// <c>TerminalControl.PtyFactory</c>, and for the same reason: this loop is where every one of the last
/// dozen bugs landed, and each needed a real process and a real worktree to reach.
/// </summary>
[Collection(GoalSeamCollection.Name)]
public class GoalWorkflowLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-loop-" + Guid.NewGuid().ToString("N"));

    public GoalWorkflowLoopTests()
    {
        Directory.CreateDirectory(_dir);

        // No git anywhere near these. The temporary directory is not a repository, so the real reader
        // would spawn four processes a lap only to report that it could not read anything.
        //
        // A *different* tree on every read, which matters: the loop compares the tree an implementation
        // started from with the tree its review was handed, and stops when they are identical. A stub
        // answering the same string every time is a tool that never changes anything, so every run
        // ended after one attempt — the stop working exactly as intended, on a fixture that lied.
        var reads = 0;
        WorktreeReader.Factory = (_, _) =>
            Task.FromResult<string?>($"diff --git a/x b/x\n+ line {Interlocked.Increment(ref reads)}");
    }

    /// <summary>
    /// Runs the body on the headless UI thread. The view model dispatches to it — every message it adds
    /// and every state it saves — so a test that drove it from the test thread would deadlock waiting
    /// for a dispatcher nobody is pumping. The same helper the launch-chain tests use.
    /// </summary>
    private static void OnUiThread(Func<Task> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(GoalWorkflowLoopTests).Assembly);
        session.Dispatch(async () => { await body(); return true; }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        GoalTileViewModel.AiRunnerFactory = null;
        WorktreeReader.Factory = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* not a test failure */ }
    }

    /// <summary>
    /// What a clarification round comes back with when the tool has nothing left to ask.
    /// <para>Answering the questions no longer walks straight to the plan: the answer goes back for
    /// another round, and it is the <em>tool</em> that says when it has enough. Every script below
    /// therefore has one more step in it than it used to — which is the change, written down.</para>
    /// </summary>
    private const string NoMoreQuestions = "```json\n{\"needsClarification\":false}\n```";

    /// <summary>Answers each prompt in turn, and records how many times it was asked.</summary>
    private void AnswerWith(params string[] answers)
    {
        var asked = 0;
        GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            Task.FromResult<AiOutput>(answers[Math.Min(asked++, answers.Length - 1)]);
    }

    /// <summary>Waits for the tile's file to appear — messages are written on a debounce.</summary>
    private static void WaitForFile(GoalTileViewModel vm)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!File.Exists(vm.FilePath) && Environment.TickCount64 < deadline)
            Thread.Sleep(10);
    }

    private static UserAiTool FakeTool() => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = "Fake Tool",
        BinaryName = "fake-tool",
        CustomPath = typeof(GoalWorkflowLoopTests).Assembly.Location,
    };

    /// <summary>
    /// A tile with a tool it will agree to run.
    /// <para>The tool is a custom one pointing at a file that exists — this assembly — so detection
    /// finds it on any machine. Nothing ever launches it: <see cref="GoalTileViewModel.AiRunnerFactory"/>
    /// stands in front. Without it these tests would pass by doing nothing wherever no AI tool happens
    /// to be installed, which is most build agents.</para>
    /// </summary>
    private GoalTileViewModel NewTile()
    {
        var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
        settings.Settings.CustomAiTools.Add(FakeTool());

        var vm = new GoalTileViewModel(_dir, settings)
        {
            // No dialog would be shown in a headless run, and the tile refuses when nothing is wired.
            ConfirmAction = _ => Task.FromResult(true)
        };

        vm.SelectedToolName = "Fake Tool";
        Assert.Contains("Fake Tool", vm.AvailableTools);
        return vm;
    }

    [Fact]
    public void A_goal_runs_through_to_a_summary_when_the_review_passes()
    {
        OnUiThread(async () =>
        {
            AnswerWith("Which files?", NoMoreQuestions, "The plan", "Implemented it", "VERDICT: PASS");

            using var vm = NewTile();

            vm.InputText = "make the tile resumable";
            await vm.SubmitCommand.ExecuteAsync(null);          // Goal   → Clarify
            vm.InputText = "all of them";
            await vm.SubmitCommand.ExecuteAsync(null);          // Clarify → Plan
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);          // Plan   → implement/review

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);

            // A finished run is not a paused one. It used to arrive here still paused whenever the route in
            // was a pause, and then labelled itself "Paused. Click Resume" over a Resume with nothing to do.
            Assert.False(vm.IsPaused);
            Assert.DoesNotContain("Resume", vm.PhaseLabel);
            Assert.False(vm.IsRunning);
        });
    }

    [Fact]
    public void A_failing_review_is_re_implemented_until_the_budget_runs_out_and_says_so()
    {
        OnUiThread(async () =>
        {
            AnswerWith("Which files?", NoMoreQuestions, "The plan", "Implemented it", "VERDICT: FAIL — not yet");

            using var vm = NewTile();

            vm.InputText = "make it faster";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);

            // Not "goal completed": falling out of the loop is the budget running out, and it used to be
            // summarised as a success.
            Assert.Contains(vm.Messages, m => m.Text.Contains("without meeting the completion criteria"));
            Assert.DoesNotContain(vm.Messages, m => m.Text.StartsWith("Goal completed"));
        });
    }

    [Fact]
    public void An_attempt_that_changes_nothing_stops_the_run_instead_of_spending_the_budget()
    {
        OnUiThread(async () =>
        {
            // The tree never moves, which is what a tool that did nothing leaves behind. Four more
            // attempts against an unchanged worktree with an unchanged prompt get the same nothing.
            WorktreeReader.Factory = (_, _) => Task.FromResult<string?>("diff --git a/x b/x");

            AnswerWith("Which files?", NoMoreQuestions, "The plan", "Implemented it", "VERDICT: FAIL");

            using var vm = NewTile();

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Text.Contains("changed no files"));
        });
    }

    [Fact]
    public void Two_reviews_that_find_the_same_things_stop_the_run()
    {
        OnUiThread(async () =>
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

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Text.Contains("reached the same conclusion"));

            // And it stopped short of the budget rather than proving the point five times.
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("attempt 4"));

            // The no-progress stop is not a budget, so there is nothing for Continue to raise: more
            // attempts would find the same things a third time.
            Assert.False(vm.CanContinue);
        });
    }

    [Fact]
    public void A_structured_review_is_shown_as_findings_and_counted_in_the_badges()
    {
        OnUiThread(async () =>
        {
            AnswerWith("Which files?", NoMoreQuestions, "The plan", "Implemented it",
                "Looks mostly fine.\n\n```json\n{\"goalMet\":true,\"findings\":[" +
                "{\"severity\":\"suggestion\",\"file\":\"src/X.cs\",\"line\":4," +
                "\"title\":\"Rename this\",\"detail\":\"x is not a name.\"}]}\n```");

            using var vm = NewTile();

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            // A suggestion never blocks, so the goal is met on the first attempt.
            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Text.StartsWith("Goal completed"));

            // The review is rendered, not reprinted: the finding is there, the prose around the block
            // is not. Kept as a finding rather than flattened into the text, which is what lets the
            // transcript draw it as a row with its severity in colour.
            Assert.Contains(vm.Messages, m => m.Findings.Any(
                f => f.Severity == GoalSeverity.Suggestion && f.File == "src/X.cs" && f.Line == 4
                     && f.Title == "Rename this"));
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("Looks mostly fine"));

            Assert.Equal(["1S"], vm.Badges.Select(b => b.Text));
        });
    }

    [Fact]
    public void The_questions_stop_after_the_round_budget_and_the_tile_plans_anyway()
    {
        OnUiThread(async () =>
        {
            // A tool that always finds one more thing to ask. Without the budget the user answers for
            // ever; with it, the tile plans with what it has and the plan can still be rejected.
            AnswerWith("And another thing?");

            using var vm = NewTile();

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            for (var i = 0; i < GoalWorkflowEngine.MaxClarifyRounds; i++)
            {
                vm.InputText = "answered";
                await vm.SubmitCommand.ExecuteAsync(null);
            }

            Assert.Equal(GoalPhase.Plan, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Text.Contains("rounds of questions"));
        });
    }

    [Fact]
    public void The_rounds_already_spent_survive_a_restart()
    {
        OnUiThread(async () =>
        {
            var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
            settings.Settings.CustomAiTools.Add(FakeTool());

            AnswerWith("And another thing?");

            var first = new GoalTileViewModel(_dir, settings) { ConfirmAction = _ => Task.FromResult(true) };
            first.SelectedToolName = "Fake Tool";
            var path = first.FilePath;

            first.InputText = "a goal";
            await first.SubmitCommand.ExecuteAsync(null);
            first.InputText = "answered";
            await first.SubmitCommand.ExecuteAsync(null);
            first.Dispose();

            // Otherwise closing the tile renews the budget, and a tool that keeps asking keeps asking.
            using var second = new GoalTileViewModel(path, _dir, settings) { ConfirmAction = _ => Task.FromResult(true) };

            second.InputText = "answered again";
            await second.SubmitCommand.ExecuteAsync(null);
            second.InputText = "and again";
            await second.SubmitCommand.ExecuteAsync(null);

            Assert.Equal(GoalPhase.Plan, second.CurrentPhase);
        });
    }

    [Fact]
    public void A_plan_the_tile_started_by_itself_comes_back_paused_when_it_is_interrupted()
    {
        OnUiThread(async () =>
        {
            var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
            settings.Settings.CustomAiTools.Add(FakeTool());

            var first = new GoalTileViewModel(_dir, settings) { ConfirmAction = _ => Task.FromResult(true) };
            first.SelectedToolName = "Fake Tool";
            var path = first.FilePath;

            // Clarify decides on its own that it has nothing left to ask, so the tile moves to Plan by
            // itself and leaves a note of its own last in the transcript. The plan run is then cut off.
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                if (asked == 3) first.PauseCommand.Execute(null);
                return Task.FromResult<AiOutput>(asked switch { 1 => "Which files?", 2 => NoMoreQuestions, _ => "The plan" });
            };

            first.InputText = "a goal";
            await first.SubmitCommand.ExecuteAsync(null);
            first.InputText = "all of it";
            await first.SubmitCommand.ExecuteAsync(null);
            first.Dispose();

            using var second = new GoalTileViewModel(path, _dir, settings) { ConfirmAction = _ => Task.FromResult(true) };

            // The old rule asked whether the *user's* message was last, which a tile-written note is
            // not — so this came back unpaused, in Plan, with no plan in it and no Resume to get one.
            Assert.Equal(GoalPhase.Plan, second.CurrentPhase);
            Assert.True(second.IsPaused);
            Assert.Contains("Resume", second.PhaseLabel);
        });
    }

    [Fact]
    public void The_questions_go_into_the_history_the_next_round_and_the_plan_read()
    {
        OnUiThread(async () =>
        {
            var prompts = new List<string>();
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, prompt, _, _) =>
            {
                prompts.Add(prompt);
                asked++;
                return Task.FromResult<AiOutput>(asked == 1
                    ? "```json\n{\"questions\":[{\"question\":\"Which config file holds the port?\"}]}\n```"
                    : NoMoreQuestions);
            };

            using var vm = NewTile();

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "1. appsettings.json";
            await vm.SubmitCommand.ExecuteAsync(null);

            // The second round is handed the answer *and* the question. Without the question it was
            // handed "1. appsettings.json" and no way of knowing what question 1 had been — which makes
            // the numbering, whose whole job is to tie the two together, worse than useless.
            Assert.Contains("Which config file holds the port?", prompts[1]);
            Assert.Contains("appsettings.json", prompts[1]);

            // And so is the plan.
            Assert.Contains("Which config file holds the port?", prompts[^1]);
        });
    }

    [Fact]
    public void Sending_back_only_the_numbering_does_not_spend_a_round()
    {
        OnUiThread(async () =>
        {
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                return Task.FromResult<AiOutput>(
                    "```json\n{\"questions\":[{\"question\":\"Which file?\"},{\"question\":\"Sync?\"}]}\n```");
            };

            using var vm = NewTile();

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            // The composer used to be filled with the numbering and pressing Enter sent it — one of
            // three rounds spent on "1.\n2.". The questions now have a box each and the composer is
            // not even up, so there is nothing to send by accident.
            Assert.Equal(2, vm.Questions.Count);
            Assert.True(vm.ShowQuestions);
            Assert.False(vm.ShowComposer);
            Assert.Equal("", vm.InputText);

            // The rule the numbering test was really about, asked where it now lives: sending with
            // every box empty spends no round.
            await vm.SendAnswersCommand.ExecuteAsync(null);

            Assert.Equal(1, asked);
            Assert.Contains(vm.Messages, m => m.Text.Contains("Answer at least one"));
            Assert.DoesNotContain(vm.Messages, m => m.Role == GoalMessageRole.User && m.Text.StartsWith("1."));

            // And the questions are still there to answer, rather than having been spent.
            Assert.Equal(2, vm.Questions.Count);
        });
    }

    [Fact]
    public void A_detection_that_finds_nothing_leaves_the_session_alone()
    {
        OnUiThread(async () =>
        {
            AnswerWith("Which files?");

            using var vm = NewTile();
            vm.InputText = "a goal worth keeping";
            await vm.SubmitCommand.ExecuteAsync(null);

            // git status said there was something; by the time the prompt is built there is not — a
            // commit in between, or the two commands disagreeing. Clearing first meant the user paid
            // for that with their session.
            WorktreeReader.Factory = (_, _) => Task.FromResult<string?>(null);

            await vm.DetectGoalCommand.ExecuteAsync(null);

            Assert.Contains(vm.Messages, m => m.Text.Contains("a goal worth keeping"));
            Assert.Contains(vm.Messages, m => m.Text.Contains("no uncommitted changes"));
        });
    }

    [Fact]
    public void The_completion_criteria_survive_a_restart()
    {
        OnUiThread(async () =>
        {
            var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
            settings.Settings.CustomAiTools.Add(FakeTool());

            AnswerWith("Which files?");

            var first = new GoalTileViewModel(_dir, settings) { ConfirmAction = _ => Task.FromResult(true) };
            first.SelectedToolName = "Fake Tool";
            var path = first.FilePath;

            first.InputText = "a goal";
            await first.SubmitCommand.ExecuteAsync(null);

            first.Criteria.MaxIterations = 9;
            first.Criteria.MaxWarnings = 2;
            first.Criteria.RequireGoalMet = false;
            first.Criteria.RequireTestsPass = false;
            first.Dispose();

            using var second = new GoalTileViewModel(path, _dir, settings) { ConfirmAction = _ => Task.FromResult(true) };

            Assert.Equal(9, second.Criteria.MaxIterations);
            Assert.Equal(2, second.Criteria.MaxWarnings);
            Assert.False(second.Criteria.RequireGoalMet);

            // A switch turned off stays off, and the one beside it stays on: both are written, so a
            // default reappearing would be the file quietly disagreeing with the panel.
            Assert.False(second.Criteria.RequireTestsPass);
            Assert.True(second.Criteria.RequireBuild);
        });
    }

    [Fact]
    public void An_answer_that_is_only_the_numbering_does_not_spend_the_pause_either()
    {
        OnUiThread(async () =>
        {
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                // Nothing at all, which pauses the tile in Clarify with the questions on screen.
                return Task.FromResult<AiOutput>(asked == 1
                    ? "```json\n{\"questions\":[{\"question\":\"Which file?\"}]}\n```"
                    : "   ");
            };

            using var vm = NewTile();
            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "an answer";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.True(vm.IsPaused);

            // Enter on the prefilled numbering. The pause is cleared at the top of Submit for anything
            // that is going to start a run — and this starts nothing, so clearing it left a stopped
            // tile unpaused with nothing running: no Resume, no run, and the only way on an answer
            // nobody had asked for.
            vm.InputText = "1. ";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.True(vm.IsPaused);
            Assert.False(vm.IsRunning);
            Assert.Contains("Resume", vm.PhaseLabel);
            // Handed back rather than swallowed — trimmed, as every guard in Submit hands text back.
            Assert.Equal("1.", vm.InputText);
        });
    }

    [Fact]
    public void What_the_tool_said_on_its_way_past_is_kept()
    {
        OnUiThread(async () =>
        {
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                return Task.FromResult<AiOutput>(asked == 1
                    ? "The goal is clear; I am assuming the API stays as it is.\n\n" +
                      "```json\n{\"needsClarification\":false}\n```"
                    : "The plan");
            };

            using var vm = NewTile();
            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            // A round that decides the goal is clear often says what it is assuming, and that is the
            // last chance to disagree before a plan is written against it.
            Assert.Contains(vm.Messages, m => m.Text.Contains("assuming the API stays as it is"));
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("needsClarification"));
            Assert.Equal(GoalPhase.Plan, vm.CurrentPhase);
        });
    }

    [Fact]
    public void A_round_that_asks_nothing_goes_straight_to_the_plan()
    {
        OnUiThread(async () =>
        {
            var prompts = new List<string>();
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, prompt, _, _) =>
            {
                prompts.Add(prompt);
                return Task.FromResult<AiOutput>(++asked == 1
                    ? "```json\n{\"needsClarification\":true,\"questions\":[]}\n```"
                    : "The plan");
            };

            using var vm = NewTile();
            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            // Not a tile waiting for an answer to nothing, and no raw JSON anywhere near the transcript
            // or the prompt that reads it back.
            Assert.Equal(GoalPhase.Plan, vm.CurrentPhase);
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("needsClarification"));
            Assert.DoesNotContain(prompts[^1], "needsClarification");
        });
    }

    [Fact]
    public void The_detect_buttons_are_offered_only_where_a_goal_is_what_is_wanted_next()
    {
        OnUiThread(async () =>
        {
            AnswerWith("Which files?");

            using var vm = NewTile();
            Assert.False(vm.CanDetectGoal);          // nothing uncommitted has been reported yet

            vm.HasUncommittedChanges = true;
            Assert.True(vm.CanDetectGoal);

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            // Mid-conversation the buttons would offer to throw the conversation away.
            Assert.Equal(GoalPhase.Clarify, vm.CurrentPhase);
            Assert.False(vm.CanDetectGoal);
        });
    }

    /// <summary>
    /// A pause taken from inside the implementation's own answer, which is the moment the phase moves
    /// on — and the two things that were both wrong about it.
    /// <para>What is owed then is the review, and resuming used to run the whole implementation again,
    /// against a worktree that already had its changes. And the *next* phase finds the pause already
    /// standing and returns before launching anything, so there is no cancelled run for
    /// HandleNonAnswerAsync to describe and nothing else writes the label — the strip went on saying
    /// "AI is implementing (attempt 1/5)…" over a tile that had stopped.</para>
    /// </summary>
    [Fact]
    public void A_pause_taken_after_the_implementation_resumes_at_the_review_and_stops_saying_it_is_working()
    {
        OnUiThread(async () =>
        {
            using var vm = NewTile();

            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                if (asked == 4) vm.PauseCommand.Execute(null);
                return Task.FromResult<AiOutput>(asked switch
                {
                    1 => "Which files?",
                    2 => NoMoreQuestions,
                    3 => "The plan",
                    _ => "Implemented it",
                });
            };

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.True(vm.IsPaused);
            Assert.False(vm.IsRunning);

            // What is owed is the review, not the implementation over again.
            Assert.Equal(GoalPhase.Review, vm.CurrentPhase);
            Assert.True(GoalTilePolicy.ResumesAtReview(vm.CurrentPhase));

            // And the tile says so rather than claiming to be working.
            Assert.DoesNotContain("implementing", vm.PhaseLabel);
            Assert.DoesNotContain("reviewing", vm.PhaseLabel);
            Assert.Contains("Resume", vm.PhaseLabel);
        });
    }

    [Fact]
    public void A_detection_over_a_tree_nobody_could_read_never_reaches_the_tool()
    {
        OnUiThread(async () =>
        {
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                return Task.FromResult<AiOutput>("some goal");
            };

            // What the real reader produces where git cannot be run: not null — a note saying so. The
            // tool was being handed a working tree consisting of an apology and asked what it was for.
            WorktreeReader.Factory = null;

            using var vm = NewTile();
            await vm.DetectGoalCommand.ExecuteAsync(null);

            Assert.Equal(0, asked);
            Assert.Contains(vm.Messages, m => m.Text.Contains("could not be read"));
        });
    }

    [Fact]
    public void A_failed_detection_does_not_point_at_a_button_that_cannot_help()
    {
        OnUiThread(async () =>
        {
            using var vm = NewTile();

            // Detection runs from the Goal phase, where Resume does nothing at all — ResumeAsync's own
            // default case only writes the cleared pause. Every "click Resume to try again" printed
            // here pointed at a button that was either absent or inert.
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
        OnUiThread(async () =>
        {
            using var vm = NewTile();

            // Thrown from outside the AI call, which RunAiAsync catches — this one comes out of the
            // tree read, where nothing did. The button appeared to do nothing at all, which is the one
            // outcome a button must never have.
            WorktreeReader.Factory = (_, _) => throw new InvalidOperationException("git is on fire");

            await vm.DetectGoalCommand.ExecuteAsync(null);

            Assert.Contains(vm.Messages, m => m.Text.Contains("git is on fire"));
        });
    }

    /// <summary>Drives a whole run: the three answers before the loop, then one warning per review with
    /// a title that moves, so the reviews never look identical and the no-progress stop stays out of the
    /// way. Counts the implementations, which is what a budget is a budget of.</summary>
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

    private static async Task RunToSummaryAsync(GoalTileViewModel vm)
    {
        vm.InputText = "a goal";
        await vm.SubmitCommand.ExecuteAsync(null);
        vm.InputText = "all of it";
        await vm.SubmitCommand.ExecuteAsync(null);
        vm.InputText = "ok";
        await vm.SubmitCommand.ExecuteAsync(null);
    }

    [Fact]
    public void A_run_that_ran_out_of_attempts_can_be_given_more_without_losing_the_conversation()
    {
        OnUiThread(async () =>
        {
            var implemented = LoopAnsweringWithOneWarning();

            using var vm = NewTile();
            vm.Criteria.MaxIterations = 2;

            await RunToSummaryAsync(vm);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Equal(2, implemented());
            Assert.True(vm.CanContinue);

            // Here the attempts really did run out, so the button names what it will add.
            Assert.Equal("Continue · +2", vm.ContinueLabel);

            // The summary names what stood in the way. Without it the choice this button exists for —
            // more attempts, or a tolerance that admits what the reviewer keeps finding — has to be made
            // by reading back through the transcript.
            Assert.Contains(vm.Messages, m => m.Text.Contains("1 warning"));

            var before = vm.Messages.Count;
            await vm.ContinueRunCommand.ExecuteAsync(null);

            // Two more attempts, the ceiling in the panel telling the truth, and the conversation kept —
            // which is the whole point: everything this session worked out about the goal is in it.
            Assert.Equal(4, implemented());
            Assert.Equal(4, vm.Criteria.MaxIterations);
            Assert.True(vm.Messages.Count > before);
            Assert.Contains(vm.Messages, m => m.Text == "a goal");
        });
    }

    /// <summary>
    /// What the next goal in the same tile starts from, after Continue has raised the budget — in both
    /// branches, because the difference between them is one line of user behaviour.
    /// <para>Criteria deliberately outlive a goal: they are how the tile works. That is right for a
    /// number the user typed and wrong for one the button wrote, and two continuations left the next
    /// goal starting at eight. So the tile remembers what the user chose and puts it back — unless the
    /// user has moved the field since, which makes the number theirs again; without clearing what was
    /// remembered, the next goal started from the old 2 rather than the 8 just typed.</para>
    /// </summary>
    [Fact]
    public void The_attempts_Continue_adds_belong_to_that_goal_and_not_to_the_tile()
    {
        // Both branches share every line up to the Continue, so the run is written once and the two
        // endings are the only thing that differs. int? retyped: the number the user puts in the field
        // after pressing Continue, or nothing at all.
        void Run(int? retyped, int expected) => OnUiThread(async () =>
        {
            var implemented = LoopAnsweringWithOneWarning();

            using var vm = NewTile();
            vm.Criteria.MaxIterations = 2;

            await RunToSummaryAsync(vm);
            Assert.True(vm.CanContinue);

            await vm.ContinueRunCommand.ExecuteAsync(null);
            Assert.Equal(4, vm.Criteria.MaxIterations);
            Assert.Equal(4, implemented());

            if (retyped is { } typed) vm.Criteria.MaxIterations = typed;

            vm.InputText = "a different goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.Equal(expected, vm.Criteria.MaxIterations);
        });

        // Untouched since: the next goal gets the number the user chose, not the one the button wrote.
        Run(retyped: null, expected: 2);

        // Moved by hand: that number is theirs, and it is what the next goal starts from.
        Run(retyped: 8, expected: 8);
    }

    [Fact]
    public void A_pause_taken_between_two_answered_phases_does_not_start_the_next_one()
    {
        OnUiThread(async () =>
        {
            using var vm = NewTile();

            // Paused from inside the clarification answer, so the pause is already standing when the
            // plan would be asked for. Nothing between two phases used to ask, so the run the user had
            // just stopped started again one line later.
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                if (asked == 2) vm.PauseCommand.Execute(null);
                return Task.FromResult<AiOutput>(asked == 1 ? "Which files?" : NoMoreQuestions);
            };

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);

            // Two runs: the first clarification and the one that answered "no more questions". The plan
            // is the third, and it must not have happened.
            Assert.Equal(2, asked);
            Assert.True(vm.IsPaused);
            Assert.DoesNotContain("creating a plan", vm.PhaseLabel);
            Assert.Contains("Resume", vm.PhaseLabel);
        });
    }

    [Fact]
    public void A_workspace_git_cannot_read_does_not_end_every_goal_after_one_attempt()
    {
        OnUiThread(async () =>
        {
            // The real reader against a directory that is not a repository — the fixture stub cannot
            // reproduce this, because a stub always answers successfully.
            WorktreeReader.Factory = null;

            AnswerWith("Which files?", NoMoreQuestions, "The plan", "Implemented it", "VERDICT: FAIL");

            using var vm = NewTile();
            vm.Criteria.MaxIterations = 2;

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            // It ran out of attempts, which is the truth. It used to stop after one and say the
            // implementation had changed nothing — in a workspace where nobody could see whether it had.
            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("changed no files"));
            Assert.Contains(vm.Messages, m => m.Text.Contains("without meeting the completion criteria"));
        });
    }

    [Fact]
    public void The_tiles_own_notes_do_not_make_a_waiting_tile_look_interrupted()
    {
        OnUiThread(async () =>
        {
            var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
            settings.Settings.CustomAiTools.Add(FakeTool());

            AnswerWith("```json\n{\"questions\":[{\"question\":\"Which file?\"}]}\n```");

            var first = new GoalTileViewModel(_dir, settings) { ConfirmAction = _ => Task.FromResult(true) };
            first.SelectedToolName = "Fake Tool";
            var path = first.FilePath;

            first.InputText = "a goal";
            await first.SubmitCommand.ExecuteAsync(null);

            // The tile's own aside, last in the transcript. LoadState writes ones like it on every load,
            // so counting them meant each restart appended a note, each note made the next restart read
            // an interrupted Clarify, and Resume spent a round on it — a tile left alone long enough
            // talked itself out of its own budget.
            first.InputText = "1.";
            await first.SubmitCommand.ExecuteAsync(null);
            Assert.Equal(GoalMessageRole.System, first.Messages[^1].Role);
            first.Dispose();

            using var second = new GoalTileViewModel(path, _dir, settings) { ConfirmAction = _ => Task.FromResult(true) };

            Assert.Equal(GoalPhase.Clarify, second.CurrentPhase);
            Assert.False(second.IsPaused);
        });
    }

    [Fact]
    public void The_badges_come_back_with_the_tile()
    {
        OnUiThread(async () =>
        {
            var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
            settings.Settings.CustomAiTools.Add(FakeTool());

            var review = "```json\n{\"goalMet\":false,\"findings\":[" +
                         "{\"severity\":\"blocker\",\"title\":\"Unacceptable\"}," +
                         "{\"severity\":\"suggestion\",\"title\":\"Rename\"}]}\n```";

            var first = new GoalTileViewModel(_dir, settings) { ConfirmAction = _ => Task.FromResult(true) };
            first.SelectedToolName = "Fake Tool";
            first.Criteria.MaxIterations = 1;
            var path = first.FilePath;

            AnswerWith("Which files?", NoMoreQuestions, "The plan", "Implemented it", review);

            first.InputText = "a goal";
            await first.SubmitCommand.ExecuteAsync(null);
            first.InputText = "all of it";
            await first.SubmitCommand.ExecuteAsync(null);
            first.InputText = "ok";
            await first.SubmitCommand.ExecuteAsync(null);

            Assert.Equal(["1B", "1S"], first.Badges.Select(b => b.Text));

            // The count is a question, and the badge carries its own answer: pressing it shows that
            // severity's findings and no other's.
            Assert.Equal(["Unacceptable"], Titles(first, GoalSeverity.Blocker));
            Assert.Equal(["Rename"], Titles(first, GoalSeverity.Suggestion));
            Assert.All(first.Badges, b => Assert.True(b.HasFindings));

            // And pressing one opens the dialog on that badge, not on the strip as a whole.
            Assert.False(first.IsShowingFindings);
            first.OpenFindingsCommand.Execute(first.Badges.Single(b => b.IsBlocker));
            Assert.True(first.IsShowingFindings);
            Assert.Equal(["Unacceptable"], first.OpenBadge!.Findings.Select(f => f.Title));
            first.CloseFindingsCommand.Execute(null);
            Assert.False(first.IsShowingFindings);

            first.Dispose();

            using var second = new GoalTileViewModel(path, _dir, settings) { ConfirmAction = _ => Task.FromResult(true) };

            // The review it summarises is still in the transcript; the strip used to go blank anyway.
            // Only the severities that found something appear — a clean review shows nothing at all
            // rather than four zeroes.
            Assert.Equal(["1B", "1S"], second.Badges.Select(b => b.Text));
            Assert.DoesNotContain(second.Badges, b => b.Severity == GoalSeverity.Error);

            // And so does what they open. The counts are saved; the findings are not saved with them,
            // so a restored badge has to take them from the review still standing in the transcript.
            Assert.Equal(["Unacceptable"], Titles(second, GoalSeverity.Blocker));
            Assert.Equal(["Rename"], Titles(second, GoalSeverity.Suggestion));
        });
    }

    /// <summary>What one badge's popup would list.</summary>
    private static IEnumerable<string> Titles(GoalTileViewModel vm, GoalSeverity severity) =>
        vm.Badges.Single(b => b.Severity == severity).Findings.Select(f => f.Title);

    [Fact]
    public void A_badge_with_nothing_behind_it_does_not_open()
    {
        OnUiThread(async () =>
        {
            var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
            settings.Settings.CustomAiTools.Add(FakeTool());

            // Prose, not JSON: parsed as unstructured, so the review is counted but has no findings —
            // which is also the shape of a goal file written before findings were kept.
            var vm = new GoalTileViewModel(_dir, settings) { ConfirmAction = _ => Task.FromResult(true) };
            vm.SelectedToolName = "Fake Tool";
            vm.Criteria.MaxIterations = 1;

            AnswerWith("Which files?", NoMoreQuestions, "The plan", "Implemented it",
                       "This is not done yet.");

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            // An unstructured review is counted as nothing, so there is no badge to press at all.
            Assert.Empty(vm.Badges);

            // And the badge that can exist without findings — one restored from a goal file written
            // before they were kept — refuses to open rather than showing an empty dialog. The markup
            // also makes it unpressable; this is the half a command cannot be talked out of.
            vm.OpenFindingsCommand.Execute(
                new GoalBadge { Severity = GoalSeverity.Error, Count = 2 });
            Assert.False(vm.IsShowingFindings);

            vm.OpenFindingsCommand.Execute(null);
            Assert.False(vm.IsShowingFindings);
            vm.Dispose();
        });
    }

    [Fact]
    public void A_detection_whose_tool_fails_leaves_the_session_alone()
    {
        OnUiThread(async () =>
        {
            AnswerWith("Which files?");

            using var vm = NewTile();
            vm.InputText = "a goal worth keeping";
            await vm.SubmitCommand.ExecuteAsync(null);

            // The tool answers nothing. Clearing the transcript before running it made that — the
            // ordinary outcome of a flaky CLI, not the unlucky one — into an empty tile where a
            // session used to be.
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) => Task.FromResult<AiOutput>("   ");

            await vm.DetectGoalCommand.ExecuteAsync(null);

            Assert.Contains(vm.Messages, m => m.Text.Contains("a goal worth keeping"));
            Assert.Equal("", vm.InputText);
        });
    }

    [Fact]
    public void Approving_a_new_plan_forgets_the_old_plans_reviews()
    {
        OnUiThread(async () =>
        {
            const string sameFinding =
                "```json\n{\"goalMet\":false,\"findings\":[" +
                "{\"severity\":\"error\",\"file\":\"a.cs\",\"title\":\"Still wrong\"}]}\n```";

            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                return Task.FromResult<AiOutput>(asked switch
                {
                    1 => "Which files?",
                    2 => NoMoreQuestions,
                    3 => "Plan A",
                    4 => "Implemented A",
                    5 => sameFinding,            // review of plan A
                    6 => NoMoreQuestions,        // the rejection goes back through clarify
                    7 => "Plan B",
                    8 => "Implemented B",
                    _ => sameFinding,            // review of plan B: the same defect
                });
            };

            using var vm = NewTile();
            vm.Criteria.MaxIterations = 1;

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);       // plan A runs its single attempt

            vm.InputText = "no, do it differently";
            await vm.SubmitCommand.ExecuteAsync(null);       // → plan B
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            // A rejected plan and its replacement are usually about the same defect, so the first
            // review of plan B matches the last review of plan A. With the fingerprint left standing
            // the run ended after a single attempt, reporting that two reviews had agreed when only
            // one of them belonged to this plan.
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("reached the same conclusion"));
        });
    }

    [Fact]
    public void Detecting_a_goal_puts_it_in_the_composer_for_the_user_to_edit()
    {
        OnUiThread(async () =>
        {
            AnswerWith("Finish the pairing flow so a paired device survives a restart.");

            using var vm = NewTile();

            await vm.DetectGoalCommand.ExecuteAsync(null);

            // A draft, not a decision. It is the tool's reading of half-finished work, and the user is
            // the only one who knows what the other half was meant to be.
            Assert.Contains("survives a restart", vm.InputText);
            Assert.Equal(GoalPhase.Goal, vm.CurrentPhase);
            Assert.DoesNotContain(vm.Messages, m => m.Role == GoalMessageRole.User);
        });
    }

    [Fact]
    public void A_detection_does_not_delete_what_the_user_typed_while_it_was_running()
    {
        OnUiThread(async () =>
        {
            using var vm = NewTile();

            // The composer stays editable while the tile works — only Send is disabled — so the
            // window between the click and the answer is one the user can type into. Typed from inside
            // the runner, which is exactly where it happens in the application.
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                vm.InputText = "what I was writing";
                return Task.FromResult<AiOutput>("Finish the pairing flow.");
            };

            await vm.DetectGoalCommand.ExecuteAsync(null);

            Assert.Equal("what I was writing", vm.InputText);

            // Kept, but not swallowed: the click has to produce something readable, or it did nothing.
            Assert.Contains(vm.Messages, m => m.Text.Contains("Finish the pairing flow."));
        });
    }

    [Fact]
    public void A_detection_over_a_composer_the_user_had_already_filled_keeps_it()
    {
        OnUiThread(async () =>
        {
            AnswerWith("Finish the pairing flow.");

            using var vm = NewTile();

            // Text that was there before the click is the user's too, which is why the rule asks about
            // the box as it is now rather than comparing it with a snapshot taken at the click.
            vm.InputText = "half a goal I started typing";

            await vm.DetectGoalCommand.ExecuteAsync(null);

            Assert.Equal("half a goal I started typing", vm.InputText);
            Assert.Contains(vm.Messages, m => m.Text.Contains("Finish the pairing flow."));
        });
    }

    [Fact]
    public void Answers_go_back_numbered_and_the_questions_join_the_transcript_with_them()
    {
        OnUiThread(async () =>
        {
            var prompts = new List<string>();
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, prompt, _, _) =>
            {
                prompts.Add(prompt);
                return Task.FromResult<AiOutput>(++asked == 1
                    ? """{"questions":[{"question":"Which file?"},{"question":"Sync or async?"}]}"""
                    : NoMoreQuestions);
            };

            using var vm = NewTile();
            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            vm.Questions[0].Answer = "appsettings.json";
            vm.Questions[1].Answer = "async";
            await vm.SendAnswersCommand.ExecuteAsync(null);

            // Filed against their numbers, which is what the next prompt reads them back by.
            Assert.Contains("1. appsettings.json", prompts[1]);
            Assert.Contains("2. async", prompts[1]);

            // The questions reach the transcript when they are answered, not when they are asked —
            // so the record still reads question then answer, without a second copy of the panel
            // sitting above the conversation while it is being filled in.
            var questionsAt = vm.Messages.ToList().FindIndex(m => m.Text.Contains("Sync or async?"));
            var answersAt = vm.Messages.ToList().FindIndex(m => m.Text.Contains("2. async"));
            Assert.True(questionsAt >= 0 && answersAt == questionsAt + 1);

            // And the panel is gone, because there is nothing left to answer: the tool said it had no
            // more questions and the tile went on to the plan, which is the next thing it asks about.
            Assert.Empty(vm.Questions);
            Assert.False(vm.ShowQuestions);
        });
    }

    [Fact]
    public void An_unanswered_question_is_left_out_rather_than_sent_empty()
    {
        OnUiThread(async () =>
        {
            var prompts = new List<string>();
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, prompt, _, _) =>
            {
                prompts.Add(prompt);
                return Task.FromResult<AiOutput>(++asked == 1
                    ? """{"questions":[{"question":"Which file?"},{"question":"Sync or async?"}]}"""
                    : NoMoreQuestions);
            };

            using var vm = NewTile();
            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            vm.Questions[1].Answer = "async";
            await vm.SendAnswersCommand.ExecuteAsync(null);

            // A blank line under a number says "none of your business" to a model that cannot tell it
            // from a question that was skipped, and the round after it asks the same thing again.
            Assert.Contains("2. async", prompts[1]);
            Assert.DoesNotContain("1. \n", prompts[1]);
        });
    }

    [Fact]
    public void Questions_come_back_with_the_tile_and_do_not_look_like_an_interrupted_run()
    {
        OnUiThread(async () =>
        {
            var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
            settings.Settings.CustomAiTools.Add(FakeTool());

            AnswerWith("""
                {"questions":[{"question":"Which file?","why":"Two candidates.",
                  "options":["appsettings.json","launchSettings.json"]}]}
                """);

            var first = new GoalTileViewModel(_dir, settings) { ConfirmAction = _ => Task.FromResult(true) };
            first.SelectedToolName = "Fake Tool";
            var path = first.FilePath;
            first.InputText = "a goal";
            await first.SubmitCommand.ExecuteAsync(null);
            Assert.Single(first.Questions);
            first.Dispose();

            using var second = new GoalTileViewModel(path, _dir, settings) { ConfirmAction = _ => Task.FromResult(true) };

            // Persisted, because a panel built from a parsed answer would not survive the tile being
            // closed — and the goal would come back waiting for questions nobody could see.
            var question = Assert.Single(second.Questions);
            Assert.Equal("Which file?", question.Question);
            Assert.Equal("Two candidates.", question.Why);
            Assert.Equal(2, question.Options.Count);

            // And a tile waiting on the user is not a run that was cut off. That used to be read as
            // "did the tool speak last", which stopped being true the moment the questions left the
            // transcript — every restart then offered Resume, which asks the same round again.
            Assert.False(second.IsPaused);
        });
    }

    [Fact]
    public void A_fresh_round_of_questions_replaces_the_one_on_screen()
    {
        OnUiThread(async () =>
        {
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) => Task.FromResult<AiOutput>(++asked == 1
                ? """{"questions":[{"question":"Which file?"}]}"""
                : """{"questions":[{"question":"Which port?"},{"question":"Which host?"}]}""");

            using var vm = NewTile();
            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            vm.Questions[0].Answer = "appsettings.json";
            await vm.SendAnswersCommand.ExecuteAsync(null);

            // Replaced rather than added to. An answer typed against "Which file?" has nowhere to go
            // once the tool has moved on to ports and hosts.
            Assert.Equal(2, vm.Questions.Count);
            Assert.Equal("Which port?", vm.Questions[0].Question);
            Assert.All(vm.Questions, q => Assert.Equal("", q.Answer));
        });
    }

    [Fact]
    public void The_plan_is_approved_by_a_button_and_changed_by_typing_into_the_same_box()
    {
        OnUiThread(async () =>
        {
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) => Task.FromResult<AiOutput>(++asked switch
            {
                1 => NoMoreQuestions,
                2 => "The plan",
                3 => "Implemented it",
                _ => "VERDICT: PASS",
            });

            using var vm = NewTile();
            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            // The composer gives way to the approval panel, and its one button says what an empty box
            // will do.
            Assert.True(vm.ShowApproval);
            Assert.False(vm.ShowComposer);
            Assert.Equal("Approve plan", vm.ApprovalActionLabel);

            // Typing turns it into the other thing, rather than leaving a button that would send an
            // approval over the top of a correction — or throw the correction away.
            vm.InputText = "no, do it differently";
            Assert.Equal("Send changes", vm.ApprovalActionLabel);

            vm.InputText = "";
            await vm.ApproveOrChangeCommand.ExecuteAsync(null);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Role == GoalMessageRole.User && m.Text == "ok");
        });
    }

    [Fact]
    public void Prose_questions_keep_the_composer_because_there_is_no_panel_to_build()
    {
        OnUiThread(async () =>
        {
            AnswerWith("Which file holds the port, and should it be async?");

            using var vm = NewTile();
            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            // A tool that ignored the schema still asked something, and the behaviour this tile always
            // had is exactly right for it: a message, and the composer to answer it in.
            Assert.Empty(vm.Questions);
            Assert.True(vm.ShowComposer);
            Assert.Contains(vm.Messages, m => m.Text.Contains("holds the port"));
        });
    }

    [Fact]
    public void Nothing_can_be_typed_at_a_tile_that_is_working()
    {
        OnUiThread(async () =>
        {
            var gate = new TaskCompletionSource<AiOutput>();
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) => gate.Task;

            using var vm = NewTile();
            vm.InputText = "a goal";
            var running = vm.SubmitCommand.ExecuteAsync(null);

            // Submit returns while IsRunning, so a composer shown here is a box that takes text and
            // does nothing with it — and the one thing it did do, silently, was hold text that a
            // finishing detection then wrote over.
            Assert.True(vm.IsRunning);
            Assert.False(vm.ShowComposer);
            Assert.False(vm.ShowQuestions);
            Assert.False(vm.ShowApproval);

            gate.SetResult(NoMoreQuestions);
            await running;
        });
    }

    [Fact]
    public void An_offered_answer_fills_the_box_without_deleting_what_was_typed_in_it()
    {
        var q = new GoalQuestionAnswer(1, new GoalQuestion
        {
            Question = "Which file?",
            Options = ["appsettings.json", "launchSettings.json"],
        });

        // Empty: it is the answer.
        q.Options[0].Use.Execute(null);
        Assert.Equal("appsettings.json", q.Answer);

        // Already an option: changing your mind between two offers should not need a selection first.
        q.Options[1].Use.Execute(null);
        Assert.Equal("launchSettings.json", q.Answer);

        // Typed: appended. A suggestion that deletes the sentence somebody wrote is not a suggestion.
        q.Answer = "neither, use the environment";
        q.Options[0].Use.Execute(null);
        Assert.Equal("neither, use the environment appsettings.json", q.Answer);
    }

    [Fact]
    public void Giving_up_on_questions_takes_them_off_the_screen_as_well()
    {
        OnUiThread(async () =>
        {
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) => Task.FromResult<AiOutput>(++asked <= 3
                ? """{"questions":[{"question":"Which file?"}]}"""
                : "The plan");

            using var vm = NewTile();
            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            // Three rounds, which is the budget, and each one answered.
            for (var round = 0; round < 3; round++)
            {
                Assert.Single(vm.Questions);
                vm.Questions[0].Answer = "appsettings.json";
                await vm.SendAnswersCommand.ExecuteAsync(null);
            }

            // The fourth round is refused and the tile plans with what it has. That path returns before
            // the round starts, so the questions were being cleared after it and never on this route:
            // the tile arrived in Plan still showing the old questions, and the approval panel stands
            // down while questions are up — leaving a set nobody was going to read and no way to
            // approve the plan they had been abandoned for.
            Assert.Equal(GoalPhase.Plan, vm.CurrentPhase);
            Assert.Empty(vm.Questions);
            Assert.False(vm.ShowQuestions);
            Assert.True(vm.ShowApproval);
            Assert.Contains(vm.Messages, m => m.Text.Contains("rounds of questions"));
        });
    }

    [Fact]
    public void A_status_line_arriving_after_the_run_is_not_shown()
    {
        OnUiThread(async () =>
        {
            AnswerWith("Which files?");

            using var vm = NewTile();
            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            Assert.False(vm.IsRunning);

            // What the reader thread does, at the moment it loses the race: the run has ended and the
            // finally has already cleared Activity. Posting anyway left an idle tile naming the last
            // file the tool happened to open, for the rest of the session.
            vm.SetActivityIfRunning("Read src/Cart.cs");

            Assert.Equal("", vm.Activity);
        });
    }

    [Fact]
    public void The_status_strip_stops_saying_what_the_tool_is_doing_when_it_stops_doing_it()
    {
        OnUiThread(async () =>
        {
            AnswerWith("Which files?");

            using var vm = NewTile();

            // Set as the reader thread sets it, mid-run. Every way a run can end — finished, paused,
            // cancelled, failed, thrown — has to take it back down, or a tile that is waiting for you
            // sits there naming the last file the tool happened to open.
            vm.Activity = "Read src/Cart.cs";

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.False(vm.IsRunning);
            Assert.Equal("", vm.Activity);
        });
    }

    [Fact]
    public void A_tool_that_says_it_failed_stops_the_run_instead_of_being_believed()
    {
        OnUiThread(async () =>
        {
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) => Task.FromResult(
                AiOutput.Failure("I got as far as renaming Cart.cs.\n\n[error] Credit balance is too low"));

            using var vm = NewTile();
            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            // Judged on the fact, not on the text. A failed run has text in it — an apology, a
            // half-finished note — and read as an answer that became the clarification, the plan or the
            // review, and the loop carried on from it.
            Assert.True(vm.IsPaused);
            Assert.DoesNotContain(vm.Messages, m => m.Role == GoalMessageRole.Assistant);

            // And what it managed to say is still shown, because a failed implementation has usually
            // already written files and this is the only account of what is in the worktree.
            Assert.Contains(vm.Messages, m => m.Text.Contains("renaming Cart.cs"));
            Assert.Contains(vm.Messages, m => m.Text.Contains("reported a failure"));
        });
    }

    [Fact]
    public void Detect_and_run_starts_at_the_review_because_the_changes_are_already_on_disk()
    {
        OnUiThread(async () =>
        {
            var prompts = new List<string>();
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, prompt, _, _) =>
            {
                prompts.Add(prompt);
                asked++;
                return Task.FromResult<AiOutput>(asked == 1 ? "Make the totals include discounts." : "VERDICT: PASS");
            };

            using var vm = NewTile();

            await vm.DetectGoalAndRunCommand.ExecuteAsync(null);

            // Two runs: the detection and the review. No implementation ran first — asking the tool to
            // redo work it can see it has already done is usually a no-op and sometimes a duplicate.
            Assert.Equal(2, prompts.Count);
            Assert.Contains("Review the code changes", prompts[1]);
            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
        });
    }

    [Fact]
    public void An_empty_answer_puts_nothing_in_the_transcript()
    {
        OnUiThread(async () =>
        {
            AnswerWith("   \n  ");

            using var vm = NewTile();

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            // The tool answered with whitespace, which is not an answer. It used to become a blank
            // assistant bubble, because the check asked whether the string was null rather than what the
            // run had come to.
            Assert.DoesNotContain(vm.Messages, m => m.Role == GoalMessageRole.Assistant);
            Assert.Contains(vm.Messages, m => m.Text.Contains("returned nothing"));
            Assert.True(vm.IsPaused);
        });
    }

    [Fact]
    public void An_empty_answer_inside_the_loop_pauses_rather_than_ending_the_goal()
    {
        OnUiThread(async () =>
        {
            // Clarify and plan answer, then nothing from the implementation. A tool that returned
            // nothing once may answer the next time — the same argument that has a crash pause rather
            // than end the goal — and the loop used to be the one place that disagreed.
            AnswerWith("Which files?", NoMoreQuestions, "The plan", "");

            using var vm = NewTile();

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.NotEqual(GoalPhase.Summary, vm.CurrentPhase);
            Assert.True(vm.IsPaused);
            Assert.Contains(vm.Messages, m => m.Text.Contains("returned nothing"));
        });
    }

    [Fact]
    public void A_tile_nobody_used_leaves_no_file_behind_and_a_used_one_survives_being_closed()
    {
        OnUiThread(async () =>
        {
            AnswerWith("Which files?");

            var untouched = NewTile();
            var untouchedPath = untouched.FilePath;
            untouched.Dispose();

            // .mtiles/goals/ lives in the user's repository and nothing ever prunes it, so a tile
            // opened and closed without a word must not leave an empty session in it.
            Assert.False(File.Exists(untouchedPath));

            var used = NewTile();
            var usedPath = used.FilePath;
            used.InputText = "a goal";
            await used.SubmitCommand.ExecuteAsync(null);

            // Messages are written on a debounce, so what proves the flush is closing before it fires.
            used.Dispose();

            Assert.True(File.Exists(usedPath));
            Assert.Contains("a goal", File.ReadAllText(usedPath));
        });
    }

    [Fact]
    public void A_paused_run_survives_being_closed_and_carries_on_when_reopened()
    {
        OnUiThread(async () =>
        {
            var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
            settings.Settings.CustomAiTools.Add(FakeTool());

            string path;
            var asked = 0;

            var first = new GoalTileViewModel(_dir, settings) { ConfirmAction = _ => Task.FromResult(true) };
            first.SelectedToolName = "Fake Tool";
            path = first.FilePath;

            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                if (asked == 4) first.PauseCommand.Execute(null);
                return Task.FromResult<AiOutput>(asked switch
                {
                    1 => "Which files?",
                    2 => NoMoreQuestions,
                    3 => "The plan",
                    _ => "Implemented it"
                });
            };

            first.InputText = "a goal";
            await first.SubmitCommand.ExecuteAsync(null);
            first.InputText = "all of it";
            await first.SubmitCommand.ExecuteAsync(null);
            first.InputText = "ok";
            await first.SubmitCommand.ExecuteAsync(null);

            Assert.True(first.IsPaused);
            first.Dispose();

            // Reopened from its own file, as a restart does.
            using var second = new GoalTileViewModel(path, _dir, settings) { ConfirmAction = _ => Task.FromResult(true) };

            Assert.True(second.IsPaused);
            Assert.Equal(GoalPhase.Review, second.CurrentPhase);
            Assert.Contains("Resume", second.PhaseLabel);

            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) => Task.FromResult<AiOutput>("VERDICT: PASS");
            await second.ResumeCommand.ExecuteAsync(null);

            // It carried on from the review rather than starting the implementation over, and finished.
            Assert.Equal(GoalPhase.Summary, second.CurrentPhase);
            Assert.False(second.IsPaused);
        });
    }

    [Fact]
    public void A_goal_file_that_cannot_be_opened_is_reported_and_never_written_over()
    {
        OnUiThread(async () =>
        {
            var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
            settings.Settings.CustomAiTools.Add(FakeTool());

            // Stubbed like every other run here. Without it the tile falls back to whatever real tool is
            // installed on the machine — the default selection is "Claude Code" — and submitting a goal
            // launched the actual CLI and waited out a real model round-trip: twenty seconds, a network
            // dependency and a different code path on an agent with no tool installed.
            AnswerWith("Which files?");

            var path = Path.Combine(_dir, "held.json");
            await File.WriteAllTextAsync(path, "{\"OriginalGoal\":\"a real session\"}");

            // Held open the way a backup tool holds a file for a moment. The content is fine; only the
            // opening fails — and the tile in front of it is empty, so saving would replace a real
            // session with the blank one that failed to load it.
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                using var vm = new GoalTileViewModel(path, _dir, settings) { ConfirmAction = _ => Task.FromResult(true) };

                Assert.Contains(vm.Messages, m => m.Text.Contains("could not be opened"));

                vm.InputText = "a goal";
                await vm.SubmitCommand.ExecuteAsync(null);
            }

            Assert.Contains("a real session", await File.ReadAllTextAsync(path));
        });
    }

    [Fact]
    public void Approving_a_plan_that_was_never_proposed_leaves_the_tile_resumable()
    {
        OnUiThread(async () =>
        {
            // Clarify asks, the answer ends the questions, and the plan run then returns nothing. The
            // tile pauses in Plan with no plan in it.
            AnswerWith("Which files?", NoMoreQuestions, "");

            using var vm = NewTile();

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.True(vm.IsPaused);

            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            // No implementation started against an empty plan, and — the part that was broken — the
            // pause is still there, so the "click Resume" it just printed is something the user can do.
            Assert.NotEqual(GoalPhase.Implement, vm.CurrentPhase);
            Assert.True(vm.IsPaused);
            Assert.Contains(vm.Messages, m => m.Text.Contains("no plan to approve"));
        });
    }

    [Fact]
    public void A_pause_while_the_working_tree_is_being_read_is_a_pause_not_an_error()
    {
        OnUiThread(async () =>
        {
            using var vm = NewTile();

            // The reader is cancelled the way a pause cancels it. Uncaught, this came out of the loop
            // as "Unexpected error: The operation was canceled" — stopping looked like breaking.
            WorktreeReader.Factory = (_, ct) =>
            {
                vm.PauseCommand.Execute(null);
                throw new OperationCanceledException(ct);
            };

            AnswerWith("Which files?", NoMoreQuestions, "The plan");

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("Unexpected error"));
            Assert.True(vm.IsPaused);
        });
    }

    [Fact]
    public void A_plan_that_was_rejected_cannot_be_approved_afterwards()
    {
        OnUiThread(async () =>
        {
            // Plan proposed, rejected, and the second planning run answers nothing. "ok" then used to
            // approve the plan the user had just turned down, because nothing cleared it.
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                return Task.FromResult<AiOutput>(asked switch
                {
                    1 => "Which files?",
                    2 => NoMoreQuestions,
                    3 => "PLAN A — rewrite everything",
                    4 => NoMoreQuestions,
                    _ => ""
                });
            };

            using var vm = NewTile();

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);      // → clarify
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);      // → plan A
            vm.InputText = "no, do it differently";
            await vm.SubmitCommand.ExecuteAsync(null);      // rejected → clarify says nothing more to ask
                                                           //          → a second planning run, which answers nothing

            Assert.Equal(GoalPhase.Plan, vm.CurrentPhase);
            Assert.True(vm.IsPaused);

            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.NotEqual(GoalPhase.Implement, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Text.Contains("no plan to approve"));
        });
    }

    [Fact]
    public void Starting_a_fresh_goal_on_an_unused_tile_still_leaves_no_file()
    {
        OnUiThread(async () =>
        {
            using var vm = NewTile();
            var path = vm.FilePath;

            await vm.NewGoalCommand.ExecuteAsync(null);

            // The guard for this existed and was unreachable: SyncFromEngine wrote the file a line
            // before anything asked whether it should.
            Assert.False(File.Exists(path));
        });
    }

    [Fact]
    public void Starting_a_fresh_goal_on_a_used_tile_writes_the_reset_out()
    {
        OnUiThread(async () =>
        {
            AnswerWith("Which files?");

            using var vm = NewTile();
            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            WaitForFile(vm);

            Assert.Contains("a goal", await File.ReadAllTextAsync(vm.FilePath));

            await vm.NewGoalCommand.ExecuteAsync(null);

            // The other half: an existing session must not be left on disk after it is cleared away.
            Assert.DoesNotContain("a goal", await File.ReadAllTextAsync(vm.FilePath));
        });
    }

    [Fact]
    public void A_tool_that_throws_leaves_the_goal_resumable_rather_than_finished()
    {
        OnUiThread(async () =>
        {
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
                throw new InvalidOperationException("the tool exploded");

            using var vm = NewTile();

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            // Not Summary: a process that would not start may well start on the next click, and ending the
            // goal over it threw away everything the session held.
            Assert.NotEqual(GoalPhase.Summary, vm.CurrentPhase);
            Assert.True(vm.IsPaused);
            Assert.Contains(vm.Messages, m => m.Text.Contains("The AI tool failed"));
        });
    }
}
