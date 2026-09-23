using Avalonia.Controls;
using Avalonia.Headless;
using mTiles.Services;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What the popup shows of the pick the arrows move.
/// </summary>
/// <remarks>
/// The view model's own tests say which path Enter would take; nothing there can say whether the user
/// can see it. The list is not focusable and its rows are marked handled before they select themselves,
/// so it never picks anything on its own — a pick nobody tells it about is a popup where Enter takes a
/// row the user was never shown.
/// </remarks>
public class FileMentionBehaviorTests
{
    /// <summary>Answers at once, so a test can look at the list on the line after the update.</summary>
    private sealed class ReadySource(params string[] paths) : IFileMentionSource
    {
        public Task<IReadOnlyList<string>> GetPathsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(paths);
    }

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FileMentionBehaviorTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>A box wired to suggestions, on screen, with its list to hand.</summary>
    private static (FileMentionsViewModel Mentions, ListBox List) Wired(params string[] paths)
    {
        var mentions = new FileMentionsViewModel(new ReadySource(paths));
        var box = new TextBox();

        new Window { Content = box, Width = 400, Height = 200 }.Show();
        FileMentionBehavior.SetMentions(box, mentions);

        return (mentions, FileMentionBehavior.GetSuggestionList(box)!);
    }

    /// <summary>The update completes on the line it is started, because the source already has an answer.</summary>
    private static void Type(FileMentionsViewModel mentions, string text) =>
        mentions.UpdateAsync(text, text.Length).GetAwaiter().GetResult();

    [Fact]
    public void The_row_Enter_would_take_is_the_lit_one()
    {
        OnUiThread(() =>
        {
            var (mentions, list) = Wired("Goal.cs", "GoalTileView.axaml");

            Type(mentions, "@goal");

            Assert.Equal(mentions.SelectedIndex, list.SelectedIndex);
            Assert.Equal(mentions.SelectedPath, list.SelectedItem);
        });
    }

    [Fact]
    public void Arrows_move_the_lit_row()
    {
        OnUiThread(() =>
        {
            var (mentions, list) = Wired("Goal.cs", "GoalTileView.axaml");
            Type(mentions, "@goal");

            mentions.MoveSelection(1);

            Assert.Equal(1, list.SelectedIndex);
            Assert.Equal(mentions.SelectedPath, list.SelectedItem);
        });
    }

    /// <summary>
    /// The pick that did not change is still shown after the rows underneath it did.
    /// </summary>
    /// <remarks>
    /// The refill leaves the pick on the top row, so the view model announces nothing while the list
    /// drops its own selection with its items — which left the second letter of a mention showing a list
    /// with no row lit at all.
    /// </remarks>
    [Fact]
    public void Another_letter_leaves_the_top_row_lit()
    {
        OnUiThread(() =>
        {
            var (mentions, list) = Wired("Goal.cs", "GoalTileView.axaml");
            Type(mentions, "@go");

            Type(mentions, "@goa");

            Assert.Equal(0, mentions.SelectedIndex);
            Assert.Equal(0, list.SelectedIndex);
        });
    }

    /// <summary>
    /// A box taken off the screen stops answering the view model.
    /// </summary>
    /// <remarks>
    /// Every round of questions builds new answer boxes from the question list's template and drops the
    /// old ones, while the view model belongs to the tile and outlives all of them. A popup still
    /// subscribed to it keeps its dead box and list alive and lights a row in them on every refresh,
    /// once more for every row of questions the tile has ever shown.
    /// </remarks>
    [Fact]
    public void A_box_off_the_screen_stops_listening()
    {
        OnUiThread(() =>
        {
            var mentions = new FileMentionsViewModel(new ReadySource("Goal.cs"));
            var box = new TextBox();
            var window = new Window { Content = box, Width = 400, Height = 200 };
            window.Show();
            FileMentionBehavior.SetMentions(box, mentions);
            var list = FileMentionBehavior.GetSuggestionList(box)!;

            window.Content = null;
            Type(mentions, "@goal");

            Assert.Equal(-1, list.SelectedIndex);
        });
    }

    [Fact]
    public void Putting_the_list_away_lights_nothing()
    {
        OnUiThread(() =>
        {
            var (mentions, list) = Wired("Goal.cs");
            Type(mentions, "@goal");

            mentions.Close();

            Assert.Equal(-1, list.SelectedIndex);
        });
    }

    /// <summary>
    /// A row built for nothing is an empty row, not a crash.
    /// </summary>
    /// <remarks>
    /// <c>FuncDataTemplate&lt;string&gt;</c> matches a null as readily as a path — every reference type
    /// does — and a <c>ListBox</c> asks for a row whenever its items go, which here is every keystroke:
    /// a refill is a Clear followed by the new matches. The row is built inside a layout pass, so the
    /// exception this guards against does not come back as an empty popup, it takes the tile down while
    /// somebody is typing into it.
    /// </remarks>
    [Fact]
    public void A_row_for_no_path_is_built_rather_than_thrown()
    {
        OnUiThread(() =>
        {
            var (_, list) = Wired("Goal.cs");

            Assert.NotNull(list.ItemTemplate!.Build(null));
        });
    }

