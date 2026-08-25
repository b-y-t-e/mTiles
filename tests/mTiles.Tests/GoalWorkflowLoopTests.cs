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
        VerifyCommandRunner.Factory = null;
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
            Task.FromResult(answers[Math.Min(asked++, answers.Length - 1)]);
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
            Assert.Contains(vm.Messages, m => m.Text.Contains("changed nothing in the working tree"));
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
                return Task.FromResult(asked switch
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
            Assert.Contains(vm.Messages, m => m.Text.Contains("reached exactly the same"));

            // And it stopped short of the budget rather than proving the point five times.
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("attempt 4"));
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
            // is not.
            Assert.Contains(vm.Messages, m => m.Text.Contains("suggest") && m.Text.Contains("src/X.cs:4"));
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("Looks mostly fine"));

            Assert.Equal(["1S"], vm.Badges.Select(b => b.Text));
        });
    }

    [Fact]
    public void A_failing_verify_command_blocks_a_review_that_says_everything_is_fine()
    {
        OnUiThread(async () =>
        {
            var ran = 0;
            VerifyCommandRunner.Factory = (_, command, _) =>
            {
                ran++;
                Assert.Equal("dotnet build", command);
                return Task.FromResult(new VerifyOutcome(true, 1, "error CS0103: the name x does not exist"));
            };

            AnswerWith("Which files?", NoMoreQuestions, "The plan", "Implemented it", "VERDICT: PASS");

            using var vm = NewTile();
            vm.Criteria.VerifyCommand = "dotnet build";

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            // The review said the goal was met; the exit code says the code does not build. The exit
            // code is the only fact here that is not the tool's opinion of its own work.
            Assert.Equal(5, ran);
            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.DoesNotContain(vm.Messages, m => m.Text.StartsWith("Goal completed"));
            Assert.Contains(vm.Messages, m => m.Text.Contains("exited 1"));
        });
    }

    [Fact]
    public void The_verify_output_reaches_the_review_prompt()
    {
        OnUiThread(async () =>
        {
            VerifyCommandRunner.Factory = (_, _, _) =>
                Task.FromResult(new VerifyOutcome(true, 0, "Build succeeded. 0 warnings"));

            var prompts = new List<string>();
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, prompt, _, _) =>
            {
                prompts.Add(prompt);
                asked++;
                return Task.FromResult(asked switch
                {
                    1 => "Which files?",
                    2 => NoMoreQuestions,
                    3 => "The plan",
                    4 => "Implemented it",
                    _ => "VERDICT: PASS",
                });
            };

            using var vm = NewTile();
            vm.Criteria.VerifyCommand = "dotnet build";

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            // So the review argues with a compiler rather than with the diff alone.
            Assert.Contains("Build succeeded. 0 warnings", prompts[^1]);
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
                return Task.FromResult(asked switch { 1 => "Which files?", 2 => NoMoreQuestions, _ => "The plan" });
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
                return Task.FromResult(asked == 1
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
                return Task.FromResult(
                    "```json\n{\"questions\":[{\"question\":\"Which file?\"},{\"question\":\"Sync?\"}]}\n```");
            };

            using var vm = NewTile();

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            // The composer was filled with the numbering, and pressing Enter used to send it — one of
            // three rounds spent on "1.\n2.".
            Assert.Equal("1. \n2. ", vm.InputText);

            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.Equal(1, asked);
            Assert.Contains(vm.Messages, m => m.Text.Contains("Answer at least one"));
            Assert.DoesNotContain(vm.Messages, m => m.Role == GoalMessageRole.User && m.Text.StartsWith("1."));
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

            first.Criteria.VerifyCommand = "dotnet build";
            first.Criteria.MaxIterations = 9;
            first.Criteria.MaxWarnings = 2;
            first.Criteria.RequireGoalMet = false;
            first.Criteria.StopOnNoChange = false;
            first.Dispose();

            using var second = new GoalTileViewModel(path, _dir, settings) { ConfirmAction = _ => Task.FromResult(true) };

            Assert.Equal("dotnet build", second.Criteria.VerifyCommand);
            Assert.Equal(9, second.Criteria.MaxIterations);
            Assert.Equal(2, second.Criteria.MaxWarnings);
            Assert.False(second.Criteria.RequireGoalMet);
            Assert.False(second.Criteria.StopOnNoChange);

            // And a command that arrived in a file is named out loud, and gated: goal files live in the
            // user's own repository and a committed one travels with the branch.
            Assert.Contains(second.Messages, m => m.Text.Contains("carries a verify command: `dotnet build`"));
        });
    }

    [Fact]
    public void A_number_field_that_never_converted_is_redrawn_when_it_is_left()
    {
        OnUiThread(async () =>
        {
            using var vm = NewTile();
            vm.Criteria.MaxIterations = 7;

            var notified = 0;
            vm.Criteria.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.Criteria.MaxIterations)) notified++;
            };

            // "50x" never reaches the property — Avalonia reports a failed conversion as a binding
            // error, so the setter is not called at all and the field still holds 7. Assigning 7 back
            // is a no-op that ObservableObject quite correctly raises nothing for, which is why this
            // has to notify by hand: the binding must be told to re-read a source that did not move,
            // or the junk stays on screen looking like a setting.
            vm.Criteria.Refresh();

            Assert.True(notified > 0);
            Assert.Equal(7, vm.Criteria.MaxIterations);

            await Task.CompletedTask;
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
                return Task.FromResult(asked == 1
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
                return Task.FromResult(asked == 1
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
                return Task.FromResult(++asked == 1
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
    public void An_attempt_count_outside_what_will_run_is_shown_as_such()
    {
        OnUiThread(async () =>
        {
            using var vm = NewTile();

            Assert.Equal("", vm.Criteria.AttemptsNote);
            Assert.Equal("", vm.Criteria.TolerancesNote);

            vm.Criteria.MaxErrors = -1;
            Assert.Equal("below zero reads as none", vm.Criteria.TolerancesNote);
            vm.Criteria.MaxErrors = 0;
            Assert.Equal("", vm.Criteria.TolerancesNote);

            vm.Criteria.MaxIterations = 999;
            Assert.Equal("using 50", vm.Criteria.AttemptsNote);

            vm.Criteria.MaxIterations = 0;
            Assert.Equal("using 1", vm.Criteria.AttemptsNote);

            vm.Criteria.MaxIterations = 3;
            Assert.Equal("", vm.Criteria.AttemptsNote);

            await Task.CompletedTask;
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

    [Fact]
    public void A_tile_stopped_between_phases_does_not_keep_saying_it_is_working()
    {
        OnUiThread(async () =>
        {
            using var vm = NewTile();

            // Paused from inside the implementation's own answer, so the *next* phase finds the pause
            // already standing and returns before launching anything. There is no cancelled run for
            // HandleNonAnswerAsync to describe, and nothing else writes the label — so the strip went
            // on saying "AI is implementing (attempt 1/5)…" over a tile that had stopped.
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                if (asked == 4) vm.PauseCommand.Execute(null);
                return Task.FromResult(asked switch
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
                return Task.FromResult("some goal");
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
    public void A_pause_during_the_verify_command_resumes_at_the_review()
    {
        OnUiThread(async () =>
        {
            using var vm = NewTile();
            vm.Criteria.VerifyCommand = "dotnet build";

            // Cancelled from inside the verify command, which is the long part of a lap and the part
            // most likely to be interrupted. Leaving Implement standing through it meant Resume ran the
            // whole implementation again over a worktree that already had its changes.
            VerifyCommandRunner.Factory = (_, _, ct) =>
            {
                vm.PauseCommand.Execute(null);
                throw new OperationCanceledException(ct);
            };

            AnswerWith("Which files?", NoMoreQuestions, "The plan", "Implemented it");

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.True(vm.IsPaused);
            Assert.Equal(GoalPhase.Review, vm.CurrentPhase);
            Assert.True(GoalTilePolicy.ResumesAtReview(vm.CurrentPhase));
        });
    }

    [Fact]
    public void A_command_the_user_typed_is_never_asked_about_however_the_panel_is_used()
    {
        OnUiThread(async () =>
        {
            AnswerWith("Which files?", NoMoreQuestions, "The plan", "Implemented it", "VERDICT: PASS");

            var ran = 0;
            VerifyCommandRunner.Factory = (_, _, _) =>
            {
                ran++;
                return Task.FromResult(new VerifyOutcome(true, 0, ""));
            };

            var asked = 0;
            using var vm = NewTile();
            vm.ConfirmAction = _ => { asked++; return Task.FromResult(false); };

            vm.Criteria.VerifyCommand = "dotnet build";

            // Leaving a number field refreshes the panel, and refreshing used to be indistinguishable
            // from the criteria arriving from somewhere else — so the command the user had just typed
            // became a command "from the file", was asked about, and was deleted on a no.
            vm.Criteria.Refresh();

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.Equal(0, asked);
            Assert.Equal("dotnet build", vm.Criteria.VerifyCommand);
            Assert.True(ran > 0);
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

    [Fact]
    public void A_verify_command_from_the_saved_file_is_not_run_until_it_is_approved()
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
            first.Criteria.VerifyCommand = "rm -rf /";
            first.Dispose();

            var ran = 0;
            VerifyCommandRunner.Factory = (_, _, _) =>
            {
                ran++;
                return Task.FromResult(new VerifyOutcome(true, 0, ""));
            };

            // A shell command that arrived in a file. Goal files live in the user's own repository and
            // nothing gitignores them unless the Git tile is used, so a committed one travels with a
            // branch — and this tile would otherwise run it unattended after every attempt.
            var asked = new List<string>();
            using var second = new GoalTileViewModel(path, _dir, settings)
            {
                ConfirmAction = q => { asked.Add(q); return Task.FromResult(false); },
            };

            AnswerWith(NoMoreQuestions, "The plan", "Implemented it", "VERDICT: PASS");

            second.InputText = "all of it";
            await second.SubmitCommand.ExecuteAsync(null);
            second.InputText = "ok";
            await second.SubmitCommand.ExecuteAsync(null);

            Assert.Equal(0, ran);
            Assert.Contains(asked, q => q.Contains("rm -rf /"));

            // Declining removes it rather than skipping it once: a question asked on every attempt is a
            // question answered wrongly on the fifth.
            Assert.Equal("", second.Criteria.VerifyCommand);
            Assert.Contains(second.Messages, m => m.Text.Contains("not approved"));
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
                return Task.FromResult("Implemented it");
            }

            if (prompt.Contains("Review the code changes"))
            {
                reviews++;
                return Task.FromResult(
                    "```json\n{\"goalMet\":false,\"findings\":[{\"severity\":\"warning\"," +
                    $"\"title\":\"W{reviews}\"}}]}}\n```");
            }

            return Task.FromResult(before++ switch
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
    public void Clearing_the_verify_command_that_hung_is_what_lets_the_goal_carry_on()
    {
        OnUiThread(async () =>
        {
            VerifyCommandRunner.Factory = (_, _, _) =>
                Task.FromResult(VerifyOutcome.Timeout("it was still running after 30 minutes and was stopped"));

            GoalTileViewModel.AiRunnerFactory = (_, prompt, _, _) =>
                Task.FromResult(prompt.Contains("Implement the following goal") ? "Implemented it" : NoMoreQuestions);

            using var vm = NewTile();
            vm.Criteria.MaxIterations = 5;
            vm.Criteria.VerifyCommand = "dotnet build";

            await RunToSummaryAsync(vm);

            // Not a budget, so more attempts would buy another half hour of the same wait.
            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.False(vm.CanContinue);

            // It is, however, the one stop the user can fix — and the summary says how. Once the
            // command that hung is gone, carrying on is exactly what they want, and the alternative was
            // retyping the goal into an empty tile.
            Assert.Contains(vm.Messages, m => m.Text.Contains("clear it under the tune button"));

            vm.Criteria.VerifyCommand = "";
            Assert.True(vm.CanContinue);

            // The budget was never spent — the run stopped on attempt 1 of 5 — so Continue has four
            // attempts to let happen and nothing to add. Adding the field on top would raise a ceiling
            // the user set to 5 up to 6, which is neither asked for nor what the button says.
            Assert.Equal("Continue", vm.ContinueLabel);
            Assert.Equal("The verify command was cleared.", vm.ContinueReason);

            var attempts = vm.Criteria.MaxIterations;
            await vm.ContinueRunCommand.ExecuteAsync(null);
            Assert.Equal(attempts, vm.Criteria.MaxIterations);
        });
    }

    [Fact]
    public void A_verify_command_that_never_finishes_stops_the_run_rather_than_being_tried_again()
    {
        OnUiThread(async () =>
        {
            var runs = 0;
            VerifyCommandRunner.Factory = (_, _, _) =>
            {
                runs++;
                return Task.FromResult(VerifyOutcome.Timeout("it was still running after 30 minutes and was stopped"));
            };

            var reviews = 0;
            GoalTileViewModel.AiRunnerFactory = (_, prompt, _, _) =>
            {
                if (prompt.Contains("Review the code changes")) reviews++;
                return Task.FromResult(prompt.Contains("Implement the following goal")
                    ? "Implemented it"
                    : NoMoreQuestions);
            };

            using var vm = NewTile();
            vm.Criteria.MaxIterations = 20;
            vm.Criteria.VerifyCommand = "dotnet build";

            await RunToSummaryAsync(vm);

            // Once, not twenty times. The timeout is half an hour, so the attempts left on the budget
            // are hours of waiting for the same answer, and the answer is unusable either way.
            Assert.Equal(1, runs);
            Assert.Equal(0, reviews);
            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Text.Contains("never finished"));

            // Not a budget, so there is nothing for Continue to raise.
            Assert.False(vm.CanContinue);
        });
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

            // Here the attempts really did run out, so the button names what it will add and the bar
            // says why it is there.
            Assert.Equal("Continue · +2", vm.ContinueLabel);
            Assert.Equal("The attempts ran out.", vm.ContinueReason);

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

    [Fact]
    public void A_number_typed_after_Continue_is_the_one_the_next_goal_starts_from()
    {
        OnUiThread(async () =>
        {
            LoopAnsweringWithOneWarning();

            using var vm = NewTile();
            vm.Criteria.MaxIterations = 2;

            await RunToSummaryAsync(vm);
            await vm.ContinueRunCommand.ExecuteAsync(null);
            Assert.Equal(4, vm.Criteria.MaxIterations);

            // Moving the field by hand makes the number theirs again. The remembered "what the user
            // chose" is 2, and without clearing it the next goal started from 2 rather than from the 8
            // they had just typed.
            vm.Criteria.MaxIterations = 8;

            vm.InputText = "a different goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.Equal(8, vm.Criteria.MaxIterations);
        });
    }

    [Fact]
    public void The_attempts_Continue_adds_belong_to_that_goal_and_not_to_the_tile()
    {
        OnUiThread(async () =>
        {
            var implemented = LoopAnsweringWithOneWarning();

            using var vm = NewTile();
            vm.Criteria.MaxIterations = 2;

            await RunToSummaryAsync(vm);
            Assert.True(vm.CanContinue);

            await vm.ContinueRunCommand.ExecuteAsync(null);
            Assert.Equal(4, vm.Criteria.MaxIterations);
            Assert.Equal(4, implemented());

            // A new goal in the same tile gets the number the user chose, not the one the button wrote.
            // Criteria deliberately outlive a goal — they are how the tile works — which is right for a
            // typed number and wrong for one Continue raised: two continuations left the next goal
            // starting at eight.
            vm.InputText = "a different goal";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.Equal(2, vm.Criteria.MaxIterations);
        });
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
                return Task.FromResult(asked == 1 ? "Which files?" : NoMoreQuestions);
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
    public void Continue_is_not_offered_where_more_attempts_would_change_nothing()
    {
        OnUiThread(async () =>
        {
            // The same review twice running, which is the no-progress stop. That is not a budget, so
            // there is nothing for Continue to raise: the next attempts would find the same things a
            // third time.
            GoalTileViewModel.AiRunnerFactory = (_, prompt, _, _) =>
                Task.FromResult(prompt.Contains("Review the code changes")
                    ? "```json\n{\"goalMet\":false,\"findings\":[{\"severity\":\"warning\",\"title\":\"W\"}]}\n```"
                    : prompt.Contains("Implement the following goal") ? "Implemented it" : NoMoreQuestions);

            using var vm = NewTile();
            vm.Criteria.MaxIterations = 5;

            await RunToSummaryAsync(vm);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.False(vm.CanContinue);
        });
    }

    [Fact]
    public void A_note_about_this_session_is_not_written_into_the_goal_file()
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
            first.Criteria.VerifyCommand = "dotnet build";
            first.Dispose();

            // Opened, closed, opened again. The note is true of each opening, so it is said at each one —
            // and if it were written down, every opening would inherit all the earlier copies too.
            var second = new GoalTileViewModel(path, _dir, settings) { ConfirmAction = _ => Task.FromResult(true) };
            second.Dispose();

            using var third = new GoalTileViewModel(path, _dir, settings) { ConfirmAction = _ => Task.FromResult(true) };

            Assert.Single(third.Messages, m => m.Text.Contains("carries a verify command"));
        });
    }

    [Fact]
    public void Editing_the_verify_command_and_undoing_it_is_not_consent()
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
            first.Criteria.VerifyCommand = "rm -rf /";
            first.Dispose();

            var ran = 0;
            VerifyCommandRunner.Factory = (_, _, _) =>
            {
                ran++;
                return Task.FromResult(new VerifyOutcome(true, 0, ""));
            };

            var asked = new List<string>();
            using var second = new GoalTileViewModel(path, _dir, settings)
            {
                ConfirmAction = q => { asked.Add(q); return Task.FromResult(false); },
            };

            // Touched, then put back exactly as it was. The string about to be handed to a shell is the
            // file's, unchanged — and a latch would have called that a choice and dropped the gate.
            second.Criteria.VerifyCommand = "rm -rf /x";
            second.Criteria.VerifyCommand = "rm -rf /";
            Assert.False(second.Criteria.VerifyCommandWasTyped);

            AnswerWith(NoMoreQuestions, "The plan", "Implemented it", "VERDICT: PASS");

            second.InputText = "all of it";
            await second.SubmitCommand.ExecuteAsync(null);
            second.InputText = "ok";
            await second.SubmitCommand.ExecuteAsync(null);

            Assert.Contains(asked, q => q.Contains("rm -rf /"));
            Assert.Equal(0, ran);
        });
    }

    [Fact]
    public void A_refusal_deletes_the_command_it_was_about_and_not_the_one_typed_since()
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
            first.Criteria.VerifyCommand = "rm -rf /";
            first.Dispose();

            VerifyCommandRunner.Factory = (_, _, _) => Task.FromResult(new VerifyOutcome(true, 0, ""));

            GoalTileViewModel? second = null;

            // The dialog is awaited and the panel stays usable while it is on screen. Somebody who says
            // "no" to the command out of the file and then types their own used to have the new one
            // deleted by the refusal — in the name of an answer that was never about it.
            second = new GoalTileViewModel(path, _dir, settings)
            {
                ConfirmAction = _ =>
                {
                    second!.Criteria.VerifyCommand = "dotnet test";
                    return Task.FromResult(false);
                },
            };

            using (second)
            {
                AnswerWith(NoMoreQuestions, "The plan", "Implemented it", "VERDICT: PASS");

                second.InputText = "all of it";
                await second.SubmitCommand.ExecuteAsync(null);
                second.InputText = "ok";
                await second.SubmitCommand.ExecuteAsync(null);

                Assert.Equal("dotnet test", second.Criteria.VerifyCommand);
            }
        });
    }

    [Fact]
    public void A_command_the_user_typed_is_never_the_subject_of_the_question()
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
            first.Criteria.VerifyCommand = "dotnet build";
            first.Dispose();

            var ran = 0;
            VerifyCommandRunner.Factory = (_, _, _) =>
            {
                ran++;
                return Task.FromResult(new VerifyOutcome(true, 0, ""));
            };

            // Loaded from the file, so the gate is armed — and then the user types their own command
            // over the top of it, which is the whole of "they chose it".
            var asked = new List<string>();
            using var second = new GoalTileViewModel(path, _dir, settings)
            {
                ConfirmAction = q => { asked.Add(q); return Task.FromResult(false); },
            };

            second.Criteria.VerifyCommand = "dotnet test";

            var reviews = 0;
            GoalTileViewModel.AiRunnerFactory = (_, prompt, _, _) =>
            {
                if (!prompt.Contains("Review the code changes"))
                    return Task.FromResult(prompt.Contains("Implement the following goal")
                        ? "Implemented it"
                        : NoMoreQuestions);

                // A title that moves, so two reviews never look alike and the no-progress stop stays
                // out of the way of what this test is about.
                reviews++;
                return Task.FromResult(
                    "```json\n{\"goalMet\":false,\"findings\":[{\"severity\":\"warning\"," +
                    $"\"title\":\"W{reviews}\"}}]}}\n```");
            };

            second.Criteria.MaxIterations = 1;
            second.InputText = "all of it";
            await second.SubmitCommand.ExecuteAsync(null);
            second.InputText = "ok";
            await second.SubmitCommand.ExecuteAsync(null);

            // Never asked, and still there. ConfirmAction answers "no", so an asked question would also
            // have deleted the command the user had just typed.
            Assert.Empty(asked);
            Assert.Equal(1, ran);
            Assert.Equal("dotnet test", second.Criteria.VerifyCommand);

            // And it survives Continue, which reloads the panel to show the raised ceiling. That reload
            // used to clear the "the user typed it" flag, so the next attempt asked them to approve
            // their own command and deleted it on the no above.
            Assert.True(second.CanContinue);
            await second.ContinueRunCommand.ExecuteAsync(null);

            Assert.Empty(asked);
            Assert.Equal("dotnet test", second.Criteria.VerifyCommand);
            Assert.True(ran > 1);
        });
    }

    [Fact]
    public void A_verify_command_too_long_to_show_is_not_copied_into_the_transcript()
    {
        OnUiThread(async () =>
        {
            var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
            settings.Settings.CustomAiTools.Add(FakeTool());

            AnswerWith("Which files?");

            var huge = new string('x', CommandDisplay.MaxConsentable * 4);

            var first = new GoalTileViewModel(_dir, settings) { ConfirmAction = _ => Task.FromResult(true) };
            first.SelectedToolName = "Fake Tool";
            var path = first.FilePath;
            first.InputText = "a goal";
            await first.SubmitCommand.ExecuteAsync(null);
            first.Criteria.VerifyCommand = huge;
            first.Dispose();

            using var second = new GoalTileViewModel(path, _dir, settings);

            // The consent gate refuses this command anyway, so the note only has to say what is in the
            // file. Printing it in full put a copy in the transcript — and the transcript is
            // reserialised into the goal file on every save, so the file carried it twice over.
            var note = Assert.Single(second.Messages, m => m.Text.Contains("verify command"));
            Assert.DoesNotContain(huge, note.Text);
            Assert.Contains(huge.Length.ToString(), note.Text);
        });
    }

    [Fact]
    public void A_tile_that_cannot_ask_about_a_verify_command_skips_it_without_deleting_it()
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
            first.Criteria.VerifyCommand = "dotnet build";
            first.Dispose();

            var ran = 0;
            VerifyCommandRunner.Factory = (_, _, _) =>
            {
                ran++;
                return Task.FromResult(new VerifyOutcome(true, 0, ""));
            };

            // No dialog to ask in. Not running it is right — an unanswered question is not a yes — but
            // "I could not ask" and "they said no" are opposite answers about *keeping* it, and
            // deleting somebody's setting because a dialog was not wired is a decision nobody made.
            using var second = new GoalTileViewModel(path, _dir, settings);

            AnswerWith(NoMoreQuestions, "The plan", "Implemented it", "VERDICT: PASS");

            second.InputText = "all of it";
            await second.SubmitCommand.ExecuteAsync(null);
            second.InputText = "ok";
            await second.SubmitCommand.ExecuteAsync(null);

            Assert.Equal(0, ran);
            Assert.Equal("dotnet build", second.Criteria.VerifyCommand);
            Assert.Contains(second.Messages, m => m.Text.Contains("cannot ask"));
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
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("changed nothing in the working tree"));
            Assert.Contains(vm.Messages, m => m.Text.Contains("without meeting the completion criteria"));
        });
    }

    [Fact]
    public void A_verify_command_that_touches_tracked_files_does_not_disarm_the_no_change_stop()
    {
        OnUiThread(async () =>
        {
            // The tree the review is handed is read after the verify command has run, so a command that
            // regenerates a tracked file made the two trees differ and quietly disarmed this stop — in
            // exactly the workspaces most likely to have one configured.
            // Keyed off the verify command actually having run, not off a read count: the
            // detect-availability probe reads the tree too, on its own schedule, and would shift any
            // counter under the test.
            var rebuilt = false;
            WorktreeReader.Factory = (_, _) => Task.FromResult<string?>(rebuilt ? "rebuilt" : "unchanged");

            VerifyCommandRunner.Factory = (_, _, _) =>
            {
                rebuilt = true;
                return Task.FromResult(new VerifyOutcome(true, 0, "built"));
            };

            AnswerWith("Which files?", NoMoreQuestions, "The plan", "Implemented it", "VERDICT: FAIL");

            using var vm = NewTile();
            vm.Criteria.VerifyCommand = "dotnet build";

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Contains(vm.Messages, m => m.Text.Contains("changed nothing in the working tree"));
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
            first.Dispose();

            using var second = new GoalTileViewModel(path, _dir, settings) { ConfirmAction = _ => Task.FromResult(true) };

            // The review it summarises is still in the transcript; the strip used to go blank anyway.
            // Only the severities that found something appear — a clean review shows nothing at all
            // rather than four zeroes.
            Assert.Equal(["1B", "1S"], second.Badges.Select(b => b.Text));
            Assert.DoesNotContain(second.Badges, b => b.Severity == GoalSeverity.Error);
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
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) => Task.FromResult("   ");

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
                return Task.FromResult(asked switch
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
            Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("reached exactly the same"));
        });
    }

    [Fact]
    public void The_note_about_a_verify_command_nobody_can_approve_is_said_once()
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
            first.Criteria.VerifyCommand = "dotnet build";
            first.Dispose();

            using var second = new GoalTileViewModel(path, _dir, settings);

            AnswerWith(NoMoreQuestions, "The plan", "Implemented it", "VERDICT: FAIL");

            second.InputText = "all of it";
            await second.SubmitCommand.ExecuteAsync(null);
            second.InputText = "ok";
            await second.SubmitCommand.ExecuteAsync(null);

            // SayOnceAsync only skips a note that is still the last thing in the transcript, and the
            // loop puts an implementation and a review in between — so this printed once per attempt.
            Assert.Equal(1, second.Messages.Count(m => m.Text.Contains("cannot ask")));
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
                return Task.FromResult(asked == 1 ? "Make the totals include discounts." : "VERDICT: PASS");
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

            // .mterminal/goals/ lives in the user's repository and nothing ever prunes it, so a tile
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
    public void A_pause_taken_after_the_implementation_resumes_at_the_review()
    {
        OnUiThread(async () =>
        {
            using var vm = NewTile();

            // Pauses from inside the implementation's own answer, which is the moment the phase moves
            // on. What is owed then is the review, and resuming used to run the whole implementation
            // again — against a worktree that already had its changes.
            var asked = 0;
            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) =>
            {
                asked++;
                if (asked == 4) vm.PauseCommand.Execute(null);
                return Task.FromResult(asked switch
                {
                    1 => "Which files?",
                    2 => NoMoreQuestions,
                    3 => "The plan",
                    _ => "Implemented it"
                });
            };

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);
            vm.InputText = "ok";
            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.True(vm.IsPaused);
            Assert.Equal(GoalPhase.Review, vm.CurrentPhase);
            Assert.True(GoalTilePolicy.ResumesAtReview(vm.CurrentPhase));

            // And the tile says so rather than claiming to be working.
            Assert.Contains("Resume", vm.PhaseLabel);
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
                return Task.FromResult(asked switch
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

            GoalTileViewModel.AiRunnerFactory = (_, _, _, _) => Task.FromResult("VERDICT: PASS");
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
                return Task.FromResult(asked switch
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
