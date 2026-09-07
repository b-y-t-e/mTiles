using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What the review prompt asks the reviewer to <em>do</em>, as opposed to what it asks it to obey.
/// </summary>
/// <remarks>
/// <para>The rules the code must follow have been in this prompt from the start; the method for
/// finding out whether it follows them was never in it at all. Read back from this repository's own
/// goal logs, twenty-one reviews at <c>effort Medium</c> came back with one finding or none, eight of
/// them empty — against twenty-one at <c>High</c> on another machine where not one came back empty.
/// The three additions pinned here are the parts of that gap the prompt can close: go and read the
/// files, work down a list rather than free-associating, and answer with everything found rather than
/// with the worst of it.</para>
/// <para>Each is pinned at both sizes, because all three have a short form for a prompt that has to
/// fit a Windows command line and the short forms are where a well-meaning edit will quietly drop the
/// clause that was doing the work.</para>
/// </remarks>
public class GoalReviewDepthTests
{
    /// <summary>The budget that forces every fitting step, from <c>GoalPromptBuilderTests</c>.</summary>
    private const int NoRoom = 4_000;

    private static string Roomy() => new GoalPromptBuilder().BuildReview("a goal", "a diff");

    private static string Cramped() => new GoalPromptBuilder().BuildReview(
        new string('g', 5_000),
        string.Join("\n", Enumerable.Repeat("+ a line of diff", 4_000)),
        budget: NoRoom);

    /// <summary>
    /// The reviewer is told to open the files, and told that new ones are in the block by name only.
    /// </summary>
    /// <remarks>
    /// The sentence that survives at every size is the one about new files, and it is the one that had
    /// a measured cost: no form of <c>git diff HEAD</c> carries an untracked file's contents, so on
    /// every unscoped path — a goal detected from the working tree, the Review button, the review of a
    /// tree an attempt left alone — the file at the centre of the change reached the reviewer as a
    /// filename. Two reviews of the same goal twenty minutes apart: the one that thought to open the
    /// new class wrote 1 784 characters of analysis, the one that did not wrote "Looks consistent.
    /// Build succeeds, all 2350 tests pass."
    /// </remarks>
    [Fact]
    public void The_reviewer_is_told_to_read_the_files_and_that_new_ones_are_named_and_not_shown()
    {
        foreach (var prompt in (string[])[Roomy(), Cramped()])
        {
            Assert.Contains("map, not the evidence", prompt);
            Assert.Contains("open the files it names", prompt);
            Assert.Contains("listed by name alone", prompt);
        }

        // The advice about how much of a large file to read is the part that gives way.
        Assert.Contains("do not judge on the fragment that happened to fit", Roomy());
    }

    /// <summary>
    /// There is a list to work down, and permission to pass over a category in silence.
    /// </summary>
    /// <remarks>
    /// The permission is not politeness. Ten headings with no way to answer "nothing here" is an
    /// invitation to write something under each, which is the one failure mode a sweep introduces —
    /// so it is asserted at both sizes alongside the headings themselves. Four of these had never been
    /// named in this prompt in any form.
    /// </remarks>
    [Fact]
    public void The_review_has_a_list_to_work_down_and_may_skip_a_category_silently()
    {
        foreach (var prompt in (string[])[Roomy(), Cramped()])
        {
            foreach (var heading in (string[])[
                "correctness", "tests", "error handling", "concurrency", "security", "performance"])
                Assert.Contains(heading, prompt);

            Assert.Contains("silently", prompt);
        }
    }

    /// <summary>
    /// The example shows a list of findings, and every entry's detail names a failing case.
    /// </summary>
    /// <remarks>
    /// <para>The example is the part of a prompt a model copies hardest, and it used to show exactly
    /// one finding. The published comparison of "report the primary issue" against "enumerate what you
    /// find" moves the same model from 1.0 findings per review to 3.1 and resolves 27% more cases.</para>
    /// <para>The <c>detail</c> assertion is the other half and is what keeps the first from being
    /// pressure to invent: the example demonstrates naming the input and the wrong result, and the
    /// prompt asks for it in words. A reviewer that cannot write the failing case has generally found
    /// a suspicion. That much survives into the short form; the second and third entries do not.</para>
    /// </remarks>
    [Fact]
    public void The_example_lists_findings_and_each_one_carries_its_failing_case()
    {
        var roomy = Roomy();

        // Three entries, which is what makes it an example of a list rather than of a finding.
        Assert.Equal(3, roomy.Split("\"severity\":").Length - 1);
        Assert.Contains("\"severity\":\"blocker\"", roomy);
        Assert.Contains("\"severity\":\"suggestion\"", roomy);

        // Asked for in words as well as shown, at both sizes.
        foreach (var prompt in (string[])[roomy, Cramped()])
        {
            Assert.Contains("all of them rather than only the worst", prompt);
            Assert.Contains("the wrong result", prompt);

            // And the empty answer stays plainly available, or the two above become a quota.
            Assert.Contains("Send an empty findings list", prompt);
        }

        // One entry when there is no room, and it still carries the failing case rather than the bare
        // "Sum() runs before ApplyDiscount()" the single-finding example used to.
        var cramped = Cramped();
        Assert.Equal(1, cramped.Split("\"severity\":").Length - 1);
        Assert.Contains("is charged 100", cramped);
    }

