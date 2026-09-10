using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The composer's words as a scope: which <c>@</c> paths it names, and what a tree read keeps.
/// </summary>
public class GoalScopeFilterTests
{
    [Fact]
    public void A_completed_mention_is_read_back_as_its_path()
    {
        Assert.Equal(["src/Cart.cs"],
            GoalScopeFilter.Mentions("skup sie tylko na @src/Cart.cs"));
    }

    [Fact]
    public void A_quoted_mention_is_read_whatever_spaces_it_carries()
    {
        // The quoted spelling is what a completion writes for a path with whitespace in it, and the
        // spaces are the whole reason the quotes exist.
        Assert.Equal(["docs/my notes.md"],
            GoalScopeFilter.Mentions("review @\"docs/my notes.md\" first"));
    }

    [Fact]
    public void An_at_inside_a_word_is_prose_not_a_mention()
    {
        Assert.Empty(GoalScopeFilter.Mentions("write me at someone@example.com about this"));
    }

    [Fact]
    public void Several_mentions_are_all_read_and_deduplicated()
    {
        Assert.Equal(["src/A.cs", "src/B.cs"],
            GoalScopeFilter.Mentions("@src/A.cs and @src/B.cs, mainly @src/A.cs."));
    }

    [Fact]
    public void A_mention_names_its_folder_and_everything_under_it()
    {
        Assert.True(GoalScopeFilter.Matches("src/Agents/X.cs", ["src/Agents"]));
        Assert.True(GoalScopeFilter.Matches("src/Agents", ["src/Agents"]));
        Assert.False(GoalScopeFilter.Matches("src/Cart.cs", ["src/Agents"]));
    }

    [Fact]
    public void A_trailing_slash_is_how_a_folder_is_typed_not_part_of_its_name()
    {
        // "@src/Agents/" — the way a half-typed folder mention ends — must scope the folder, not a
        // directory whose name ends in a slash, which matches nothing at all.
        Assert.Equal(["src/Agents"], GoalScopeFilter.Mentions("tylko @src/Agents/"));
        Assert.True(GoalScopeFilter.Matches("src/Agents/X.cs", GoalScopeFilter.Mentions("tylko @src/Agents/")));
    }

    [Fact]
    public void A_token_with_no_slash_and_no_dot_is_still_a_mention()
    {
        // A folder is the commonest thing anybody points at and is spelled exactly like a word, so a
        // syntax rule here dropped "@frontend" before the filesystem was ever asked: the tree was read
        // unnarrowed, and in a repository with a branch of that name the token went on to be resolved
        // as a commit instead. What a token names is the filesystem's answer and then git's — this
        // class only says which words carry an "@".
        Assert.Equal(["frontend"], GoalScopeFilter.Mentions("popraw @frontend"));
        Assert.Equal(["HEAD~1"], GoalScopeFilter.Mentions("sprawdz @HEAD~1"));
        Assert.Equal(["admin"], GoalScopeFilter.Mentions("ping @admin about the failure"));
        Assert.Equal(["notes.md"], GoalScopeFilter.Mentions("see @notes.md"));
    }

    [Fact]
    public void A_trailing_range_separator_survives_the_sentence_trim()
    {
        // "@master.." is the one documented way to name a branch called after an ordinary word:
        // GoalScopeRef refuses the bare token, because "master", "admin" and "release" are words as
        // often as they are branches. Trimmed with the sentence punctuation it came back as exactly
        // that bare word, so the scope never formed and the tree was read from HEAD in silence —
        // while "@master..HEAD" escaped only because a letter happens to end it.
        Assert.Equal(["master.."], GoalScopeFilter.Mentions("popraw formularz @master.."));
        Assert.Equal(["master..HEAD"], GoalScopeFilter.Mentions("popraw formularz @master..HEAD"));

        // Exactly two go back, never the whole run. A third dot is the sentence's, and three of them
        // are a symmetric difference this tile does not answer.
        Assert.Equal(["master.."], GoalScopeFilter.Mentions("sprawdz @master..."));
        Assert.Equal(["master.."], GoalScopeFilter.Mentions("sprawdz @master.., potem reszte"));

        // And an ordinary sentence still loses its full stop: what follows the cut is one dot, not a
        // separator.
        Assert.Equal(["src/Cart.cs"], GoalScopeFilter.Mentions("zobacz @src/Cart.cs."));
        Assert.Equal(["v1.2"], GoalScopeFilter.Mentions("od @v1.2."));
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
}
