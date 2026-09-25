using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The test timings driven through the real loop: which calls the tile makes, in which order, and
/// what a failing test run does to a goal a review has already accepted.
/// </summary>
[Collection(GoalSeamCollection.Name)]
public class GoalTestTimingLoopTests : GoalTileFixture
{
    private const string Accepted = "```json\n{\"goalMet\":true,\"findings\":[]}\n```";

    private const string OneTestFails =
        "```json\n{\"goalMet\":true,\"findings\":[{\"severity\":\"error\",\"category\":\"tests\"," +
        "\"title\":\"CartTests.Total fails\",\"detail\":\"Expected 90, got 100.\"}]}\n```";

    private const string TestsPass = "```json\n{\"goalMet\":true,\"findings\":[]}\n```";

    /// <summary>An accepting review that still names one error — the one a user unticks at the gate.</summary>
    private const string AcceptedButForAnError =
        "```json\n{\"goalMet\":true,\"findings\":[{\"severity\":\"error\",\"category\":\"correctness\"," +
        "\"title\":\"null deref\",\"detail\":\"x may be null.\"}]}\n```";

    /// <summary>A review that sends the work back, so a later lap is the one that accepts.</summary>
    private const string NotYet =
        "```json\n{\"goalMet\":false,\"findings\":[{\"severity\":\"error\",\"category\":\"correctness\"," +
        "\"title\":\"half done\",\"detail\":\"The total is not computed.\"}]}\n```";

    private static readonly TimeSpan GateDeadline = TimeSpan.FromSeconds(10);

    /// <summary>Every call the run made, in order, named by what it was asked to do.</summary>
    private sealed record Calls(List<string> Order, List<string> Prompts)
    {
        public int Count(string kind) => Order.Count(k => k == kind);
    }

    /// <summary>A reviewer that always accepts, and a test run that answers from
    /// <paramref name="testRuns"/> in turn, repeating the last.</summary>
    private static Calls Scripted(params string[] testRuns) => Scripted([Accepted], testRuns);

    /// <summary>A reviewer that answers from <paramref name="reviews"/> in turn, repeating the last,
    /// and a test run scripted the same way.</summary>
    private static Calls Scripted(string[] reviews, string[] testRuns)
    {
        var calls = new Calls([], []);
        var before = 0;

        GoalTileViewModel.AiRunnerFactory = (_, prompt, _, _) =>
        {
            lock (calls)
            {
                calls.Prompts.Add(prompt);

                if (prompt.Contains("Implement the following goal"))
                {
                    calls.Order.Add("implement");
                    return Task.FromResult<AiOutput>("Implemented it");
                }

                if (prompt.Contains("Review the code changes"))
                {
                    var answer = reviews[Math.Min(calls.Count("review"), reviews.Length - 1)];
                    calls.Order.Add("review");
                    return Task.FromResult<AiOutput>(answer);
                }

                if (prompt.Contains("One check is left: the project's tests"))
                {
                    var answer = testRuns[Math.Min(calls.Count("tests"), testRuns.Length - 1)];
                    calls.Order.Add("tests");
                    return Task.FromResult<AiOutput>(answer);
                }

                return Task.FromResult<AiOutput>(before++ switch
                {
                    0 => "Which files?",
                    1 => NoMoreQuestions,
                    _ => "The plan",
                });
            }
        };

        return calls;
    }

    private GoalTileViewModel TileWith(GoalTestTiming timing)
    {
        var vm = NewTile();
        vm.GateModeLabel = GoalReviewGatePolicy.Label(GoalReviewGateMode.Off);
        vm.Criteria.TestTimingLabel = GoalTestPolicy.Label(timing);
        return vm;
    }

    /// <summary>
    /// When done: the review accepts, the tests fail, the failure goes back as a finding, and the
    /// next acceptance runs them again — the goal is met only once they pass.
    /// </summary>
    [Fact]
    public void When_done_a_failing_test_run_sends_the_run_round_again()
    {
        Ui.Run(async () =>
        {
            var calls = Scripted(OneTestFails, TestsPass);

            using var vm = TileWith(GoalTestTiming.WhenMet);
            await RunToSummary(vm);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Equal(["implement", "review", "tests", "implement", "review", "tests"], calls.Order);

            // What failed is what the second implementation was told to fix.
            var secondImplement = calls.Prompts.Where(p => p.Contains("Implement the following goal")).Last();
            Assert.Contains("CartTests.Total fails", secondImplement);

            // Neither the implementations nor the reviews ran the suite themselves.
            foreach (var prompt in calls.Prompts.Where(p =>
                         p.Contains("Implement the following goal") || p.Contains("Review the code changes")))
            {
                Assert.DoesNotContain("the project's tests pass", prompt);
                Assert.Contains("once a review accepts the work", prompt);
            }

            Assert.Contains(vm.Messages, m => m.Text.StartsWith("Tests failed · 1 error"));
            Assert.Contains(vm.Messages, m => m.Text.StartsWith("Tests pass"));
        });
    }

    /// <summary>First review and when done: the first review runs them and, accepting, owes nothing
    /// more.</summary>
    [Fact]
    public void The_first_review_runs_the_tests_itself()
    {
        Ui.Run(async () =>
        {
            var calls = Scripted(TestsPass);

            using var vm = TileWith(GoalTestTiming.FirstReviewAndWhenMet);
            await RunToSummary(vm);

            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Equal(["implement", "review"], calls.Order);

            var review = calls.Prompts.Single(p => p.Contains("Review the code changes"));
            Assert.Contains("the project's tests pass", review);
        });
    }

