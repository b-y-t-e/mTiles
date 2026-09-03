using System.Globalization;
using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>How borrowed text is put into a prompt without the text taking the prompt over.</summary>
public class GoalPromptBuilderTests
{
    [Fact]
    public void Everything_borrowed_is_capped_not_only_the_working_tree()
    {
        // The plan and the previous review are the tool's own output and have no natural size — a plan
        // for a large change runs to pages — so capping the tree and leaving these unbounded was
        // capping the smaller half of a prompt that has to fit on a command line.
        var builder = new GoalPromptBuilder();
        var hugePlan = new string('p', GoalPromptBuilder.MaxBorrowedChars * 4);
        var hugeReview = new string('r', GoalPromptBuilder.MaxBorrowedChars * 4);

        var prompt = builder.BuildImplement(
            new GoalPromptBuilder.ImplementContext("a goal", hugePlan, hugeReview));

        Assert.Contains("truncated at", prompt);
        Assert.True(prompt.Length < GoalPromptBuilder.MaxBorrowedChars * 4,
            $"prompt was {prompt.Length} characters, which is not a budget");
    }

    [Fact]
    public void An_attempt_is_told_which_attempt_it_is_and_what_the_earlier_ones_decided()
    {
        var prompt = new GoalPromptBuilder().BuildImplement(
            new GoalPromptBuilder.ImplementContext(
                "a goal",
                AttemptLog: ["Attempt 1: added a cache. Rejected: rewriting the parser — too broad."],
                Attempt: 4,
                Attempts: 5));

        // A model that does not know it is nearly out of attempts keeps experimenting; the last one
        // should be the safe version rather than a fresh idea.
        Assert.Contains("attempt 4 of 5", prompt);
        Assert.Contains("Rejected: rewriting the parser", prompt);

        // And it is asked to leave the same note behind for the attempt after it.
        Assert.Contains("\"Rejected:\"", prompt);
    }

    [Fact]
    public void The_working_tree_gives_way_before_the_note_about_earlier_attempts()
    {
        // A reversal of what this test used to assert, and the argument is that one of the two is
        // recoverable. These tools run in the workspace with their own tools: a dropped diff is one
        // `git diff HEAD` away. A note about the path an earlier attempt tried and backed out of is
        // recoverable by nothing at all — and Fit only descends this ladder on a large working tree
        // after several attempts, which is the exact run where that note is worth most.
        //
        // With line breaks: a single 40 000-character line is not a large diff to anything downstream,
        // and a fixture that only looks big is how a fitting test passes for the wrong reason.
        var tree = GoalDiffContext.Compose(
            string.Join("\n", Enumerable.Range(0, 4_000).Select(i => $"+ line {i}")), "src/New.cs")!;
        var log = new[] { "Attempt 1: tried the cache. Rejected: the parser rewrite, too broad." };

        var squeezed = new GoalPromptBuilder().BuildImplement(
            new GoalPromptBuilder.ImplementContext("a goal", GitDiff: tree, AttemptLog: log),
            // Tight enough to force the rung where the tree gives way. The two survive together above
            // it, which is the point: this is about the order they are given up in, not about making
            // the note fragile.
            budget: 2_000);

        Assert.Contains("Rejected: the parser rewrite", squeezed);
        Assert.DoesNotContain("Current state of the working tree", squeezed);
    }

    [Fact]
    public void A_diff_of_a_markdown_file_cannot_close_the_fence_around_it()
    {
        // A three-backtick fence is ended by the first fence inside the diff, and everything after it —
        // the rest of the diff included — reads as prose to whatever is being asked to read it.
        var tick = new string('`', 3);
        var diffOfMarkdown = $"diff --git a/README.md b/README.md\n+{tick}\n+some code\n+{tick}";

        var block = GoalPromptBuilder.Block("Current state of the working tree", diffOfMarkdown);

        var fence = block.Split('\n')[1];
        Assert.True(fence.Length > 3, $"fence was {fence.Length} backticks, content holds a run of 3");
        Assert.EndsWith($"{fence}\n\n", block);
    }

