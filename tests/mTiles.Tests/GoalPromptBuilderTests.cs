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
    public void The_implementer_is_shown_the_build_error_rather_than_a_review_of_it()
    {
        // The verify output used to go to the review alone, so what reached whoever had to fix a broken
        // build was the reviewer's account of the compiler — a line and column turned into "there is a
        // type mismatch somewhere in the cart code".
        var prompt = new GoalPromptBuilder().BuildImplement(
            new GoalPromptBuilder.ImplementContext(
                "a goal",
                VerifyOutput: "src/Cart.cs(42,17): error CS1503: cannot convert int to string",
                ReviewFeedback: "error: the cart does not build"));

        Assert.Contains("CS1503", prompt);
        Assert.Contains("src/Cart.cs(42,17)", prompt);
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
    public void A_prompt_the_command_line_could_not_carry_says_that_rather_than_crashing()
    {
        // Over the limit Process.Start throws a Win32Exception whose text says nothing about length,
        // so the tile reported that the tool had failed and offered to try again — which could only
        // fail identically, for ever.
        var tooLong = new string('x', 40_000);

        var ex = Assert.Throws<InvalidOperationException>(
            () => AiProcessRunner.RunPlainAsync("claude.cmd", tooLong, ".", new CodexToolRunner())
                .GetAwaiter().GetResult());

        Assert.Contains("command line", ex.Message);
        Assert.Contains(".cmd shim", ex.Message);
    }

    [Fact]
    public void A_tool_that_reads_stdin_is_not_held_to_the_command_line_limit()
    {
        // Claude takes its prompt on standard input, which removes the limit entirely. The other three
        // are left alone: opting in is a claim about somebody else's CLI, and a tool that does not read
        // stdin would sit waiting for input that never arrives.
        // Through the interface: the default lives there, so asking the concrete type would not see it.
        Assert.True(((IAiToolRunner)new ClaudeToolRunner()).AcceptsPromptOnStdin);
        Assert.False(((IAiToolRunner)new CodexToolRunner()).AcceptsPromptOnStdin);
        Assert.False(((IAiToolRunner)new OpenCodeToolRunner()).AcceptsPromptOnStdin);
        Assert.False(((IAiToolRunner)new PiToolRunner()).AcceptsPromptOnStdin);
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
}
