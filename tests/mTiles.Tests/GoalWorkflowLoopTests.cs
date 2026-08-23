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
public class GoalWorkflowLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-loop-" + Guid.NewGuid().ToString("N"));

    public GoalWorkflowLoopTests()
    {
        Directory.CreateDirectory(_dir);

        // No git anywhere near these. The temporary directory is not a repository, so the real reader
        // would spawn four processes a lap only to report that it could not read anything.
        WorktreeReader.Factory = (_, _) => Task.FromResult<string?>("diff --git a/x b/x");
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
            AnswerWith("Which files?", "The plan", "Implemented it", "VERDICT: PASS");

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
            AnswerWith("Which files?", "The plan", "Implemented it", "VERDICT: FAIL — not yet");

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
            Assert.Contains(vm.Messages, m => m.Text.Contains("without a passing review"));
            Assert.DoesNotContain(vm.Messages, m => m.Text.StartsWith("Goal completed"));
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
            AnswerWith("Which files?", "The plan", "");

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
                if (asked == 3) vm.PauseCommand.Execute(null);
                return Task.FromResult(asked switch
                {
                    1 => "Which files?",
                    2 => "The plan",
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
                if (asked == 3) first.PauseCommand.Execute(null);
                return Task.FromResult(asked switch { 1 => "Which files?", 2 => "The plan", _ => "Implemented it" });
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
            // Clarify answers, the plan run returns nothing. The tile pauses in Plan with no plan in it.
            AnswerWith("Which files?", "");

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

            AnswerWith("Which files?", "The plan");

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
                    2 => "PLAN A — rewrite everything",
                    3 => "More questions?",
                    _ => ""
                });
            };

            using var vm = NewTile();

            vm.InputText = "a goal";
            await vm.SubmitCommand.ExecuteAsync(null);      // → clarify
            vm.InputText = "all of it";
            await vm.SubmitCommand.ExecuteAsync(null);      // → plan A
            vm.InputText = "no, do it differently";
            await vm.SubmitCommand.ExecuteAsync(null);      // rejected → back to clarify, which asks again
            vm.InputText = "here is the answer";
            await vm.SubmitCommand.ExecuteAsync(null);      // → a second planning run, which answers nothing

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
