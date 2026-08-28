using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The suggestion popup's own state: when it is up, what is picked, and what taking one writes back.
/// </summary>
public class FileMentionsViewModelTests
{
    /// <summary>
    /// A tree that never touches a disk, and can be made to answer slowly on purpose.
    /// </summary>
    private sealed class FakeSource(params string[] paths) : IFileMentionSource
    {
        /// <summary>Held shut until a test says otherwise, so two updates can be in flight at once.</summary>
        internal TaskCompletionSource? Gate { get; set; }

        public async Task<IReadOnlyList<string>> GetPathsAsync(CancellationToken ct = default)
        {
            if (Gate != null) await Gate.Task;
            return paths;
        }
    }

    private static FileMentionsViewModel Mentions(params string[] paths) => new(new FakeSource(paths));

    [Fact]
    public async Task An_at_opens_the_list_with_the_first_row_picked()
    {
        var mentions = Mentions("Goal.cs", "Other.cs");

        await mentions.UpdateAsync("@go", 3);

        Assert.True(mentions.IsOpen);
        Assert.Equal("Goal.cs", mentions.SelectedPath);
        Assert.Single(mentions.Suggestions);
    }

    [Fact]
    public async Task Text_with_no_mention_in_it_puts_the_list_away()
    {
        var mentions = Mentions("Goal.cs");
        await mentions.UpdateAsync("@go", 3);

        await mentions.UpdateAsync("@go and more", 12);

        Assert.False(mentions.IsOpen);
        Assert.Null(mentions.SelectedPath);
    }

    [Fact]
    public async Task A_mention_that_matches_nothing_shows_nothing()
    {
        var mentions = Mentions("Goal.cs");

        await mentions.UpdateAsync("@kubernetes", 11);

        Assert.False(mentions.IsOpen);
    }

    [Fact]
    public async Task The_pick_moves_and_wraps_round_both_ends()
    {
        var mentions = Mentions("Goal.cs", "GoalTwo.cs");
        await mentions.UpdateAsync("@goal", 5);

        Assert.True(mentions.MoveSelection(1));
        Assert.Equal("GoalTwo.cs", mentions.SelectedPath);

        Assert.True(mentions.MoveSelection(1));
        Assert.Equal("Goal.cs", mentions.SelectedPath);

        Assert.True(mentions.MoveSelection(-1));
        Assert.Equal("GoalTwo.cs", mentions.SelectedPath);
    }

    [Fact]
    public void Nothing_to_move_while_the_list_is_down()
    {
        Assert.False(Mentions("Goal.cs").MoveSelection(1));
    }

    [Fact]
    public async Task Taking_the_picked_row_writes_the_path_back_and_closes()
    {
        var mentions = Mentions("src/Goal.cs");
        await mentions.UpdateAsync("look at @go", 11);

        var completed = mentions.Complete("look at @go", 11);

        Assert.NotNull(completed);
        Assert.Equal("look at @src/Goal.cs ", completed!.Value.Text);
        Assert.False(mentions.IsOpen);
    }

    [Fact]
    public async Task A_clicked_row_is_taken_rather_than_the_picked_one()
    {
        var mentions = Mentions("Goal.cs", "GoalTwo.cs");
        await mentions.UpdateAsync("@goal", 5);

        var completed = mentions.Complete("@goal", 5, "GoalTwo.cs");

        Assert.Equal("@GoalTwo.cs ", completed!.Value.Text);
    }

    [Fact]
    public void Taking_something_while_the_list_is_down_writes_nothing()
    {
        Assert.Null(Mentions("Goal.cs").Complete("@goal", 5));
    }

    /// <summary>
    /// The answer to the last keystroke wins, whatever order the reads come back in.
    /// </summary>
    /// <remarks>
    /// The tree is read once and cached, but the first read of a large repository takes long enough for
    /// three more letters to arrive — and a popup answering <c>@g</c> on top of the answer to
    /// <c>@goal</c> is a list of the wrong files with the right one already typed.
    /// </remarks>
    [Fact]
    public async Task A_slow_read_never_lands_on_top_of_a_later_one()
    {
        var source = new FakeSource("Goal.cs", "Zebra.cs");
        var mentions = new FileMentionsViewModel(source);

        // The read for "@z" is held open while the user types on.
        var held = new TaskCompletionSource();
        source.Gate = held;
        var stale = mentions.UpdateAsync("@z", 2);

        source.Gate = null;
        await mentions.UpdateAsync("@goal", 5);
        Assert.Equal("Goal.cs", mentions.SelectedPath);

        held.SetResult();
        await stale;

        Assert.Equal("Goal.cs", mentions.SelectedPath);
    }