    /// <summary>
    /// Every prompt whose answer the user reads is told to answer in the user's own language.
    /// </summary>
    /// <remarks>
    /// The tool decides this for itself otherwise, and inconsistently: the same run asked its questions
    /// in Polish and handed back an English plan, because every instruction around it — the worked
    /// examples especially — is written in English.
    /// </remarks>
    [Theory]
    [InlineData("clarify")]
    [InlineData("plan")]
    [InlineData("implement")]
    [InlineData("review")]
    public void Every_prompt_the_user_reads_asks_for_the_users_own_language(string which)
    {
        var builder = new GoalPromptBuilder();
        var prompt = which switch
        {
            "clarify" => builder.BuildClarify("a goal", []),
            "plan" => builder.BuildPlan("a goal", []),
            "implement" => builder.BuildImplement(new GoalPromptBuilder.ImplementContext("a goal")),
            _ => builder.BuildReview("a goal", "a diff"),
        };

        Assert.Contains("same language as the goal", prompt);

        // The carve-out travels with it, always. Without it a model answering in Polish translates the
        // severity words, the json keys and the two markers that are parsed rather than read — and
        // the machinery stops seeing them without saying anything.
        Assert.Contains("marker words", prompt);
        Assert.Contains("in English", prompt);
    }

    /// <summary>
    /// The two health checks reach the prompts as instructions, and switching one off takes its
    /// sentence with it.
    /// </summary>
    /// <remarks>
    /// What this replaces was a verify command the tile ran itself, gating completion on an exit code —
    /// which only worked in a repository that was already green. The sentence about pre-existing
    /// failures is the load-bearing one: without it a tool told the tests must pass, in front of a
    /// suite that was already red, spends the goal's attempts fixing somebody else's tests.
    /// </remarks>
    [Fact]
    public void The_health_checks_are_asked_for_in_the_prompts_the_work_is_done_from()
    {
        var all = new GoalPromptBuilder(() => new GoalCompletionCriteria());

        foreach (var prompt in (string[])[
            all.BuildImplement(new GoalPromptBuilder.ImplementContext("a goal")),
            all.BuildReview("a goal", null),
        ])
        {
            Assert.Contains("the project builds", prompt);
            Assert.Contains("the project's tests pass", prompt);
            Assert.Contains("already", prompt);
        }

        // The reviewer is told to go and find out, rather than to read it off the diff.
        Assert.Contains("Establish these yourself", all.BuildReview("a goal", null));

        // And, because going and finding out means running a build that writes, it is told in words
        // what the withdrawn read-only sandbox used to say: run them, change nothing else. The
        // implementer is not — writing files is the whole of its job.
        Assert.Contains("do not edit, create or delete any file",
            all.BuildReview("a goal", null));
        Assert.DoesNotContain("do not edit, create or delete any file",
            all.BuildImplement(new GoalPromptBuilder.ImplementContext("a goal")));

        var buildOnly = new GoalPromptBuilder(
            () => new GoalCompletionCriteria { RequireTestsPass = false });
        var implement = buildOnly.BuildImplement(new GoalPromptBuilder.ImplementContext("a goal"));
        Assert.Contains("the project builds", implement);
        Assert.DoesNotContain("tests pass", implement);

        var neither = new GoalPromptBuilder(
            () => new GoalCompletionCriteria { RequireBuild = false, RequireTestsPass = false });
        Assert.DoesNotContain("the project builds",
            neither.BuildImplement(new GoalPromptBuilder.ImplementContext("a goal")));
    }

    /// <summary>
    /// The reviewer is warned that the working tree is not all one change — but only where that block
    /// really is everything uncommitted.
    /// </summary>
    /// <remarks>
    /// The other half of the guard below, and the half that is about the run finishing rather than
    /// about files surviving. A <c>[scope]</c> finding lands as a warning against a tolerance of zero,
    /// and the user's own files stay where they are — so every remaining attempt reviews the same tree,
    /// finds the same thing, and a goal that was reached is reported as not reached.
    /// </remarks>
    [Fact]
    public void The_reviewer_is_told_not_to_report_the_users_own_parallel_work()
    {
        foreach (var budget in (int?[])[null, 400])
        {
            var fallback = new GoalPromptBuilder()
                .BuildReview("add cart discounts", new string('x', 20_000), budget: budget);

            Assert.Contains("unrelated work by the user", fallback);
            Assert.Contains("do not report their unrelated changes as a finding", fallback);

            // And gone the moment the block is only what this run changed. The sentence asks the
            // reviewer for a distinction it has no data for, and a finding it swallows over that leaves
            // no trace anywhere — where an invented one is at least in the transcript. It is kept for
            // the fallback and nowhere else.
            var scoped = new GoalPromptBuilder()
                .BuildReview("add cart discounts", new string('x', 20_000), scoped: true, budget);

            Assert.DoesNotContain("unrelated work by the user", scoped);
        }
    }

