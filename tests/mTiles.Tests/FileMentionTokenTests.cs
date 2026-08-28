using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// When an <c>@</c> is a request for a file, and what taking one leaves in the box.
/// </summary>
/// <remarks>
/// Every rule here is one that fails silently when it is wrong: a popup that opens over an email
/// address, or a completion that eats a word the user can still see.
/// </remarks>
public class FileMentionTokenTests
{
    [Theory]
    [InlineData("@", 1, "")]
    [InlineData("@Goal", 5, "Goal")]
    [InlineData("look at @Goal", 13, "Goal")]
    [InlineData("look at @Goal", 10, "G")]
    [InlineData("@src/mTiles/Views", 17, "src/mTiles/Views")]
    public void An_at_that_opens_a_word_is_a_mention(string text, int caret, string query)
    {
        var token = FileMentionToken.At(text, caret);

        Assert.NotNull(token);
        Assert.Equal(query, token!.Value.Query);
    }

    [Theory]
    [InlineData("andrzej@example.com", 19)]  // an address, not a request
    [InlineData("@Goal and then", 14)]       // the mention was closed by a space
    [InlineData("plain text", 10)]
    [InlineData("", 0)]
    [InlineData("@Goal", 0)]                 // the caret is before the @
    public void Anything_else_is_not(string text, int caret) =>
        Assert.Null(FileMentionToken.At(text, caret));

    [Fact]
    public void A_caret_outside_the_text_asks_nothing()
    {
        Assert.Null(FileMentionToken.At("@Goal", 6));
        Assert.Null(FileMentionToken.At("@Goal", -1));
    }

    [Fact]
    public void Taking_a_file_replaces_what_was_typed_and_closes_the_mention()
    {
        var token = FileMentionToken.At("look at @Goal", 13)!.Value;

        var completed = token.Complete("look at @Goal", "src/mTiles/ViewModels/GoalTileViewModel.cs");

        Assert.Equal("look at @src/mTiles/ViewModels/GoalTileViewModel.cs ", completed.Text);
        Assert.Equal(completed.Text.Length, completed.CaretIndex);

        // The trailing space is what shuts the popup: there is no token under the new caret.
        Assert.Null(FileMentionToken.At(completed.Text, completed.CaretIndex));
    }

    /// <summary>
    /// A mention ends at the first space, so a path holding one has to be quoted or it names half a
    /// file and leaves the rest of the name loose in the sentence.
    /// </summary>
    [Fact]
    public void A_path_with_a_space_in_it_is_quoted()
    {
        var token = FileMentionToken.At("see @my", 7)!.Value;

        var completed = token.Complete("see @my", "docs/my notes.md");

        Assert.Equal("see @\"docs/my notes.md\" ", completed.Text);
        Assert.Equal(completed.Text.Length, completed.CaretIndex);
    }

    /// <summary>
    /// A quote inside the name is left as it is, not escaped.
    /// </summary>
    /// <remarks>
    /// It cannot happen on Windows, where the character is illegal in a file name, and the backslash
    /// this used to write was worse than nothing: the parser on the other side reads
    /// <c>@"([^"]+)"</c>, so an escaped quote ends the mention exactly where a bare one does — and the
    /// backslash is then delivered as part of the name. Tools of this kind do not escape it either.
    /// </remarks>
    [Fact]
    public void A_quote_in_the_name_is_left_alone() =>
        Assert.Equal("@\"a \"b\".cs\"", FileMentionToken.Mention("a \"b\".cs"));

    [Fact]
    public void A_path_without_whitespace_is_left_bare() =>
        Assert.Equal("@src/Goal.cs", FileMentionToken.Mention("src/Goal.cs"));

    /// <summary>
    /// The query stops at the caret, but the replacement takes the whole word.
    /// </summary>
    /// <remarks>
    /// The usual rule. Fixing a typo in the middle of a path is then a matter of picking the right
    /// row; the opposite rule left the tail of the old path stranded after the new one.
    /// </remarks>
    [Fact]
    public void The_whole_word_is_replaced_though_only_what_precedes_the_caret_is_searched()
    {
        var token = FileMentionToken.At("@fi.cs", 3)!.Value;

        Assert.Equal("fi", token.Query);
        Assert.Equal("@Goal.cs ", token.Complete("@fi.cs", "Goal.cs").Text);
    }

    [Fact]
    public void A_word_the_caret_sits_at_the_end_of_is_replaced_whole()
    {
        var token = FileMentionToken.At("see @fi.cs now", 7)!.Value;

        Assert.Equal("see @Goal.cs  now", token.Complete("see @fi.cs now", "Goal.cs").Text);
    }

    // ── Tab: the part every row agrees on ───────────────

    [Fact]
    public void The_common_prefix_of_one_path_is_that_path() =>
        Assert.Equal("src/Goal.cs", FileMentionToken.CommonPrefix(["src/Goal.cs"]));

    [Fact]
    public void The_common_prefix_stops_where_the_paths_disagree() =>
        Assert.Equal(
            "src/tools/Bash",
            FileMentionToken.CommonPrefix(["src/tools/BashTool.cs", "src/tools/Bashful.cs"]));

    [Fact]
    public void Paths_that_share_nothing_have_no_common_prefix() =>
        Assert.Equal("", FileMentionToken.CommonPrefix(["src/a.cs", "docs/b.md"]));

    /// <summary>Compared without case, but spelled the way the first path is.</summary>
    [Fact]
    public void The_prefix_is_spelled_the_way_the_file_is() =>
        Assert.Equal("Goal", FileMentionToken.CommonPrefix(["GoalTile.cs", "goalPolicy.cs"]));

    [Fact]
    public void Nothing_has_no_common_prefix() =>
        Assert.Equal("", FileMentionToken.CommonPrefix([]));

