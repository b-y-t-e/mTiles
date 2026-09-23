using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The composer's words as a scope: which <c>@</c> paths it names, and what a tree read keeps.
/// </summary>
public class GoalScopeFilterTests
{
    /// <summary>
    /// Which <c>@</c> tokens the composer's words carry, in order and once each; what a token names is
    /// the filesystem's answer and then git's, not this class's.
    /// </summary>
    [Theory]
    [InlineData("skup sie tylko na @src/Cart.cs", "src/Cart.cs")]
    // The quoted spelling a completion writes for a path with spaces in it.
    [InlineData("review @\"docs/my notes.md\" first", "docs/my notes.md")]
    // An @ inside a word is prose.
    [InlineData("write me at someone@example.com about this", "")]
    [InlineData("@src/A.cs and @src/B.cs, mainly @src/A.cs.", "src/A.cs|src/B.cs")]
    // A trailing slash is how a folder is typed, not part of its name.
    [InlineData("tylko @src/Agents/", "src/Agents")]
    // No slash and no dot is still a mention: a folder is spelled exactly like a word.
    [InlineData("popraw @frontend", "frontend")]
    [InlineData("sprawdz @HEAD~1", "HEAD~1")]
    [InlineData("ping @admin about the failure", "admin")]
    [InlineData("see @notes.md", "notes.md")]
    // A trailing range separator survives the sentence trim — exactly two dots, never the run.
    [InlineData("popraw formularz @master..", "master..")]
    [InlineData("popraw formularz @master..HEAD", "master..HEAD")]
    [InlineData("sprawdz @master...", "master..")]
    [InlineData("sprawdz @master.., potem reszte", "master..")]
    // An ordinary sentence still loses its full stop.
    [InlineData("zobacz @src/Cart.cs.", "src/Cart.cs")]
    [InlineData("od @v1.2.", "v1.2")]
    public void The_mentions_are_the_at_tokens_the_words_carry(string text, string expected)
    {
        string[] paths = expected.Length == 0 ? [] : expected.Split('|');

        Assert.Equal(paths, GoalScopeFilter.Mentions(text));
    }

    [Fact]
    public void A_mention_names_its_folder_and_everything_under_it()
    {
        Assert.True(GoalScopeFilter.Matches("src/Agents/X.cs", ["src/Agents"]));
        Assert.True(GoalScopeFilter.Matches("src/Agents", ["src/Agents"]));
        Assert.False(GoalScopeFilter.Matches("src/Cart.cs", ["src/Agents"]));
        Assert.True(GoalScopeFilter.Matches("src/Agents/X.cs", GoalScopeFilter.Mentions("tylko @src/Agents/")));
    }

    private const string Diff =
        "diff --git a/src/Agents/X.cs b/src/Agents/X.cs\n" +
        "--- a/src/Agents/X.cs\n" +
        "+++ b/src/Agents/X.cs\n" +
        "@@ -1 +1 @@\n" +
        "-old\n" +
        "+new\n" +
        "diff --git a/src/Cart.cs b/src/Cart.cs\n" +
        "--- a/src/Cart.cs\n" +
        "+++ b/src/Cart.cs\n" +
        "@@ -1 +1 @@\n" +
        "-total\n" +
        "+discounted\n";

    [Fact]
    public void The_diff_keeps_only_the_sections_inside_the_scope()
    {
        var kept = GoalScopeFilter.Diff(Diff, ["src/Agents"]);

        Assert.NotNull(kept);
        Assert.Contains("src/Agents/X.cs", kept);
        Assert.DoesNotContain("Cart.cs", kept);
    }

    [Fact]
    public void A_scope_that_names_nothing_that_changed_answers_null()
    {
        // Null, not empty: the caller turns it into the note saying the scope matched nothing, which
        // is what keeps the tool from reading the omission as a clean tree.
        Assert.Null(GoalScopeFilter.Diff(Diff, ["docs/"]));
    }