    [Fact]
    public async Task Closing_the_list_disowns_a_read_still_in_flight()
    {
        var source = new FakeSource("Goal.cs");
        var mentions = new FileMentionsViewModel(source);
        var gate = new TaskCompletionSource();
        source.Gate = gate;

        var update = mentions.UpdateAsync("@go", 3);
        mentions.Close();

        gate.SetResult();
        await update;

        Assert.False(mentions.IsOpen);
    }

    // ── Folders are a step, not an answer ───────────────

    /// <summary>
    /// Taking a folder types it and leaves the list up, so Enter walks down a tree.
    /// </summary>
    /// <remarks>
    /// No trailing space, which is what keeps the mention open: a space would put the caret somewhere
    /// <c>FileMentionToken.At</c> finds no token, and <c>@src/</c> names a place the user has not
    /// finished naming.
    /// </remarks>
    [Fact]
    public async Task Taking_a_folder_steps_into_it_rather_than_finishing_the_mention()
    {
        var mentions = Mentions("src/", "src/Goal.cs");
        await mentions.UpdateAsync("@src", 4);

        var completed = mentions.Complete("@src", 4, "src/");

        Assert.Equal("@src/", completed!.Value.Text);
        Assert.Equal(5, completed.Value.CaretIndex);
        Assert.True(mentions.IsOpen);
    }

    [Fact]
    public async Task Taking_a_file_finishes_the_mention_and_closes()
    {
        var mentions = Mentions("src/", "src/Goal.cs");
        await mentions.UpdateAsync("@src", 4);

        var completed = mentions.Complete("@src", 4, "src/Goal.cs");

        Assert.Equal("@src/Goal.cs ", completed!.Value.Text);
        Assert.False(mentions.IsOpen);
    }

    // ── Tab narrows before it picks ─────────────────────

    /// <summary>
    /// Tab types the part every row agrees on and leaves the list up — the shell's completion.
    /// </summary>
    [Fact]
    public async Task Tab_types_what_every_row_agrees_on()
    {
        var mentions = Mentions("src/tools/BashTool.cs", "src/tools/Bashful.cs");
        await mentions.UpdateAsync("@bash", 5);

        var completed = mentions.CompleteCommonPrefix("@bash", 5);

        Assert.Equal("@src/tools/Bash", completed!.Value.Text);
        Assert.True(mentions.IsOpen);
    }

    /// <summary>
    /// With nothing left to narrow, Tab picks — otherwise it would be the one key in the popup that did
    /// nothing.
    /// </summary>
    [Fact]
    public async Task Tab_picks_when_there_is_nothing_left_to_narrow()
    {
        var mentions = Mentions("src/Goal.cs");
        await mentions.UpdateAsync("@src/Goal.cs", 12);

        var completed = mentions.CompleteCommonPrefix("@src/Goal.cs", 12);

        Assert.Equal("@src/Goal.cs ", completed!.Value.Text);
        Assert.False(mentions.IsOpen);
    }

    [Fact]
    public void Tab_writes_nothing_while_the_list_is_down() =>
        Assert.Null(Mentions("src/Goal.cs").CompleteCommonPrefix("@src", 4));

    // ── The bare @, and names with spaces in them ───────

    /// <summary>
    /// The first row a bare <c>@</c> offers is a way in, and Enter goes in rather than finishing.
    /// </summary>
    /// <remarks>
    /// The top level is derived from the corpus, and it used to be derived without the separator — so
    /// <c>src</c> was not a folder to anything that reads one, and the very first Enter after typing
    /// <c>@</c> wrote <c>@src </c> and closed the popup. That is the opposite of the rule the tile
    /// states, and it happened on the one keystroke every use of this feature starts with.
    /// </remarks>
    [Fact]
    public async Task The_first_row_after_a_bare_at_is_a_folder_to_step_into()
    {
        var mentions = Mentions("src/Goal.cs", "src/deep/Other.cs", "README.md");
        await mentions.UpdateAsync("@", 1);

        Assert.Equal("src/", mentions.Suggestions[0]);

        var completed = mentions.Complete("@", 1);

        Assert.Equal("@src/", completed!.Value.Text);
        Assert.True(mentions.IsOpen, "the list closed on a folder, so the tree cannot be walked");
    }

    /// <summary>
    /// A folder with a space in it can still be stepped into.
    /// </summary>
    /// <remarks>
    /// Such a name has to be quoted, or the mention ends at the space for whatever reads the prompt.
    /// The quoting then hid the mention from the popup's own reader, which stopped at the first
    /// whitespace as well — so stepping into <c>my folder/</c> closed the list and nothing could reopen
    /// it. The names that most need the quoting were the ones it broke.
    /// </remarks>
    [Fact]
    public async Task A_folder_with_a_space_can_be_stepped_into()
    {
        var mentions = Mentions("my folder/", "my folder/Goal.cs");
        await mentions.UpdateAsync("@my", 3);

        var stepped = mentions.Complete("@my", 3, "my folder/");

        // The quote is opened and not closed: a mention still being written has no closing quote, and
        // writing one put the caret behind it where nothing could find a token at all.
        Assert.Equal("@\"my folder/", stepped!.Value.Text);

        // Asked at the caret the completion actually returned, not at a position worked out here. The
        // earlier version of this test computed its own index just inside the quote, so it went on
        // passing while the caret the user's box receives landed somewhere no token existed.
        var token = FileMentionToken.At(stepped.Value.Text, stepped.Value.CaretIndex);

        Assert.NotNull(token);
        Assert.Equal("my folder/", token!.Value.Query);
    }