    // ── The caret inside a quoted mention ───────────────

    /// <summary>
    /// A caret between the <c>@</c> and its quote asks a question, it does not throw.
    /// </summary>
    /// <remarks>
    /// <c>LastIndexOf</c> searches backward from the caret and finds a match that begins one character
    /// before it, so the opener straddles the caret and the slice that follows ends before it starts.
    /// Reached by typing <c>@"</c> and pressing Left, or by clicking between the two — and from there it
    /// is an exception out of a keystroke, which the popup answers by doing nothing at all.
    /// </remarks>
    [Theory]
    [InlineData("@\"", 1)]
    [InlineData("see @\"", 5)]
    [InlineData("@\"my folder/\"", 1)]
    public void A_caret_between_the_at_and_its_quote_is_read_as_a_bare_mention(string text, int caret)
    {
        var token = FileMentionToken.At(text, caret);

        Assert.NotNull(token);
        Assert.Equal("", token!.Value.Query);
    }

    [Fact]
    public void A_caret_inside_a_quoted_mention_reads_what_precedes_it()
    {
        var token = FileMentionToken.At("@\"my folder/\"", 12);

        Assert.NotNull(token);
        Assert.Equal("my folder/", token!.Value.Query);
    }

    /// <summary>A quoted mention the caret has left behind is finished, not being written.</summary>
    [Fact]
    public void A_caret_after_a_closed_quote_is_in_no_mention() =>
        Assert.Null(FileMentionToken.At("@\"my folder/\" ", 14));

    // ── A quote somebody thought better of ──────────────

    /// <summary>
    /// A mention begun after an abandoned <c>@"</c> is the one the caret is in.
    /// </summary>
    /// <remarks>
    /// A quoted mention may hold whitespace, so the search for one runs back to the last <c>@"</c>
    /// however far away it is. A quote typed and then written past therefore swallowed the rest of the
    /// message: the matcher was asked for everything after it, found nothing, and no mention typed
    /// later could open the list again — with nothing on screen to say that deleting one character
    /// somewhere behind was the way out.
    /// </remarks>
    [Fact]
    public void An_abandoned_quote_does_not_swallow_a_later_mention()
    {
        const string text = "Fix @\" the thing and @tests/Foo";

        var token = FileMentionToken.At(text, text.Length);

        Assert.NotNull(token);
        Assert.Equal("tests/Foo", token!.Value.Query);
        Assert.Equal(text.IndexOf("@tests", StringComparison.Ordinal), token.Value.Start);
    }

    /// <summary>Until one is begun, the abandoned quote is still what the caret is in.</summary>
    [Fact]
    public void Before_a_later_mention_the_abandoned_quote_is_what_there_is()
    {
        const string text = "Fix @\" the thing and ";

        var token = FileMentionToken.At(text, text.Length);

        Assert.NotNull(token);
        Assert.Equal(4, token!.Value.Start);
    }

    /// <summary>And a real quoted path is not lost to the rule that fixes it.</summary>
    [Theory]
    [InlineData("see @\"docs/my notes.md", "docs/my notes.md")]
    [InlineData("see @\"docs/my not", "docs/my not")]
    [InlineData("see @\"docsnospace", "docsnospace")]
    public void A_quoted_path_still_wins_where_it_is_the_nearer_one(string text, string query)
    {
        var token = FileMentionToken.At(text, text.Length);

        Assert.NotNull(token);
        Assert.Equal(query, token!.Value.Query);
    }

    /// <summary>
    /// A later quote elsewhere in the message does not become this mention's end.
    /// </summary>
    /// <remarks>
    /// <para>The closing quote was taken as the next one anywhere after the caret, so a mention still
    /// being typed swallowed everything up to some unrelated quotation later in the sentence. Completing
    /// <c>@"my fold</c> in <c>Fix @"my fold and then "quoted" here</c> spliced <c>and then "</c> out of
    /// the message — silently, and past Ctrl+Z, because the box's text is set programmatically.</para>
    /// <para>A quote closes a mention only when a token boundary follows it. Here it is followed by
    /// <c>q</c>, so it closes nothing and the replacement stops at the caret.</para>
    /// </remarks>
    [Fact]
    public void A_quotation_later_in_the_message_is_not_this_mentions_closing_quote()
    {
        const string text = "Fix @\"my fold and then \"quoted\" here";
        var caret = text.IndexOf("fold", StringComparison.Ordinal) + "fold".Length;

        var token = FileMentionToken.At(text, caret)!.Value;

        Assert.Equal("my fold", token.Query);

        var completed = token.Complete(text, "docs/my notes.md");

        // Nothing after the caret was consumed. The mention brings its own trailing space and the
        // text kept the one it already had, which is the same double space every completion made in
        // the middle of a sentence leaves.
        Assert.Equal("Fix @\"docs/my notes.md\"  and then \"quoted\" here", completed.Text);
    }

    /// <summary>A quote that does end the token still ends the mention.</summary>
    /// <remarks>
    /// The other side of the same rule, and what keeps a finished mention editable: the closing quote
    /// of <c>@"docs/my notes.md"</c> is followed by a space, so completing from inside replaces the
    /// whole path rather than leaving its tail behind.
    /// </remarks>
    [Theory]
    [InlineData("see @\"docs/my notes.md\" and more", "see @\"docs/other file.md\"  and more")]
    [InlineData("see @\"docs/my notes.md\"", "see @\"docs/other file.md\" ")]
    public void A_quote_that_ends_the_token_does_close_the_mention(string text, string expected)
    {
        var caret = text.IndexOf("my", StringComparison.Ordinal) + 2;

        var token = FileMentionToken.At(text, caret)!.Value;

        Assert.Equal(expected, token.Complete(text, "docs/other file.md").Text);
    }
}