    /// <summary>
    /// A box that comes back is wired to the suggestions again, selection included.
    /// </summary>
    /// <remarks>
    /// <para>The counterpart of <see cref="A_box_off_the_screen_stops_listening"/>: unwiring is
    /// reversible, because an answer box leaves the question list's template and comes back every time
    /// the questions are rebuilt.</para>
    /// <para><b>What this does not cover, and cannot:</b> whether the popup is on screen. It does not
    /// open in a headless top level — which is why nothing in this class asserts its visibility — so
    /// the other half of the same fix, <c>SyncVisibility</c> being called from <c>Wire</c> and on
    /// <c>GotFocus</c> rather than only from a change notification, is held by the reasoning written
    /// there and not by a test. The bug it closes is that <c>IsOpen</c> is a bool: a view model that is
    /// already open sets true over true and raises nothing, so a wiring that began life disagreeing
    /// with it never heard otherwise.</para>
    /// </remarks>
    [Fact]
    public void A_box_that_comes_back_is_listening_again()
    {
        OnUiThread(() =>
        {
            var mentions = new FileMentionsViewModel(new ReadySource("Goal.cs", "GoalTileView.axaml"));
            var box = new TextBox();
            var window = new Window { Content = box, Width = 400, Height = 200 };
            window.Show();
            FileMentionBehavior.SetMentions(box, mentions);
            var list = FileMentionBehavior.GetSuggestionList(box)!;

            window.Content = null;
            Type(mentions, "@goal");
            Assert.Equal(-1, list.SelectedIndex);

            window.Content = box;
            Type(mentions, "@goal");

            Assert.Equal(mentions.SelectedIndex, list.SelectedIndex);
            Assert.Equal(mentions.SelectedPath, list.SelectedItem);
        });
    }

    /// <summary>
    /// A second wiring over the same suggestions picks up what they already say.
    /// </summary>
    /// <remarks>
    /// What a re-applied template does: the old popup is detached and a new one built over a view model
    /// that is already open with a row picked. Nothing will announce that state to it, so it has to ask
    /// — which is what <c>Wire</c> now does, and what leaving the list lit depends on.
    /// </remarks>
    [Fact]
    public void A_second_wiring_adopts_the_pick_the_suggestions_already_hold()
    {
        OnUiThread(() =>
        {
            var mentions = new FileMentionsViewModel(new ReadySource("Goal.cs", "GoalTileView.axaml"));
            var box = new TextBox();
            new Window { Content = box, Width = 400, Height = 200 }.Show();
            FileMentionBehavior.SetMentions(box, mentions);

            Type(mentions, "@goal");
            Assert.True(mentions.IsOpen);

            FileMentionBehavior.SetMentions(box, null);
            FileMentionBehavior.SetMentions(box, mentions);

            var list = FileMentionBehavior.GetSuggestionList(box)!;

            Assert.Equal(mentions.SelectedIndex, list.SelectedIndex);
            Assert.Equal(mentions.SelectedPath, list.SelectedItem);
        });
    }

    /// <summary>Runs <paramref name="test"/> against a focused box with a template, so there is laid-out
    /// text to measure the <c>@</c> in.</summary>
    /// <remarks>The test application carries no theme, and a <c>TextBox</c> without one has no presenter.
    /// The theme is taken off again afterwards: the headless application is shared by the whole assembly,
    /// and a theme left on it templates every control a later test builds.</remarks>
    private static void WithFocusedWideBox(Action<TextBox> test)
    {
        var app = Avalonia.Application.Current!;
        var theme = new Avalonia.Themes.Fluent.FluentTheme();
        app.Styles.Insert(0, theme);

        var box = new TextBox();
        var window = new Window { Content = box, Width = 800, Height = 200 };
        try
        {
            window.Show();
            FileMentionBehavior.SetMentions(box, new FileMentionsViewModel(new ReadySource("Goal.cs")));
            box.Focus();
            test(box);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
        }
    }

    private static void LayOut(TextBox box)
    {
        box.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The list is hung from the <c>@</c> being typed, not from the box's left edge.
    /// </summary>
    /// <remarks>
    /// In a wide tile the box's corner is half a screen away from the end of a long line, which is where
    /// the eye is when a mention is started there.
    /// </remarks>
    [Fact]
    public void The_list_is_hung_from_the_at_sign()
    {
        OnUiThread(() => WithFocusedWideBox(box =>
        {
            box.Text = "a sentence long enough to push the mention well to the right @go";
            box.CaretIndex = box.Text.Length;
            LayOut(box);

            var anchor = FileMentionBehavior.GetAnchor(box);
            Assert.NotNull(anchor);
            Assert.True(anchor.Value.X > 200, $"anchored at {anchor.Value.X}");
        }));
    }

    /// <summary>The <c>@</c>, not the caret: typing the query does not walk the list to the right.</summary>
    [Fact]
    public void Typing_the_query_leaves_the_list_where_it_is()
    {
        OnUiThread(() => WithFocusedWideBox(box =>
        {
            box.Text = "see @g";
            box.CaretIndex = box.Text.Length;
            LayOut(box);
            var before = FileMentionBehavior.GetAnchor(box);

            box.Text = "see @goal";
            box.CaretIndex = box.Text.Length;
            LayOut(box);

            Assert.NotNull(before);
            Assert.Equal(before, FileMentionBehavior.GetAnchor(box));
        }));
    }
}