    /// <summary>And what is offered inside it is what is inside it.</summary>
    [Fact]
    public async Task What_is_offered_inside_a_quoted_folder_is_what_is_in_it()
    {
        var mentions = Mentions("my folder/Goal.cs", "elsewhere/Other.cs");

        const string text = "@\"my folder/";
        await mentions.UpdateAsync(text, text.Length);

        Assert.Equal(["my folder/Goal.cs"], mentions.Suggestions);
    }

    /// <summary>
    /// Stepping into a folder with a space, then taking a file inside it, end to end.
    /// </summary>
    /// <remarks>
    /// Driven through the caret each step returns rather than through positions worked out here, which
    /// is the difference between testing the rule and testing the flow. The two are not the same: the
    /// caret is what the box is given, and a step that closed its quote left it somewhere no token
    /// existed while every assertion about <c>At</c> went on passing.
    /// </remarks>
    [Fact]
    public async Task A_folder_with_a_space_can_be_walked_all_the_way_to_a_file()
    {
        var mentions = Mentions("my folder/", "my folder/Goal.cs", "elsewhere/Other.cs");

        await mentions.UpdateAsync("@my", 3);
        var stepped = mentions.Complete("@my", 3, "my folder/")!.Value;

        // The list is still up, and it is now about what is in that folder — the folder row itself
        // included, since it matches its own name, exactly as an unquoted `@src/` keeps offering `src/`.
        await mentions.UpdateAsync(stepped.Text, stepped.CaretIndex);

        Assert.True(mentions.IsOpen, "stepping into a folder with a space closed the list");
        Assert.Contains("my folder/Goal.cs", mentions.Suggestions);
        Assert.DoesNotContain("elsewhere/Other.cs", mentions.Suggestions);

        var finished = mentions.Complete(stepped.Text, stepped.CaretIndex, "my folder/Goal.cs")!.Value;

        Assert.Equal("@\"my folder/Goal.cs\" ", finished.Text);
        Assert.False(mentions.IsOpen);
    }

    /// <summary>
    /// An unfinished quoted mention takes nothing past the caret when it is completed.
    /// </summary>
    /// <remarks>
    /// A closing quote is what says where a mention ends. Without one, the end is the caret and no
    /// further — otherwise a completion made in the middle of a sentence would swallow the rest of it,
    /// which is the same hazard an abandoned quote already carries at the other end.
    /// </remarks>
    [Fact]
    public async Task An_unfinished_quoted_mention_leaves_the_rest_of_the_sentence_alone()
    {
        var mentions = Mentions("my folder/Goal.cs");

        const string text = "@\"my folder/ and then some words";
        const int caret = 12;   // just after the `/`

        await mentions.UpdateAsync(text, caret);
        var finished = mentions.Complete(text, caret)!.Value;

        Assert.Equal("@\"my folder/Goal.cs\"  and then some words", finished.Text);
    }

    /// <summary>
    /// A disposed tile stops asking the working tree.
    /// </summary>
    /// <remarks>
    /// The source folds the caller's token into its own budget rather than replacing it, and that was
    /// written before anything passed one: every call was <c>GetPathsAsync()</c>, so a closed tile left
    /// a git process running out a ten-second clock over a workspace nobody was looking at. Small, and
    /// the point is that the comment on the other side is now true.
    /// </remarks>
    [Fact]
    public async Task A_disposed_tile_cancels_the_reading_it_asked_for()
    {
        var source = new WatchfulSource("src/Goal.cs");
        var mentions = new FileMentionsViewModel(source);

        mentions.Dispose();
        await mentions.UpdateAsync("@goal", 5);

        Assert.True(source.Asked, "the source was never asked, so this proves nothing");
        Assert.True(source.TokenWasCancelled, "the reading was started with a token nobody can cancel");
    }

    /// <summary>Records the token it was handed, so a test can say whether one arrived at all.</summary>
    private sealed class WatchfulSource(params string[] paths) : IFileMentionSource
    {
        internal bool Asked { get; private set; }
        internal bool TokenWasCancelled { get; private set; }

        public Task<IReadOnlyList<string>> GetPathsAsync(CancellationToken ct = default)
        {
            Asked = true;
            TokenWasCancelled = ct.IsCancellationRequested;

            return Task.FromResult<IReadOnlyList<string>>(paths);
        }
    }
}