    /// <summary>
    /// The implementation is forbidden to undo work it did not do, and is given somewhere else to put
    /// a finding about it.
    /// </summary>
    /// <remarks>
    /// <para>A data-loss guard. The review is handed the whole working tree as "the changes that were
    /// just made", so a user working in the terminal tile next door has their own change reported as a
    /// finding — and <c>GoalTranscript.Feedback</c> passes it back under "Fix these findings". The next
    /// attempt reverted the user's files and deleted the ones they had not committed, which is the only
    /// thing that makes such a finding go away.</para>
    /// <para>The last clause is asserted separately because it is the one that makes the rest
    /// survivable: a warning counts against a tolerance of zero, so a run cannot finish while the
    /// finding stands. Forbidding the repair with no way past it leaves the tool holding something it
    /// may neither ignore nor fix.</para>
    /// <para>Never trimmed, for the reason the answer-language line is not: the prompts that reach the
    /// last rung are the large ones, and a large prompt means a busy working tree — which is exactly
    /// the run with somebody else's work in it.</para>
    /// </remarks>
    [Fact]
    public void The_implementation_is_told_not_to_undo_work_that_is_not_its_own()
    {
        var builder = new GoalPromptBuilder();

        foreach (var budget in (int?[])[null, 400])
        {
            var prompt = builder.BuildImplement(
                new GoalPromptBuilder.ImplementContext(
                    "add cart discounts",
                    GitDiff: new string('x', 20_000),
                    ReviewFeedback: "Warning: unrelated changes glued onto this one"),
                budget);

            Assert.Contains("Never revert, delete or restore a file", prompt);
            Assert.Contains("say so in your closing line instead", prompt);
        }
    }

    /// <summary>
    /// The plan is asked to stay minimal, in the words that stop it inflating.
    /// </summary>
    /// <remarks>
    /// This phase's characteristic failure is an essay: the user's goal restated at four times the
    /// length, steps grouped under invented headings, each one annotated with the principle it serves.
    /// It matters because the plan is what the user approves and what every implement prompt then
    /// carries — scope invented here is scope the run spends its attempts building.
    /// </remarks>
    [Fact]
    public void The_plan_is_asked_for_the_users_goal_tightened_rather_than_expanded()
    {
        var prompt = new GoalPromptBuilder().BuildPlan("add cart discounts", []);

        Assert.Contains("only tighter", prompt);
        Assert.Contains("not add scope, requirements or detail they did not give you", prompt);
        Assert.Contains("Do not invent files, requirements or constraints", prompt);
        Assert.Contains("do not name the principles above", prompt);

        // The build and the tests are the implementation's business and the review's. In a plan they
        // only ever came back as two more steps saying "run the tests".
        Assert.DoesNotContain("the project builds", prompt);
    }

    /// <summary>
    /// The one prompt with no goal to take a language from asks for the machine's own.
    /// </summary>
    /// <remarks>
    /// Every other prompt says "answer in the language of the goal above". This one is reached from the
    /// + button over an uncommitted working tree — nothing has been typed, and a diff is written in
    /// English whoever wrote it. The answer went into the composer as the user's own goal, so one
    /// phase with nothing to read from set the language of the entire run.
    /// </remarks>
    [Fact]
    public void Detecting_a_goal_answers_in_the_language_this_machine_is_set_up_in()
    {
        Assert.Contains("Answer in Polish",
            GoalPromptBuilder.AnswerInSystemLanguage(new CultureInfo("pl-PL")));
        Assert.Contains("Answer in German",
            GoalPromptBuilder.AnswerInSystemLanguage(new CultureInfo("de")));

        // Nothing to say where the prompt is already in that language, and nothing to say where the
        // machine did not answer the question.
        Assert.Equal("", GoalPromptBuilder.AnswerInSystemLanguage(new CultureInfo("en-GB")));
        Assert.Equal("", GoalPromptBuilder.AnswerInSystemLanguage(CultureInfo.InvariantCulture));

        // And it is really in the prompt, whatever this machine happens to be set to.
        Assert.Contains(GoalPromptBuilder.AnswerInSystemLanguage(CultureInfo.CurrentUICulture),
            new GoalPromptBuilder().BuildDetectGoal("a diff"));
    }

