using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Controls.Templates;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using mTiles.Views;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Notepad.Avalonia.Controls;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The ask panels as XAML, drawn.
/// </summary>
/// <remarks>
/// The view model's own tests say what a panel should hold; nothing there can say whether the markup
/// binds to it, and a panel bound to nothing looks exactly like a panel with nothing to show. What is
/// asked here is only what markup can get wrong: which panel is on screen, and whether the tile is the
/// thing deciding.
/// </remarks>
public class GoalAskPanelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-ask-" + Guid.NewGuid());

    public GoalAskPanelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory */ }
    }

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(GoalAskPanelTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private GoalTileViewModel Tile()
    {
        var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
        return new GoalTileViewModel(_dir, settings) { ConfirmAction = _ => Task.FromResult(true) };
    }

    private static GoalTileView Shown(GoalTileViewModel vm)
    {
        var view = new GoalTileView { DataContext = vm };
        var window = new Window { Content = view, Width = 620, Height = 480 };

        // The colour tokens the panel's styles reach for. Without them every DynamicResource resolves
        // to nothing and the test would be drawing a different control from the one users see.
        window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://mTiles/Styles/"))
        {
            Source = new Uri("avares://mTiles/Styles/AppTheme.axaml"),
        });

        window.Show();
        Pump();
        return view;
    }

    private static void Pump()
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    /// <summary>The ask panels, visible or not — both are always in the tree.</summary>
    private static List<Border> Asks(GoalTileView view) =>
        [..view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("ask"))];

    /// <summary>
    /// The control the transcript's own template would build for one message.
    /// </summary>
    /// <remarks>
    /// Reached through the row's <c>ContentControl</c>, whose <c>ContentTemplate</c> is the chooser the
    /// markup declares. Built by hand because item containers are created lazily and this headless
    /// window never gets far enough to create them — so a search of the visual tree finds nothing
    /// whatever the template says.
    /// </remarks>
    private static Control? BodyFor(IDataTemplate rowTemplate, GoalMessage message)
    {
        var row = rowTemplate.Build(message)!;
        row.DataContext = message;

        var chooser = row.GetLogicalDescendants().OfType<ContentControl>()
            .Select(c => c.ContentTemplate)
            .First(t => t is GoalMessageTemplate)!;

        var body = chooser.Build(message);
        if (body != null) body.DataContext = message;
        return body;
    }

    private static ItemsControl QuestionList(GoalTileView view) =>
        view.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "QuestionList");

    [Fact]
    public void Only_the_panel_for_what_is_being_asked_is_on_screen()
    {
        OnUiThread(() =>
        {
            using var vm = Tile();
            var view = Shown(vm);

            // An empty tile asks for a goal, and the composer is the whole of that. Both panels are in
            // the tree either way — what a user sees is which of them is visible.
            Assert.Equal(2, Asks(view).Count);
            Assert.True(vm.ShowComposer);
            Assert.DoesNotContain(Asks(view), b => b.IsVisible);

            vm.CurrentPhase = GoalPhase.Clarify;
            vm.Questions.Add(new GoalQuestionAnswer(1, new GoalQuestion { Question = "Which file?" }));
            Pump();

            // Bound to ShowQuestions, so asking is what puts it on screen — not a call from the code
            // behind that some other path could forget to make. And exactly one panel: the composer and
            // the two panels ask for the same thing in three shapes, and two of them at once is how an
            // answer ends up somewhere nobody is reading.
            Assert.True(vm.ShowQuestions);
            Assert.False(vm.ShowComposer);
            Assert.Single(Asks(view), b => b.IsVisible);
        });
    }

    [Fact]
    public void A_tile_with_nothing_in_it_can_be_asked_every_question_the_markup_asks()
    {
        OnUiThread(() =>
        {
            using var vm = Tile();

            // A fresh tile has proposed no plan, and ProposedPlan is nullable, so reading .Length off it
            // threw — inside a property the XAML reads the moment the panel binds. Every empty Goal
            // tile crashed on being shown, and nothing in the view model's own tests went near it,
            // because they all start by giving the tile something to do.
            Assert.False(vm.ShowApproval);
            Assert.False(vm.ShowQuestions);
            Assert.True(vm.ShowComposer);

            // Through the markup as well, which is the path that actually failed.
            var view = Shown(vm);
            Assert.DoesNotContain(Asks(view), b => b.IsVisible);
        });
    }

    /// <summary>
    /// Every message is drawn once, by one control.
    /// </summary>
    /// <remarks>
    /// The template holds two controls for one message and picks between them, so the failure mode is
    /// not "the wrong one" but "both": a second copy of every user and system message, drawn under the
    /// first, from an edit that replaced one control and left the other behind. Counting is the only
    /// assertion that catches it — asking which control is visible passes with two of them.
    /// </remarks>
    [Theory]
    [InlineData(GoalMessageRole.Assistant, true)]    // the tool's prose: markdown
    [InlineData(GoalMessageRole.Assistant, false)]   // a review this application composed, and every
                                                     // assistant message in a file written before the
                                                     // flag existed: shown as written
    [InlineData(GoalMessageRole.User, false)]
    [InlineData(GoalMessageRole.System, false)]
    public void A_message_is_drawn_by_exactly_one_control(GoalMessageRole role, bool markdown)
    {
        OnUiThread(() =>
        {
            using var vm = Tile();
            var view = Shown(vm);

            var template = view.GetVisualDescendants().OfType<ItemsControl>()
                .First(i => i.Name == "Transcript").ItemTemplate!;

            var message = new GoalMessage { Role = role, Text = "a line", Markdown = markdown };
            var body = BodyFor(template, message);

            // One control, by construction rather than by visibility. The row used to hold both and hide
            // one, which draws the right thing and builds the wrong one as well: a hidden control is
            // still constructed, and this transcript does not virtualise, so a long conversation carried
            // a MarkdownViewer per message that existed only to be invisible. It is also how a stale
            // copy of a control survived an edit and every message was drawn twice.
            Assert.NotNull(body);
            Assert.Single(new[] { body });
            Assert.Equal(markdown && role == GoalMessageRole.Assistant, body is MarkdownViewer);
        });
    }

    [Fact]
    public void Only_the_tools_own_words_are_rendered_as_markdown()
    {
        OnUiThread(() =>
        {
            using var vm = Tile();
            var view = Shown(vm);

            // The template is built by hand rather than by adding messages and looking for the rows.
            // Item containers are created lazily and this headless window never gets far enough to
            // create them, so a search of the visual tree finds nothing whatever the template says.
            var template = view.GetVisualDescendants().OfType<ItemsControl>()
                .First(i => i.Name == "Transcript").ItemTemplate!;

            static MarkdownViewer? ViewerFor(IDataTemplate template, GoalMessageRole role, string text)
            {
                // Markdown asked for, so the role is the only thing under test here.
                var message = new GoalMessage { Role = role, Text = text, Markdown = true };
                return BodyFor(template, message) as MarkdownViewer;
            }

            // Your own text is text you typed — asterisks in it are asterisks you meant — and a note
            // from the tile has no markup in it at all.
            Assert.Null(ViewerFor(template, GoalMessageRole.User, "*keep my asterisks*"));
            Assert.Null(ViewerFor(template, GoalMessageRole.System, "A note."));

            // And an assistant message this application composed rather than the tool wrote — which
            // is also how a goal file written before this flag existed reads back. That is the whole
            // reason the flag points this way: the other way round, every review already on disk came
            // back claiming to be prose and was re-flowed on the first restart. A review
            // is a column of severities with each detail indented under its title; markdown collapses
            // runs of spaces, reads a two-space indent as a continuation and turns a * inside a finding
            // into emphasis — so the one part of the transcript arranged to be read in columns was the
            // part being re-flowed.
            var composed = new GoalMessage
            {
                Role = GoalMessageRole.Assistant,
                Text = "error  src/Cart.cs:42\n  Total ignores discounts",
            };
            Assert.IsNotType<MarkdownViewer>(BodyFor(template, composed));

            var viewer = ViewerFor(template, GoalMessageRole.Assistant, "## Plan");
            Assert.NotNull(viewer);
            Assert.Equal("## Plan", viewer.MarkdownText);

            // And the control is told to leave the colours alone. Its default theme is Light, and it
            // assigns its own brushes over the ones set here — a white block in a dark tile.
            Assert.Equal(EditorTheme.None, viewer.ColorTheme);
        });
    }

    [Fact]
    public void The_markdown_view_wears_this_applications_colours_and_not_its_own()
    {
        OnUiThread(() =>
        {
            var view = new GoalMarkdownView { MarkdownText = "## Plan" };
            var window = new Window { Content = view, Width = 400, Height = 300 };
            window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://mTiles/Styles/"))
            {
                Source = new Uri("avares://mTiles/Styles/AppTheme.axaml"),
            });
            // The colour tokens are written into Application.Resources at run time by ThemeBridge,
            // derived from the active terminal theme, so a test window has to stand in for it.
            window.Resources["TextPrimary"] = Brushes.White;
            window.Resources["TextMuted"] = Brushes.Gray;
            window.Resources["AccentDefault"] = Brushes.SteelBlue;
            window.Resources["BgElevated"] = Brushes.DimGray;
            window.Resources["TerminalFontFamily"] = new FontFamily("Cascadia Mono");
            window.Resources["UiFontSize"] = 17.0;
            window.Show();
            Pump();

            // MarkdownViewer runs its own ApplyTheme in its constructor, with ColorTheme still at its
            // default of Light, and writes every brush as a local value: white ground, black text.
            // Markup cannot undo that — ColorTheme="None" arrives too late, and a Style loses to a local
            // value — which is why these are pushed after attachment instead. A probe caught this as
            // Foreground=Black on a viewer built straight from the transcript's template.
            Assert.Equal(Brushes.White, view.Foreground);
            Assert.Equal(Brushes.DimGray, view.CodeBackground);
            Assert.Equal(Brushes.Transparent, view.BackgroundBrush);
            Assert.Equal(EditorTheme.None, view.ColorTheme);

            // The font and the size the user chose in Settings, not the control's defaults. This is the
            // half that has no visible symptom until you know what you are looking at: a proportional
            // face in a tile that is otherwise entirely the terminal's.
            Assert.Equal("Cascadia Mono", view.DefaultFont.Name);
            Assert.Equal("Cascadia Mono", view.CodeFont.Name);
            Assert.Equal(17.0, view.DefaultFontSize);

            // And it follows a change on its own. Calling ApplyTokens by hand here would have proved
            // only that the method works — the thing that has to be right is the wiring, because
            // picking another theme or font size happens while a tile is open and nothing else is going
            // to tell this control about it.
            window.Resources["UiFontSize"] = 21.0;
            window.Resources["TextPrimary"] = Brushes.Yellow;
            Pump();

            Assert.Equal(21.0, view.DefaultFontSize);
            Assert.Equal(Brushes.Yellow, view.Foreground);
        });
    }

    [Fact]
    public void A_selection_in_a_rendered_answer_is_not_answered_with_text_from_a_terminal()
    {
        OnUiThread(() =>
        {
            // The window-level Ctrl+C handler runs before the focused control sees the key, so anything
            // holding a selection of its own has to be named or the copy is served from whichever
            // terminal still had one — in a tile the user is not even looking at. This is a bug that
            // was found once, fixed for SelectableTextBlock, and reintroduced the moment the tool's
            // answers stopped being one.
            Assert.True(TerminalClipboardCoordinator.HandlesItsOwnCopy(new GoalMarkdownView()));

            // The Note tile's editor is the same control's sibling and was never covered: the comment
            // said it was, and had said so since before notes stopped being AvaloniaEdit.
            Assert.True(TerminalClipboardCoordinator.HandlesItsOwnCopy(new Notepad.Avalonia.Controls.NoteEditor()));

            // And something that genuinely has no selection still falls through to the terminal.
            Assert.False(TerminalClipboardCoordinator.HandlesItsOwnCopy(new Border()));
        });
    }

    [Fact]
    public void The_question_list_is_bound_to_the_questions()
    {
        OnUiThread(() =>
        {
            using var vm = Tile();

            // The panel belongs to the phase that asks: questions on screen in any other phase are
            // left over from one the tile has moved on from.
            vm.CurrentPhase = GoalPhase.Clarify;
            vm.Questions.Add(new GoalQuestionAnswer(1, new GoalQuestion
            {
                Question = "Which file holds the port?",
                Why = "There are two candidates.",
                Options = ["appsettings.json", "launchSettings.json"],
            }));
            vm.Questions.Add(new GoalQuestionAnswer(2, new GoalQuestion { Question = "Sync or async?" }));

            var view = Shown(vm);

            // Asked of the list rather than of the visual tree: item containers are built lazily and a
            // headless window does not always get far enough to build them, so searching for the chips
            // proves nothing either way. What this does catch is the binding — a list bound to nothing
            // and a panel with no questions in it look identical on screen.
            Assert.Equal(2, QuestionList(view).ItemCount);
        });
    }

    /// <summary>
    /// The panel takes at most three fifths of the tile, and never grows on its own.
    /// </summary>
    /// <remarks>
    /// The ratchet is the part worth pinning. A clamped height is read straight back as "what the user
    /// wanted" on the next pass, so the rule has to be one that survives being applied to its own
    /// output: shrinking the tile clamps the panel, and growing the tile again must leave it where the
    /// clamp put it rather than expanding a panel nobody dragged.
    /// </remarks>
    [Theory]
    [InlineData(230, 800, 230)]    // room to spare: what was asked for
    [InlineData(230, 200, 120)]    // a small tile: three fifths of it, so the transcript survives
    [InlineData(230, 0, 230)]      // before the first layout there is nothing to measure
    public void The_panel_never_takes_more_than_its_share(double wanted, double available, double expected)
    {
        Assert.Equal(expected, GoalTileView.FitsIn(wanted, available));
    }

    [Fact]
    public void Clamping_does_not_ratchet_the_panel_open_again()
    {
        // 230 wanted, clamped to 120 by a short tile, and that 120 is what the next pass reads back.
        var clamped = GoalTileView.FitsIn(230, 200);
        Assert.Equal(120, clamped);

        // The tile grows again. The panel stays where the clamp left it: the user asked for 230 once,
        // the row has said 120 ever since, and inventing the difference back is not restoring anything.
        Assert.Equal(120, GoalTileView.FitsIn(clamped, 800));
    }

    /// <summary>
    /// A link in text a model wrote is a barrier, and the barrier is the address.
    /// </summary>
    /// <remarks>
    /// Tested as a function rather than through a dialog, for the reason <c>CommandDisplay</c> has its
    /// own test class: it is the security decision, and the window it is shown in is not.
    /// </remarks>
    [Theory]
    // Ordinary links, normalised on the way through — which is the point: what the dialog shows has to
    // be what the browser gets.
    [InlineData("https://example.com/x", "https://example.com/x")]
    [InlineData("http://example.com", "http://example.com/")]
    // The attack this exists for: a cyrillic "а" reads as apple.com and opens punycode. Showing the raw
    // text while opening the parsed address would be a barrier in name only.
    [InlineData("https://аpple.com/", "https://xn--pple-43d.com/")]
    [InlineData("https://example.com/a/../b", "https://example.com/b")]
    // Not every scheme some application on this machine has registered for itself.
    [InlineData("file:///C:/Windows/System32/cmd.exe", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("ms-msdt:/id", null)]
    [InlineData("not a url", null)]
    [InlineData("", null)]
    public void A_link_is_only_opened_when_its_address_can_be_shown_and_read(string url, string? opening)
    {
        Assert.Equal(opening, GoalMarkdownView.LinkToOpen(url));
    }

    [Fact]
    public void A_link_too_long_to_read_is_refused_rather_than_shortened()
    {
        // The same rule a verify command gets, and for the same reason: truncating into the dialog moves
        // the payload past the ellipsis rather than removing it.
        var huge = "https://example.com/" + new string('a', CommandDisplay.MaxConsentable);

        Assert.Null(GoalMarkdownView.LinkToOpen(huge));
    }
}
