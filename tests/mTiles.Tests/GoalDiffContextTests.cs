using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What the working tree looks like when it reaches a prompt. This was the last piece of the Goal
/// tile's run with no test on it, and it was the one with a bug in it.
/// </summary>
public class GoalDiffContextTests
{
    [Fact]
    public void Nothing_changed_is_nothing_to_say()
    {
        Assert.Null(GoalDiffContext.Compose("", ""));
        Assert.Null(GoalDiffContext.Compose(null, null));
        Assert.Null(GoalDiffContext.Compose("   \n ", "\t"));
    }

    [Fact]
    public void A_diff_alone_is_passed_through()
    {
        Assert.Equal("diff --git a/x b/x", GoalDiffContext.Compose("diff --git a/x b/x\n", null));
    }

    [Fact]
    public void Untracked_names_alone_are_still_worth_saying()
    {
        // A first implementation that only adds files produces no diff at all. Saying nothing there
        // is what has a resumed run create every one of them a second time.
        var composed = GoalDiffContext.Compose("", "src/New.cs\nsrc/Other.cs")!;

        Assert.Contains("Untracked files", composed);
        Assert.Contains("src/New.cs", composed);
        Assert.DoesNotContain("\n\n\n", composed);
    }

    [Fact]
    public void A_huge_diff_does_not_take_the_untracked_list_down_with_it()
    {
        // The bug this exists for: the list used to be appended and the whole thing truncated
        // afterwards, so the moment the diff passed the cap the list was cut off entirely — in
        // exactly the case it was added for, a resumed run against a large implementation.
        var huge = new string('x', GoalDiffContext.MaxDiffChars * 3);
        var composed = GoalDiffContext.Compose(huge, "src/New.cs")!;

        Assert.Contains("src/New.cs", composed);
        Assert.Contains("diff truncated", composed);
        Assert.True(composed.Length < GoalDiffContext.MaxDiffChars + GoalDiffContext.MaxUntrackedChars + 500);
    }

    [Fact]
    public void A_flood_of_untracked_names_is_capped_on_its_own()
    {
        // Its own cap, far below the diff's: names cost a line each, and a tree with thousands of
        // untracked files is one where the list has stopped being information.
        var manyNames = string.Join("\n", Enumerable.Range(0, 20_000).Select(i => $"generated/file{i}.txt"));

        var composed = GoalDiffContext.Compose("diff --git a/x b/x", manyNames)!;

        Assert.Contains("diff --git a/x b/x", composed);
        Assert.Contains("file list truncated", composed);
        Assert.True(composed.Length < GoalDiffContext.MaxDiffChars);
    }

    [Fact]
    public void The_file_summary_survives_a_diff_that_does_not()
    {
        // The failure this is for, measured: 140 000 characters of diff across twenty-one files, of
        // which 6 000 reached the tool — four per cent, and by path order that four per cent was two
        // markdown files. Nothing in the block said the change went any further, so "Detect goal" named
        // a goal drawn from the fragment, confidently and about the wrong work.
        var huge = new string('x', 200_000);
        var summary = string.Join("\n", Enumerable.Range(0, 21).Select(i => $" src/File{i}.cs | 12 ++--"));

        var composed = GoalDiffContext.Compose(huge, null, null, summary)!;

        Assert.Contains("src/File20.cs", composed);
        Assert.Contains("diff truncated", composed);

        // Above the body, because whatever cuts this block again cuts it from the end — the same rule
        // the untracked names follow, and for the same reason: it is bounded by the file count rather
        // than by the size of the change.
        Assert.True(composed.IndexOf("Changed files:", StringComparison.Ordinal)
                    < composed.IndexOf('x'));
    }

    [Fact]
    public void The_caps_follow_the_transport_rather_than_being_constants()
    {
        // Six thousand is what a Windows command line allows. A tool that reads its prompt on stdin has
        // no such limit, and neither has any tool off Windows — so charging it there was paying a
        // transport cost on a channel with no transport.
        var shim = GoalDiffContext.CapsFor(8_191);
        var stdin = GoalDiffContext.CapsFor(null);

        Assert.Equal(GoalDiffContext.MaxDiffCharsOnCommandLine, shim.Diff);
        Assert.Equal(GoalDiffContext.MaxDiffCharsOffCommandLine, stdin.Diff);

        var diff = new string('x', 20_000);

        Assert.Contains("diff truncated", GoalDiffContext.Compose(diff, null, null, null, shim)!);
        Assert.DoesNotContain("diff truncated", GoalDiffContext.Compose(diff, null, null, null, stdin)!);
    }