    /// <summary>
    /// What is missing is a finding, weighed by what happens without it.
    /// </summary>
    /// <remarks>
    /// A whole class of finding the prompt could not produce, because an absence leaves no line in a
    /// diff. The second assertion is the guard: weighed by the fact that something is absent, every
    /// gap is worth reporting and the review becomes a wish list — so where there is no room for the
    /// weighing clause the question is dropped outright rather than shortened.
    /// </remarks>
    [Fact]
    public void The_review_asks_what_is_missing_only_where_it_can_also_say_how_to_weigh_it()
    {
        var roomy = Roomy();
        Assert.Contains("ask what is not there", roomy);
        Assert.Contains("weighed by what happens without it", roomy);
        Assert.Contains("A gap that changes nothing is not a finding", roomy);

        Assert.DoesNotContain("ask what is not there", Cramped());
    }

    /// <summary>
    /// The reviewer is no longer told that running the commands is instead of reading the change.
    /// </summary>
    /// <remarks>
    /// The health rules were the only sentence in this prompt naming an action, and the action they
    /// named excluded the diff — "by running this project's own commands rather than by reading the
    /// diff". A reviewer following it literally runs the build, reports it green and stops, which is
    /// what the shortest reviews in the logs are. The instruction to establish the two facts by running
    /// something is kept; only the clause discouraging the reading is gone.
    /// </remarks>
    [Fact]
    public void The_health_check_no_longer_tells_the_reviewer_not_to_read_the_diff()
    {
        var prompt = new GoalPromptBuilder(() => new GoalCompletionCriteria())
            .BuildReview("a goal", "a diff");

        Assert.Contains("Establish these yourself", prompt);
        Assert.DoesNotContain("rather than by reading", prompt);
    }

    /// <summary>
    /// The sweep follows <c>QualityRules</c>' scope instead of quietly re-opening it.
    /// </summary>
    /// <remarks>
    /// With SOLID switched off the prompt says so outright, because silence about a principle does not
    /// switch it off. A sweep whose last line asked for "the Clean Code and SOLID rules above" would
    /// contradict that in the same prompt — and the finding lands as a warning against a tolerance of
    /// zero, which blocks the run.
    /// </remarks>
    [Fact]
    public void The_sweep_does_not_ask_for_the_solid_findings_the_rules_have_just_ruled_out()
    {
        var off = new GoalPromptBuilder(() => new GoalCompletionCriteria
        {
            Solid = new SolidPrinciples
            {
                SingleResponsibility = false,
                OpenClosed = false,
                Liskov = false,
                InterfaceSegregation = false,
                DependencyInversion = false,
            },
        }).BuildReview("a goal", "a diff");

        Assert.Contains("SOLID principles are out of scope", off);
        Assert.Contains("- the Clean Code rules stated above", off);
        Assert.DoesNotContain("the Clean Code and SOLID rules stated above", off);

        Assert.Contains("the Clean Code and SOLID rules stated above", Roomy());
    }

    /// <summary>
    /// A review asked for on its own claims no attempts, because it spends none.
    /// </summary>
    /// <remarks>
    /// <para><c>Reviewed</c> has said no number since it was written — "none were spent and none were
    /// meant to be" — and <c>Met</c>, which the same button reaches when the tree passes, said one
    /// anyway. Read back from a real log: pressing Review after a finished run printed "Goal completed
    /// after 4 attempts" over four attempts the press had no part in, and printed it again on the next
    /// press.</para>
    /// <para>Which of the two reasons that button ends on is deliberately left alone. <c>Met</c> is
    /// what keeps Continue off a summary with nothing to continue towards, so the honest sentence is
    /// bought here rather than by handing the button the other reason and a Continue with it.</para>
    /// </remarks>
    [Fact]
    public void A_review_that_spent_no_attempts_does_not_report_any()
    {
        var pressed = GoalCompletionPolicy.Summarise(GoalStopReason.Met, 0);

        Assert.DoesNotContain("attempt", pressed);
        Assert.Contains("meets the goal", pressed);

        // And a run that did spend them still says how many. "after 0 attempts" would describe a run
        // that failed to start; this is the difference between the two.
        Assert.Contains("after 1 attempt", GoalCompletionPolicy.Summarise(GoalStopReason.Met, 1));
        Assert.Contains("after 4 attempts", GoalCompletionPolicy.Summarise(GoalStopReason.Met, 4));
    }
}