    /// <summary>
    /// The instruction survives every fitting step, because it is fixed text rather than borrowed.
    /// </summary>
    /// <remarks>
    /// The prompts that reach the last rung are the large ones, and a large one is exactly where the
    /// user least wants an answer they have to translate.
    /// </remarks>
    [Fact]
    public void The_language_instruction_is_not_what_gives_way_when_the_prompt_will_not_fit()
    {
        var fitted = new GoalPromptBuilder().BuildReview(
            new string('g', 5_000), string.Join("\n", Enumerable.Repeat("+ a line of diff", 4_000)),
            budget: 4_000);

        Assert.True(CommandLineLength.Quoted(fitted) <= 4_000);
        Assert.Contains("same language as the goal", fitted);
    }

    /// <summary>
    /// Detection is the one prompt without it, and that is not an oversight.
    /// </summary>
    /// <remarks>
    /// It runs on an empty tile: there is no goal yet, so there is nothing of the user's writing to
    /// point at. Naming a language it cannot know would be guessing, and the sentence it produces goes
    /// into the composer for the user to edit anyway.
    /// </remarks>
    [Fact]
    public void Detection_has_no_language_to_match_and_does_not_pretend_otherwise()
    {
        var prompt = new GoalPromptBuilder().BuildDetectGoal("a diff");

        Assert.DoesNotContain("same language as the goal", prompt);
    }

    /// <summary>
    /// A run started by "Set goal &amp; run" tells the tool nobody is waiting, and shows it the answer
    /// that shape of run wants.
    /// </summary>
    /// <remarks>
    /// The example is the half that is easy to leave behind: the ordinary one shows
    /// <c>needsClarification</c> true with a question under it, and an example contradicting the
    /// instruction above it is the one thing a model follows — which here is a round of questions
    /// asked of nobody.
    /// </remarks>
    [Fact]
    public void A_run_that_stops_for_nothing_asks_for_no_questions()
    {
        var builder = new GoalPromptBuilder();

        var asking = builder.BuildClarify("a goal", []);
        var running = builder.BuildClarify("a goal", [], noQuestions: true);

        Assert.Contains("will not stop for questions", running);
        Assert.Contains("Set needsClarification to false", running);
        Assert.Contains("\"needsClarification\":false", running);
        Assert.DoesNotContain("\"needsClarification\":true", running);

        // The ordinary round is untouched: it still asks the tool to decide, and still shows it a
        // question.
        Assert.Contains("Decide whether the goal is specific", asking);
        Assert.Contains("\"needsClarification\":true", asking);
        Assert.DoesNotContain("will not stop for questions", asking);

        // The carve-out every prompt carries — see the language test above.
        Assert.Contains("same language as the goal", running);
    }

    /// <summary>
    /// The assumptions this run asks for are allowed to exist: the prompt asks for a paragraph and
    /// must not, one line later, forbid everything but the json block.
    /// </summary>
    /// <remarks>
    /// It did — the example was borrowed from the ordinary round's "one fenced json block and nothing
    /// else" — and the example is what a model follows, so the one thing a user sees before a plan
    /// written without questions was the thing least likely to be written. The paragraph is read from
    /// what stands before the fence, so the order is the assertion: assumptions first, block last.
    /// </remarks>
    [Fact]
    public void A_run_that_stops_for_nothing_leaves_room_for_what_it_is_assuming()
    {
        var running = new GoalPromptBuilder().BuildClarify("a goal", [], noQuestions: true);

        Assert.Contains("what you are assuming", running);
        Assert.DoesNotContain("json block and nothing else", running);
        Assert.True(running.IndexOf("paragraph first", StringComparison.Ordinal)
                    < running.IndexOf("```json", StringComparison.Ordinal));
    }
}
