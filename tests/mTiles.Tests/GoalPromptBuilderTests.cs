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

        var prompt = builder.BuildImplement("a goal", hugePlan, hugeReview, gitDiff: null);

        Assert.Contains("truncated at", prompt);
        Assert.True(prompt.Length < GoalPromptBuilder.MaxBorrowedChars * 4,
            $"prompt was {prompt.Length} characters, which is not a budget");
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
