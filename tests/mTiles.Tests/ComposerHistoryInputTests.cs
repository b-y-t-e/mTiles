using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Up and Down through a composer's history, pressed as a keyboard presses them on a real <see cref="TextBox"/>
/// with the dispatcher running between presses.
/// </summary>
/// <remarks>The pure <c>ComposerHistory</c> cannot show what failed here: the box raises
/// <see cref="TextBox.TextChanged"/> through the dispatcher, after the step that set the text, and a walk that
/// ended on that event ended after every step — Up never got past the newest message and the draft was lost.</remarks>
public class ComposerHistoryInputTests
{
    private static readonly Avalonia.Themes.Fluent.FluentTheme Theme = new();

    [Fact]
    public void Up_walks_back_past_the_newest_message_and_Down_brings_the_draft_back() => InWindow((window, box) =>
    {
        box.Text = "draft";
        box.CaretIndex = 0;

        Press(window, PhysicalKey.ArrowUp);
        Assert.Equal("b", box.Text);
        Press(window, PhysicalKey.ArrowUp);
        Assert.Equal("a", box.Text);

        box.CaretIndex = box.Text!.Length;
        Press(window, PhysicalKey.ArrowDown);
        Assert.Equal("b", box.Text);
        Press(window, PhysicalKey.ArrowDown);
        Assert.Equal("draft", box.Text);
    });

    [Fact]
    public void An_edit_to_a_recalled_message_ends_the_walk() => InWindow((window, box) =>
    {
        box.CaretIndex = 0;
        Press(window, PhysicalKey.ArrowUp);
        Assert.Equal("b", box.Text);

        box.Text = "b edited";
        Dispatcher.UIThread.RunJobs();
        box.CaretIndex = 0;
        Press(window, PhysicalKey.ArrowUp);

        // A new walk from the newest message, with the edited text as its draft.
        Assert.Equal("b", box.Text);
        box.CaretIndex = box.Text!.Length;
        Press(window, PhysicalKey.ArrowDown);
        Assert.Equal("b edited", box.Text);
    });

    private static void Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void InWindow(Action<Window, TextBox> body) => OnUiThread(() =>
    {
        // Control templates on the application: without a theme a TextBox has no presenter and edits nothing.
        var app = Avalonia.Application.Current!;
        app.Styles.Add(Theme);
        var box = new TextBox { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var window = new Window { Content = box, Width = 500, Height = 300 };
        ComposerHistoryInput.Attach(box, () => ["a", "b"], () => false);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            box.Focus();
            body(window, box);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(Theme);
        }
    });

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ComposerHistoryInputTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }
}
