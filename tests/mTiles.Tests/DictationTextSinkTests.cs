using Avalonia.Controls;
using Avalonia.Headless;
using AvaloniaEdit;
using mTiles.Models;
using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Where a transcript lands when a text control has the keyboard — the half of the sink that needs a
/// visual tree, and so the half that had no tests while both of its rules were got wrong.
/// </summary>
/// <remarks>
/// Both failures are silent. Inserting beside a selection instead of replacing it leaves the user with
/// the sentence they meant to redo <em>and</em> its replacement; writing into a control that has left
/// the tree loses the text while reporting success, which is exactly the answer the service must not be
/// given — it has an error path for undeliverable text and this bypassed it.
/// </remarks>
public class DictationTextSinkTests
{
    private static readonly SpeechSettings Plain = new() { AppendTrailingSpace = false };

    /// <summary>The shipped default. A transcript is trimmed at both ends before it goes anywhere, so
    /// the space between what was dictated and what follows comes from this setting, not from the text.
    /// </summary>
    private static readonly SpeechSettings Spaced = new() { AppendTrailingSpace = true };

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DictationTextSinkTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>A window is what makes a control "on screen" as far as the sink is concerned.</summary>
    private static Window ShowingWindow(Control content)
    {
        var window = new Window { Content = content };
        window.Show();
        return window;
    }

    [Fact]
    public void Text_goes_in_at_the_caret()
        => OnUiThread(() =>
        {
            var box = new TextBox { Text = "before after" };
            var window = ShowingWindow(box);
            box.CaretIndex = 7;

            Assert.True(DictationTextSink.Insert(null, "middle", Spaced, box));

            Assert.Equal("before middle after", box.Text);
            Assert.Equal(14, box.CaretIndex);
        });

    [Fact]
    public void A_selection_is_replaced_rather_than_written_around()
        => OnUiThread(() =>
        {
            var box = new TextBox { Text = "keep this drop that keep this too" };
            var window = ShowingWindow(box);
            box.SelectionStart = 10;
            box.SelectionEnd = 19;                       // "drop that"

            Assert.True(DictationTextSink.Insert(null, "said instead", Plain, box));

            Assert.Equal("keep this said instead keep this too", box.Text);
            Assert.Equal(22, box.CaretIndex);
            Assert.Equal(box.SelectionStart, box.SelectionEnd);   // and nothing left selected
        });

    /// <summary>
    /// The insertion is undoable — a dictated sentence is exactly what somebody presses Ctrl+Z on.
    /// </summary>
    /// <remarks>
    /// This does <em>not</em> discriminate between writing through the selection and assigning Text:
    /// measured, Avalonia restores the previous text either way. It is here for the property that
    /// matters to the user, not as a guard on the implementation.
    /// </remarks>
    [Fact]
    public void What_was_dictated_can_be_undone()
        => OnUiThread(() =>
        {
            var box = new TextBox { Text = "typed by hand" };
            var window = ShowingWindow(box);
            box.CaretIndex = 0;

            // The transcript is trimmed at both ends, so the space comes from AppendTrailingSpace.
            Assert.True(DictationTextSink.Insert(null, "spoken", Spaced, box));
            Assert.Equal("spoken typed by hand", box.Text);

            box.Undo();

            Assert.Equal("typed by hand", box.Text);
        });

    /// <summary>Backwards is the same selection: dragging right to left is how half of it is made.</summary>
    [Fact]
    public void A_selection_made_backwards_is_replaced_too()
        => OnUiThread(() =>
        {
            var box = new TextBox { Text = "alpha beta gamma" };
            var window = ShowingWindow(box);
            box.SelectionStart = 10;
            box.SelectionEnd = 6;                        // "beta" selected right to left

            Assert.True(DictationTextSink.Insert(null, "BETA", Plain, box));

            Assert.Equal("alpha BETA gamma", box.Text);
        });

    [Fact]
    public void An_editor_replaces_its_selection_as_well()
        => OnUiThread(() =>
        {
            var editor = new TextEditor { Text = "one two three" };
            var window = ShowingWindow(editor);
            editor.SelectionStart = 4;
            editor.SelectionLength = 3;                  // "two"

            Assert.True(DictationTextSink.Insert(null, "2", Plain, editor));

            Assert.Equal("one 2 three", editor.Text);
        });

    /// <summary>
    /// The focused element is captured when the key goes down and used seconds later, by which time its
    /// dialog may have closed. Writing there and reporting success loses a spoken paragraph in a control
    /// nobody can see.
    /// </summary>
    [Fact]
    public void A_control_that_has_left_the_tree_is_refused_rather_than_written_to()
        => OnUiThread(() =>
        {
            var box = new TextBox { Text = "" };
            var window = ShowingWindow(box);
            window.Content = null;                       // the dialog closed while the user was speaking
            window.Close();

            Assert.False(DictationTextSink.Insert(null, "spoken", Plain, box));
            Assert.Equal("", box.Text);
        });