    /// <summary>
    /// The file summary took its room from the diff rather than from nowhere.
    /// </summary>
    /// <remarks>
    /// Added without this, the worktree block grew from at most 7 000 characters to at most 10 000
    /// against the 8 191 a <c>.cmd</c> shim allows — so <c>GoalPromptBuilder.Fit</c> would have started
    /// cutting the diff harder than before the summary existed, silently, for three of the four
    /// supported tools. A block that grows has to say where the room came from.
    /// </remarks>
    [Fact]
    public void The_block_on_a_command_line_is_no_bigger_than_it_was_before_the_summary_existed()
    {
        var shim = GoalDiffContext.CapsFor(8_191);

        Assert.Equal(GoalDiffContext.MaxDiffChars, shim.Diff + shim.Summary);
        Assert.Equal(GoalDiffContext.MaxSummaryCharsOnCommandLine, shim.Summary);

        // And it is spent, not merely reserved: a real block gets both parts, neither empty.
        var composed = GoalDiffContext.Compose(
            new string('x', 50_000), null, null,
            string.Join("\n", Enumerable.Range(0, 400).Select(i => $" src/File{i}.cs | 3 +++")),
            shim)!;

        Assert.True(composed.Length < GoalDiffContext.MaxDiffChars + 500);
        Assert.Contains("src/File0.cs", composed);
        Assert.Contains("summary truncated", composed);
        Assert.Contains("diff truncated", composed);
    }

    [Fact]
    public void A_tree_that_could_not_be_read_says_so_rather_than_looking_clean()
    {
        // Silence and a clean tree are the same thing to whatever reads this, and a tool told nothing
        // has changed — when in truth nobody could find out — writes straight over work it cannot see.
        var composed = GoalDiffContext.Compose("", "", "`git diff HEAD` failed: not a git repository")!;

        Assert.Contains("could not be read", composed);
        Assert.Contains("not a git repository", composed);
    }

    [Fact]
    public void A_truncated_file_list_never_ends_mid_path()
    {
        // A path cut in half is a filename that does not exist, which is worse than one name fewer.
        var names = string.Join("\n", Enumerable.Range(0, 20_000).Select(i => $"generated/file{i}.txt"));

        var composed = GoalDiffContext.Compose(null, names)!;
        var lines = composed.Split('\n');

        // Every line that is a path is a whole one: the last is the truncation note, the first the header.
        foreach (var line in lines[1..^1])
            Assert.Matches(@"^generated/file\d+\.txt$", line);
    }

    [Fact]
    public void A_named_scope_filters_every_part_of_the_block()
    {
        const string diff =
            "diff --git a/src/Agents/X.cs b/src/Agents/X.cs\n--- a/src/Agents/X.cs\n+++ b/src/Agents/X.cs\n@@ -1 +1 @@\n-new\n+newer\n" +
            "diff --git a/src/Cart.cs b/src/Cart.cs\n--- a/src/Cart.cs\n+++ b/src/Cart.cs\n@@ -1 +1 @@\n-total\n+discounted\n";

        var composed = GoalDiffContext.Compose(diff, "src/Agents/notes.md\nREADME.md",
            summary: " src/Agents/X.cs | 2 +-\n src/Cart.cs | 2 +-\n 2 files changed",
            onlyPaths: ["src/Agents"])!;

        Assert.Contains("src/Agents/X.cs", composed);
        Assert.DoesNotContain("Cart.cs", composed);
        Assert.Contains("src/Agents/notes.md", composed);
        Assert.DoesNotContain("README.md", composed);
        // The totals described the whole change, and after a filter that is no longer what is true.
        Assert.DoesNotContain("files changed", composed);
    }

    [Fact]
    public void A_scope_matching_nothing_says_so_rather_than_looking_clean()
    {
        // A block gone silent about every file but none must not read as a tree where nothing else
        // changed — the omission was the user's, and the tool is told it was deliberate. Its own
        // wording, too: the git-failure note opens "could not be read in full", and the tree here was
        // read perfectly well.
        var composed = GoalDiffContext.Compose("diff --git a/src/Cart.cs b/src/Cart.cs\n--- a/src/Cart.cs",
            null, onlyPaths: ["docs/"])!;

        Assert.Contains("nothing in the change is inside the scope the user named", composed);
        Assert.DoesNotContain("could not be read in full", composed);
        Assert.DoesNotContain("Cart.cs", composed);
    }

    [Fact]
    public void The_note_rides_on_the_diff_alone_because_the_other_parts_empty_in_the_ordinary_course()
    {
        // A scope naming one file empties the untracked list as a matter of course — none of the
        // others matched — and a scope note beside a diff that names the scoped file would be a
        // contradiction in one block.
        var composed = GoalDiffContext.Compose(
            "diff --git a/src/Cart.cs b/src/Cart.cs\n--- a/src/Cart.cs\n+++ b/src/Cart.cs",
            "src/Other.cs\nsrc/Another.cs", onlyPaths: ["src/Cart.cs"])!;

        Assert.DoesNotContain("scope the user named", composed);
        Assert.Contains("Cart.cs", composed);
        Assert.DoesNotContain("src/Other.cs", composed);
    }

    [Fact]
    public void No_scope_filters_nothing()
    {
        var composed = GoalDiffContext.Compose("diff --git a/src/Cart.cs b/src/Cart.cs", null,
            onlyPaths: null)!;

        Assert.Contains("Cart.cs", composed);
    }
}
