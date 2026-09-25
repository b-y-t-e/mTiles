using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Which step of a goal run has the tests run: the rule as a table, apart from the loop that carries
/// it out.
/// </summary>
public class GoalTestPolicyTests
{
    private static GoalCompletionCriteria Criteria(bool tests, GoalTestTiming timing) =>
        new() { RequireTestsPass = tests, TestTiming = timing };

    /// <summary>Every row: the switch, the timing, the attempt — and what the implementation, the
    /// review, and an accepting review that did not run them are each told.</summary>
    [Theory]
    //          tests  timing                                    attempt  impl   review  owed after accept
    [InlineData(true,  GoalTestTiming.EveryReview,            1,       true,  true,   false)]
    [InlineData(true,  GoalTestTiming.EveryReview,            3,       true,  true,   false)]
    [InlineData(true,  GoalTestTiming.WhenMet,                1,       false, false,  true)]
    [InlineData(true,  GoalTestTiming.WhenMet,                3,       false, false,  true)]
    [InlineData(true,  GoalTestTiming.FirstReviewAndWhenMet,  1,       false, true,   false)]
    [InlineData(true,  GoalTestTiming.FirstReviewAndWhenMet,  2,       false, false,  true)]
    [InlineData(false, GoalTestTiming.EveryReview,            1,       false, false,  false)]
    [InlineData(false, GoalTestTiming.WhenMet,                2,       false, false,  false)]
    [InlineData(false, GoalTestTiming.FirstReviewAndWhenMet,  1,       false, false,  false)]
    public void When_the_tests_are_run(bool tests, GoalTestTiming timing, int attempt,
        bool inImplement, bool inReview, bool owedAfterAcceptance)
    {
        var criteria = Criteria(tests, timing);
        var reviewRan = GoalTestPolicy.InReview(criteria, attempt);

        Assert.Equal(inImplement, GoalTestPolicy.InImplement(criteria));
        Assert.Equal(inReview, reviewRan);
        Assert.Equal(owedAfterAcceptance, GoalTestPolicy.OwesTestRun(criteria, reviewRan));
    }

    /// <summary>Whether the review ran the tests is not persisted, so after a restart it reads false.
    /// Under every review that must not become a debt: the goal finishes rather than reviewing again.</summary>
    [Fact]
    public void Every_review_owes_nothing_after_a_restart() =>
        Assert.False(GoalTestPolicy.OwesTestRun(Criteria(true, GoalTestTiming.EveryReview), reviewRanTests: false));

    [Fact]
    public void Every_timing_has_a_label_that_names_it_back()
    {
        foreach (var timing in Enum.GetValues<GoalTestTiming>())
        {
            Assert.Equal(timing, GoalTestPolicy.FromLabel(GoalTestPolicy.Label(timing)));
            Assert.False(string.IsNullOrWhiteSpace(GoalTestPolicy.Description(timing)));
        }

        Assert.Equal(GoalTestTiming.EveryReview, GoalTestPolicy.FromLabel("a word from a newer build"));
        Assert.Equal(GoalTestTiming.EveryReview, GoalTestPolicy.FromLabel(null));
    }

    /// <summary>A goal file written before the timing existed reads as what that tile did; one
    /// carrying a word this build does not know reads as the most thorough answer, never the
    /// cheapest.</summary>
    [Theory]
    [InlineData("{}", GoalTestTiming.EveryReview)]
    [InlineData("{\"TestTiming\":\"WhenMet\"}", GoalTestTiming.WhenMet)]
    [InlineData("{\"TestTiming\":\"SomethingNewer\"}", GoalTestTiming.EveryReview)]
    public void The_timing_is_read_forgivingly(string json, GoalTestTiming expected)
    {
        var criteria = System.Text.Json.JsonSerializer.Deserialize<GoalCompletionCriteria>(json);

        Assert.Equal(expected, criteria!.TestTiming);
    }

    [Fact]
    public void A_copy_keeps_the_timing_and_the_commit_switch()
    {
        var copy = new GoalCompletionCriteria
        {
            TestTiming = GoalTestTiming.FirstReviewAndWhenMet,
            CommitWhenDone = true,
        }.Copy();

        Assert.Equal(GoalTestTiming.FirstReviewAndWhenMet, copy.TestTiming);
        Assert.True(copy.CommitWhenDone);
    }
}