    // ---- the terminal, which is the branch that can run something ----

    /// <summary>
    /// <c>AutoSubmitEnter</c> adds exactly one carriage return, at the end, and only when it is on.
    /// </summary>
    /// <remarks>
    /// The whole of the feature's risk is here: everywhere else a transcript is text landing in a box,
    /// and here it is bytes going to a shell, where a <c>\r</c> is not a character but a command being
    /// run. Composing was tested and this was not — and composing never touches <c>\r</c>, so the tests
    /// that looked like they covered it could not have.
    /// </remarks>
    [Theory]
    [InlineData(false, "git status")]
    [InlineData(true, "git status\r")]
    public void The_carriage_return_is_added_only_when_it_was_asked_for(bool submit, string expected)
    {
        string? sent = null;
        Assert.True(DictationTextSink.Type(text => { sent = text; return true; }, "git status", submit));

        Assert.Equal(expected, sent);
        Assert.Equal(submit ? 1 : 0, sent!.Count(c => c == '\r'));
    }

    /// <summary>
    /// A transcript full of newlines still runs at most the one command the user asked to submit.
    /// </summary>
    /// <remarks>
    /// The sanitiser is what makes that true — it turns every control character into a space — so this
    /// pins the two halves together rather than either alone: what a model heard cannot become a second
    /// command line, whatever it heard.
    /// </remarks>
    [Fact]
    public void A_transcript_cannot_smuggle_a_second_command_past_the_submit()
    {
        string? sent = null;
        var payload = DictationTextSink.Compose("rm -rf x\r\nsudo shutdown", Plain);
        DictationTextSink.Type(text => { sent = text; return true; }, payload, submit: true);

        Assert.Equal("rm -rf x sudo shutdown\r", sent);
        Assert.Equal(1, sent!.Count(c => c == '\r'));
        Assert.DoesNotContain('\n', sent!);
    }

    /// <summary>A terminal that refuses the text has not delivered it, and the caller is told so — the
    /// service quotes an undeliverable transcript back rather than losing it.</summary>
    [Fact]
    public void A_refused_send_is_reported_as_undelivered()
        => Assert.False(DictationTextSink.Type(_ => false, "spoken", submit: true));

    /// <summary>No tile and no focused control: nowhere to put it, and that is not a silent success.</summary>
    [Fact]
    public void With_nowhere_to_put_it_the_transcript_is_refused()
        => OnUiThread(() => Assert.False(DictationTextSink.Insert(null, "spoken", Plain, null)));

    /// <summary>
    /// A transcript that sanitises down to nothing is not "nowhere to put it".
    /// </summary>
    /// <remarks>
    /// <para>False out of <c>Insert</c> means one thing to the user: "There was nowhere to put the text",
    /// which sends them looking at a tile that closed or a shell that exited. Nothing surviving
    /// sanitisation is the opposite problem — the destination was fine and there was nothing to deliver —
    /// and the service already treats an empty transcript as a silence worth no more than a trace line.
    /// The sanitiser can produce that emptiness one step later than the service's own check: a result of
    /// nothing but control characters is not whitespace, so it passes <c>IsNullOrWhiteSpace</c> and then
    /// comes out of <c>Compose</c> empty.</para>
    /// <para>Both destinations are asserted, and the terminal one is the point: it must not be told to
    /// type an empty string, let alone the bare carriage return that <c>AutoSubmitEnter</c> would add to
    /// it — a transcript of one cough would run whatever was already on the command line.</para>
    /// </remarks>
    [Fact]
    public void A_transcript_that_sanitises_to_nothing_is_not_reported_as_undeliverable()
        => OnUiThread(() =>
        {
            var box = new TextBox { Text = "untouched" };
            var window = ShowingWindow(box);
            box.CaretIndex = 0;

            // Control characters, not the empty string: this is the shape that gets past the
            // service's own IsNullOrWhiteSpace check and is emptied one step later, here.
            const string nothingUsable = "";
            Assert.False(string.IsNullOrWhiteSpace(nothingUsable));

            Assert.True(DictationTextSink.Insert(null, nothingUsable, Spaced, box));
            Assert.Equal("untouched", box.Text);

            // And with no control focused at all, where the terminal would otherwise be reached.
            Assert.True(DictationTextSink.Insert(null, nothingUsable, Spaced, null));
        });

    [Fact]
    public void A_read_only_control_is_not_written_to()
        => OnUiThread(() =>
        {
            var box = new TextBox { Text = "fixed", IsReadOnly = true };
            var window = ShowingWindow(box);

            Assert.False(DictationTextSink.Insert(null, "spoken", Plain, box));
            Assert.Equal("fixed", box.Text);
        });
}
