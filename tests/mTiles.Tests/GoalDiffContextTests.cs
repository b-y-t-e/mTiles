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

    // ── The parts that only move when something is cut ──

    [Fact]
    public void The_untracked_list_follows_the_transport_like_everything_else()
    {
        // It was a constant while the diff and the summary both followed the transport, so off the
        // command line the diff was given forty thousand characters and the list of new files was
        // still held to a thousand — about seventeen paths, on a tree where the new files were the
        // work.
        Assert.Equal(GoalDiffContext.MaxUntrackedChars, GoalDiffContext.CapsFor(8_191).Untracked);
        Assert.Equal(
            GoalDiffContext.MaxUntrackedCharsOffCommandLine, GoalDiffContext.CapsFor(null).Untracked);

        var names = string.Join("\n", Enumerable.Range(0, 90).Select(i => $"src/Feature{i}/New{i}.cs"));

        Assert.Contains("file list truncated",
            GoalDiffContext.Compose("", names, null, null, GoalDiffContext.CapsFor(8_191))!);
        Assert.DoesNotContain("file list truncated",
            GoalDiffContext.Compose("", names, null, null, GoalDiffContext.CapsFor(null))!);
    }

    [Fact]
    public void A_truncated_list_says_how_many_files_there_are()
    {
        // "… truncated at 1000 characters" tells a model there is some more. A count is a fact it can
        // act on, and it is what makes the detection prompt's invitation — go and read the rest — an
        // instruction rather than a suggestion.
        var names = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"src/Feature{i}/New{i}.cs"));

        var composed = GoalDiffContext.Compose("", names, null, null, GoalDiffContext.CapsFor(8_191))!;

        Assert.Contains("of 200 files shown", composed);
        Assert.DoesNotContain("characters", composed);
    }

    [Fact]
    public void A_clipped_summary_keeps_the_largest_changes_and_the_total()
    {
        // git writes the stat in path order, so a summary clipped to a quarter is the quarter whose
        // paths sort first — one directory, chosen by its initial. What somebody naming the goal would
        // have looked at is the largest part of the work.
        var rows = Enumerable.Range(0, 300)
            .Select(i => $" src/Area{i:D3}/File{i}.cs | {(i == 299 ? 9000 : 1)} +-");
        var stat = string.Join("\n", [.. rows, " 300 files changed, 9299 insertions(+)"]);

        var composed = GoalDiffContext.Compose(
            "diff --git a/x b/x", null, null, stat, GoalDiffContext.CapsFor(8_191))!;

        // The one file that carries the change, which path order buried at the very end.
        Assert.Contains("File299.cs", composed);

        // And git's own total, which path order puts last and a cut from the end destroys — the only
        // line in the part that describes the change everywhere rather than in the rows that fitted.
        Assert.Contains("300 files changed", composed);
        Assert.Contains("of 300 files shown", composed);
    }

    [Fact]
    public void A_summary_that_fits_is_left_exactly_as_git_wrote_it()
    {
        // The re-ordering is what a cut costs, not a house style. Below the cap the block is what git
        // produced, in git's order, which is what keeps an ordinary working tree reading as it did.
        var stat = " src/Aaa.cs | 1 +\n src/Bbb.cs | 500 +++\n 2 files changed, 501 insertions(+)";

        var composed = GoalDiffContext.Compose(null, null, null, stat)!;

        // Trimmed, which is what Clip has always done to the block as a whole and is not the
        // re-ordering: the rows are in git's order and the total is still last.
        Assert.Equal($"Changed files:\n{stat.Trim()}", composed);
    }

    [Fact]
    public void A_row_git_could_not_count_keeps_its_name()
    {
        // A binary file and a mode change carry no number. Sorted as zero rather than dropped: the name
        // still says where the change reaches, and a name is the part of this block that survives.
        var sorted = GoalDiffContext.StatBySize(
            " assets/logo.png | Bin 0 -> 12 bytes\n src/Cart.cs | 40 ++--\n 2 files changed, 40 insertions(+)");

        Assert.StartsWith(" 2 files changed", sorted);
        Assert.Contains("assets/logo.png", sorted);
        Assert.True(sorted.IndexOf("Cart.cs", StringComparison.Ordinal)
                    < sorted.IndexOf("logo.png", StringComparison.Ordinal));
    }

    [Fact]
    public void A_modified_binary_sorts_by_nothing_rather_than_by_its_size_in_bytes()
    {
        // git writes byte counts in the position a text file writes line counts, so a rewritten image
        // read literally outweighs every real change in the repository — and a clipped summary would
        // then keep the binaries and drop the work, the exact inverse of why the ordering exists.
        var sorted = GoalDiffContext.StatBySize(
            " assets/big.png | Bin 300000 -> 250000 bytes\n"
            + " assets/gone.png | Bin 524288 -> 0 bytes\n"
            + " src/Cart.cs | 40 ++--\n"
            + " 3 files changed, 40 insertions(+)");

        Assert.True(sorted.IndexOf("Cart.cs", StringComparison.Ordinal)
                    < sorted.IndexOf("big.png", StringComparison.Ordinal));
        Assert.True(sorted.IndexOf("Cart.cs", StringComparison.Ordinal)
                    < sorted.IndexOf("gone.png", StringComparison.Ordinal));
    }
}