    /// <summary>First review and when done: a review after the first one does not run the tests, so
    /// its acceptance owes a test run of its own.</summary>
    [Fact]
    public void A_later_acceptance_owes_a_test_run()
    {
        Ui.Run(async () =>
        {
            var calls = Scripted([NotYet, Accepted], [TestsPass]);

            using var vm = TileWith(GoalTestTiming.FirstReviewAndWhenMet);
            await RunToSummary(vm);

            Assert.Equal(GoalStopReason.Met, Saved(vm).LastStopReason);
            Assert.Equal(["implement", "review", "implement", "review", "tests"], calls.Order);

            var secondReview = calls.Prompts.Where(p => p.Contains("Review the code changes")).Last();
            Assert.DoesNotContain("the project's tests pass", secondReview);
        });
    }

    /// <summary>
    /// When done, at the gate: unticking the last error accepts the work, and Resume runs the owed
    /// tests alone — a failure goes back to the implementation, and the goal is met only once a later
    /// acceptance's tests pass.
    /// </summary>
    [Fact]
    public void Resume_after_the_gate_accepts_runs_the_owed_tests_and_a_failure_goes_back_round()
    {
        Ui.Run(async () =>
        {
            var calls = Scripted([AcceptedButForAnError], [OneTestFails, TestsPass]);

            using var vm = NewTile();
            vm.Criteria.TestTimingLabel = GoalTestPolicy.Label(GoalTestTiming.WhenMet);
            await Send(vm, "a goal");
            await Send(vm, "all of it");
            vm.GateSeconds = GoalReviewGatePolicy.MaxSeconds;

            vm.InputText = "ok";
            var run = vm.SubmitCommand.ExecuteAsync(null);
            await WhenTrue(vm, () => vm.GateOffersTheClock, GateDeadline);

            vm.Messages.Last(m => m.Findings is { Count: > 0 }).Findings!
                .Single(f => f.Title == "null deref").Fix = false;
            await run;

            Assert.True(vm.GateOffersResume);
            Assert.Equal(["implement", "review"], calls.Order);

            // The second lap is not to stop at the gate again: this test is about the first Resume.
            vm.GateModeLabel = GoalReviewGatePolicy.Label(GoalReviewGateMode.Off);
            await vm.ResumeCommand.ExecuteAsync(null);

            Assert.Equal(["implement", "review", "tests", "implement", "review", "tests"], calls.Order);
            Assert.Contains("CartTests.Total fails",
                calls.Prompts.Where(p => p.Contains("Implement the following goal")).Last());
            Assert.Equal(GoalPhase.Summary, vm.CurrentPhase);
            Assert.Equal(GoalStopReason.Met, Saved(vm).LastStopReason);
        });
    }

    /// <summary>When done, over an attempt that changed nothing: the review of the unchanged tree is
    /// an acceptance like any other and owes the tests.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unchanged_tree_accepted_owes_the_tests(bool testsPass)
    {
        Ui.Run(async () =>
        {
            TreeNeverMoves();
            var calls = Scripted([Accepted], [testsPass ? TestsPass : OneTestFails]);

            using var vm = TileWith(GoalTestTiming.WhenMet);
            await RunToSummary(vm);

            Assert.Equal(["implement", "review", "tests"], calls.Order.Take(3));
            if (testsPass)
                Assert.Equal(GoalStopReason.Met, Saved(vm).LastStopReason);
            else
            {
                Assert.NotEqual(GoalStopReason.Met, Saved(vm).LastStopReason);
                Assert.Contains(vm.Messages, m => m.Text.StartsWith("Tests failed · 1 error"));
            }
        });
    }

    /// <summary>Every review — the default, and what the tile did before the choice — makes no call
    /// of its own for the tests.</summary>
    [Fact]
    public void Every_review_runs_no_separate_test_step()
    {
        Ui.Run(async () =>
        {
            var calls = Scripted(OneTestFails);

            using var vm = TileWith(GoalTestTiming.EveryReview);
            await RunToSummary(vm);

            Assert.Equal(["implement", "review"], calls.Order);
            Assert.Contains("the project's tests pass",
                calls.Prompts.Single(p => p.Contains("Implement the following goal")));
        });
    }

    /// <summary>With the tests switched off, no timing brings them back.</summary>
    [Fact]
    public void Tests_switched_off_are_never_run_whatever_the_timing()
    {
        Ui.Run(async () =>
        {
            var calls = Scripted(OneTestFails);

            using var vm = TileWith(GoalTestTiming.WhenMet);
            vm.Criteria.RequireTestsPass = false;
            await RunToSummary(vm);

            Assert.Equal(["implement", "review"], calls.Order);
        });
    }

    /// <summary>The timing is part of the goal and comes back with it.</summary>
    [Fact]
    public void The_timing_survives_a_restart()
    {
        Ui.Run(async () =>
        {
            AnswerWith("Which files?");

            var first = TileWith(GoalTestTiming.FirstReviewAndWhenMet);
            await Send(first, "a goal");

            using var second = Reopen(first);

            Assert.Equal(GoalTestTiming.FirstReviewAndWhenMet, second.Criteria.TestTiming);
        });
    }
}