    [Fact]
    public void An_empty_scope_leaves_the_diff_alone()
    {
        Assert.Equal(Diff, GoalScopeFilter.Diff(Diff, []));
        Assert.Equal(Diff, GoalScopeFilter.Diff(Diff, GoalScopeFilter.Mentions("tylko zmiany agentow")));
    }

    [Fact]
    public void The_stat_keeps_its_file_lines_and_drops_the_totals_that_would_then_lie()
    {
        var stat = " src/Agents/X.cs | 12 ++++++++----\n" +
                   " src/Cart.cs      |  3 +++\n" +
                   " 2 files changed, 15 insertions(+), 4 deletions(-)";

        var kept = GoalScopeFilter.Stat(stat, ["src/Agents"]);

        Assert.NotNull(kept);
        Assert.Contains("src/Agents/X.cs", kept);
        Assert.DoesNotContain("Cart.cs", kept);
        Assert.DoesNotContain("files changed", kept);
    }

    [Fact]
    public void The_untracked_list_filters_one_path_per_line()
    {
        var names = "docs/notes.md\nsrc/Agents/notes.md\nREADME.md";

        var kept = GoalScopeFilter.Lines(names, ["src/Agents"]);

        Assert.Equal("src/Agents/notes.md", kept);
    }

    [Fact]
    public void A_diff_ending_exactly_at_a_heading_is_its_own_heading()
    {
        var kept = GoalScopeFilter.Diff("diff --git a/src/Agents/X.cs b/src/Agents/X.cs", ["src/Agents"]);

        Assert.NotNull(kept);
        Assert.Contains("src/Agents/X.cs", kept);
    }

    [Fact]
    public void A_rename_line_is_kept_by_its_new_path_whatever_form_the_stat_wrote_it_in()
    {
        // Both rename spellings: the whole-path arrow, and the brace form when the rename shares a
        // directory — where neither side of the arrow alone is the path the file is now at.
        var stat = " src/Old.cs => src/Agents/New.cs | 12 ++++++++----\n" +
                   " src/{Cart => Agents}/Helper.cs |  3 +++\n" +
                   " src/Cart.cs      |  1 -";

        var kept = GoalScopeFilter.Stat(stat, ["src/Agents"])!;

        Assert.Contains("src/Agents/New.cs", kept);
        Assert.Contains("Agents}/Helper.cs", kept);
        Assert.DoesNotContain("src/Cart.cs", kept);
    }

    [Fact]
    public void A_mention_that_opens_another_one_does_not_leave_its_tail_behind()
    {
        // Cut by position, never by spelling: taking "@src" out wherever it occurs also takes the head
        // of "@src/Cart.cs" and leaves "/Cart.cs" standing, which reads as words — so a box holding
        // nothing but pointers was adopted as a goal instead of narrowing one read from the changes.
        Assert.Equal("", GoalScopeFilter.WordsOnly("@src @src/Cart.cs"));
        Assert.Equal("", GoalScopeFilter.WordsOnly("@docs/GOAL.md @docs/GOAL.md.bak"));
    }

    [Fact]
    public void The_words_left_over_are_what_the_user_actually_asked_for()
    {
        // The pointer leaves a space where it stood, as it always did — the words either side of it
        // are what the caller reads, and squeezing the gap is not this method's question.
        Assert.Equal("popraw   koszyk", GoalScopeFilter.WordsOnly("popraw @src/Cart.cs koszyk"));
        Assert.Equal("", GoalScopeFilter.WordsOnly("  @HEAD~1  "));
    }

    [Fact]
    public void A_mention_span_leaves_the_sentence_its_punctuation()
    {
        const string text = "fix @src/Cart.cs, then the tests";

        var (start, length, path) = Assert.Single(GoalScopeFilter.MentionSpans(text));

        Assert.Equal("src/Cart.cs", path);
        Assert.Equal("@src/Cart.cs", text.Substring(start, length));
    }
}
