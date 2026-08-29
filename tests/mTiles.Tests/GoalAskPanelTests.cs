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
    /// Nothing the tile asks of the user is pinned to its bottom edge: every one of them is a block
    /// inside the conversation.
    /// </summary>
    /// <remarks>
    /// <para>The whole of what this rearrangement is, asked of the markup — which is the only place it
    /// can be got wrong. A block moved back out of the scroller looks perfectly reasonable in the file
    /// and is a bar across the foot of the tile again on screen, and the view model cannot tell: every
    /// one of these was bound to exactly the same property before and after.</para>
    /// <para>Asked as "is <c>ChatScroll</c> an ancestor" rather than by counting children, because what
    /// matters is that it scrolls with the conversation, not where in the column it was put.</para>
    /// </remarks>
    [Fact]
    public void Everything_the_tile_asks_for_scrolls_with_the_conversation()
    {
        OnUiThread(() =>
        {
            using var vm = Tile();
            var view = Shown(vm);

            var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First(c => c.Name == "ChatScroll");

            // The two ask blocks — the questions and the plan — and the composer. All three are in the
            // tree whether or not they are showing, so this holds before anything has been asked.
            var blocks = Asks(view)
                .Concat(view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("composer")))
                .ToList();

            Assert.Equal(3, blocks.Count);
            Assert.All(blocks, b => Assert.Contains(scroller, b.GetVisualAncestors()));

            // And the transcript is in there with them, which is the point: one scroller, not two
            // fighting each other for the tile's height.
            Assert.Contains(scroller,
                view.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "Transcript")
                    .GetVisualAncestors());
        });
    }

    /// <summary>
    /// Which changes overrule the reader's scroll position, and which leave it alone.
    /// </summary>
    /// <remarks>
    /// <para>The follow-to-the-bottom rule stands down when the reader has scrolled up, which is right
    /// for the dozen messages a run posts and wrong for the handful of moments the tile stops and needs
    /// an answer. While those were bars docked under the transcript it could not come up — they were on
    /// screen at any offset. In the conversation they are not: the composer vanishes from where it was
    /// and comes back below the fold, and what is left on screen is a tile that appears to be doing
    /// nothing with nowhere to type. The round of questions was covered by accident, because taking the
    /// keyboard drags it into view; the plan and the composer had nothing equivalent.</para>
    /// <para>The two exclusions are the part worth pinning. <c>CanDetectGoal</c> is fed by the git
    /// watcher, so it turns over when a file changes in a terminal tile next door — forcing on it would
    /// move somebody's reading position because of an edit made somewhere else entirely. And nothing
    /// forces on the way <em>out</em>: a block disappearing is not a reason to move anybody's view.
    /// </para>
    /// </remarks>
    [Fact]
    public void Only_a_request_arriving_overrules_where_the_reader_is()
    {
        OnUiThread(() =>
        {
            using var vm = Tile();

            // A fresh tile: the composer is up and nothing else is.
            Assert.True(vm.ShowComposer);
            Assert.True(Appears(vm, nameof(vm.ShowComposer)));
            Assert.False(Appears(vm, nameof(vm.ShowApproval)));
            Assert.False(Appears(vm, nameof(vm.ShowQuestions)));
            Assert.False(Appears(vm, nameof(vm.HasFinishedRunActions)));

            // A round is asked. The questions overrule; the composer, now gone, does not — which is the
            // going-away case, and it fires on exactly the same property name.
            vm.CurrentPhase = GoalPhase.Clarify;
            vm.Questions.Add(new GoalQuestionAnswer(1, new GoalQuestion { Question = "Which file?" }));

            Assert.True(Appears(vm, nameof(vm.ShowQuestions)));
            Assert.False(vm.ShowComposer);
            Assert.False(Appears(vm, nameof(vm.ShowComposer)));

            // Neither of the two that are not requests, whatever the tile is doing.
            Assert.False(Appears(vm, nameof(vm.CanDetectGoal)));
            Assert.False(Appears(vm, nameof(vm.IsRunning)));

            // And a name nothing knows about is not a request either — the handler falls through to the
            // ordinary follow rather than forcing on every property the view model raises.
            Assert.False(Appears(vm, nameof(vm.CurrentPhase)));
        });
    }

    /// <summary>
    /// A block that was already showing has not arrived, however often it is announced.
    /// </summary>
    /// <remarks>
    /// <para>The rule is <em>arriving</em>, and for a while the code read <em>showing</em> — which is a
    /// different sentence every time a notification carries a value that did not move. This view model
    /// raises all three ask flags together and unconditionally, on every phase of every lap, at a
    /// moment when the composer has been up all along; so the tile scrolled a reader who had gone back
    /// through the transcript down to the bottom several times a run, over nothing having appeared.
    /// That is precisely the reader the whole rule exists to leave alone.</para>
    /// <para>Stated on the pure half so it can be asked without a window: the previous value is a
    /// parameter here and a small map in the view, seeded when it attaches.</para>
    /// </remarks>
    [Fact]
    public void A_block_that_was_already_there_has_not_arrived()
    {
        OnUiThread(() =>
        {
            using var vm = Tile();
            Assert.True(vm.ShowComposer);

            const string composer = nameof(GoalTileViewModel.ShowComposer);

            // The same state, announced twice. The first is the composer arriving; the second is
            // RefreshAsk saying so again, and it must not move anybody.
            Assert.True(GoalTileView.Appeared(vm, composer, wasShowing: false));
            Assert.False(GoalTileView.Appeared(vm, composer, wasShowing: true));

            // And going away is not arriving either, whatever was remembered.
            vm.CurrentPhase = GoalPhase.Clarify;
            vm.Questions.Add(new GoalQuestionAnswer(1, new GoalQuestion { Question = "Which file?" }));

            Assert.False(vm.ShowComposer);
            Assert.False(GoalTileView.Appeared(vm, composer, wasShowing: true));
            Assert.False(GoalTileView.Appeared(vm, composer, wasShowing: false));
        });
    }

    /// <summary>
    /// The other two arms, each seen answering yes.
    /// </summary>
    /// <remarks>
    /// <para>A five-armed switch that reads properties by name is exactly the shape a copy-paste
    /// survives: an arm returning its neighbour's property is indistinguishable from a correct one
    /// while every property it could return is false. Both of these are false on a fresh tile, so
    /// asserting them there proves only that nothing is on — which is what the test above could do,
    /// and no more.</para>
    /// <para>Reached by loading a state rather than running to it, because these are facts about a
    /// saved conversation and the constructor that takes a file is how a tile gets one. What is being
    /// pinned is that the plan waiting to be approved, and the row of things to do with a finished run,
    /// each pull the view to themselves — the failure otherwise being the one this whole rule exists
    /// for: the block arrives below the fold and the tile looks like it has stopped.</para>
    /// </remarks>
    [Fact]
    public void The_plan_and_the_finished_run_actions_are_requests_too()
    {
        OnUiThread(() =>
        {
            using var waitingForApproval = TileWith(new GoalTileState
            {
                OriginalGoal = "a goal",
                CurrentPhase = GoalPhase.Plan,
                ProposedPlan = "1. Do the thing.",
            });

            Assert.True(waitingForApproval.ShowApproval);
            Assert.True(Appears(waitingForApproval, nameof(GoalTileViewModel.ShowApproval)));

            // And not the arm next door, which is false in this very state — a swapped pair would pass
            // the assertion above and fail this one.
            Assert.False(waitingForApproval.ShowComposer);
            Assert.False(Appears(waitingForApproval, nameof(GoalTileViewModel.ShowComposer)));

            using var finished = TileWith(new GoalTileState
            {
                OriginalGoal = "a goal",
                CurrentPhase = GoalPhase.Summary,
                LastStopReason = GoalStopReason.Reviewed,
            });

            Assert.True(finished.HasFinishedRunActions);
            Assert.True(Appears(finished, nameof(GoalTileViewModel.HasFinishedRunActions)));
            Assert.False(Appears(finished, nameof(GoalTileViewModel.ShowApproval)));
        });
    }

    /// <summary>
    /// A stopped run offers Resume where everything else this tile asks for is answered.
    /// </summary>
    /// <remarks>
    /// The transcript's last line says to click Resume, and for a long time the only Resume on screen
    /// was the header's play glyph — 13 pixels at the far end of the tile from the sentence naming it,
    /// while the questions, the plan, Continue and Commit are all labelled buttons in the flow. What is
    /// pinned here is that the row comes up for a run that is merely stopped, and that it stands down
    /// where Resume would mean asking again over a block already asking.
    /// </remarks>
    [Fact]
    public void A_stopped_run_offers_Resume_in_the_conversation()
    {
        OnUiThread(() =>
        {
            using var stopped = TileWith(new GoalTileState
            {
                OriginalGoal = "a goal",
                CurrentPhase = GoalPhase.Implement,
                IsPaused = true,
            });

            Assert.True(stopped.ShowResume);
            Assert.True(stopped.CanResume);

            // The row itself, not just the flag: HasFinishedRunActions is what puts it on screen, and
            // every other arm of it is false in this state — a stopped run has finished nothing.
            Assert.True(stopped.HasFinishedRunActions);
            Assert.False(stopped.CanContinue);
            Assert.False(stopped.CanReReview);

            var view = Shown(stopped);
            Assert.Contains(view.GetVisualDescendants().OfType<Button>(),
                b => b.IsVisible && (b.Content as string) == "Resume");

            // And down where the plan is waiting: Resume re-runs the phase, which there means proposing
            // a plan again beside the one the user has not answered yet.
            using var waitingForApproval = TileWith(new GoalTileState
            {
                OriginalGoal = "a goal",
                CurrentPhase = GoalPhase.Plan,
                ProposedPlan = "1. Do the thing.",
                IsPaused = true,
            });

            Assert.True(waitingForApproval.ShowApproval);
            Assert.False(waitingForApproval.ShowResume);

            // And not offered at all in a phase Resume has nothing to run. Closing a tile mid-detection
            // is what reaches this — ClosingIsAPause pauses it in Goal — and the header, which asked
            // IsPaused on its own, showed an enabled play glyph whose only effect was to clear the
            // pause. Both buttons read these two properties now, so there is one answer.
            using var pausedBeforeAGoal = TileWith(new GoalTileState
            {
                CurrentPhase = GoalPhase.Goal,
                IsPaused = true,
            });

            Assert.False(pausedBeforeAGoal.ShowResume);
            Assert.False(pausedBeforeAGoal.HasFinishedRunActions);
            Assert.DoesNotContain(Shown(pausedBeforeAGoal).GetVisualDescendants().OfType<Button>(),
                b => b.IsVisible && (b.Content as string) == "Resume");
        });
    }

    /// <summary>Arriving: it is showing now and was not a moment ago.</summary>
    private static bool Appears(GoalTileViewModel vm, string name) =>
        GoalTileView.Appeared(vm, name, wasShowing: false);

    /// <summary>A tile reopened on a saved conversation, which is the only way to a phase it did not
    /// get to by being driven.</summary>
    private GoalTileViewModel TileWith(GoalTileState state)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        new GoalStatePersistence().Save(path, state);

        var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
        return new GoalTileViewModel(path, _dir, settings) { ConfirmAction = _ => Task.FromResult(true) };
    }

    /// <summary>
    /// A copy button hands over what it is attached to, whichever of the four things that is.
    /// </summary>
    /// <remarks>
    /// One handler serves the message, the finding, the answered question and the one still being
    /// typed, so the thing that can go wrong is a case falling through to the empty string — a button
    /// that does nothing, silently, on the one row somebody wanted. Every answer goes through
    /// <c>GoalTranscript</c>, so a finding copied alone reads as it does inside the review it came from.
    /// </remarks>
    [Fact]
    public void A_copy_button_hands_over_whatever_it_is_attached_to()
    {
        var finding = new GoalFinding { Severity = GoalSeverity.Error, Title = "Total ignores discounts" };
        var question = new GoalQuestion { Question = "Which file?", Answer = "appsettings.json" };

        Assert.Equal(GoalTranscript.Copyable(finding), GoalTileView.TextOf(finding));
        Assert.Contains("Total ignores discounts", GoalTileView.TextOf(finding));

        Assert.Contains("Which file?", GoalTileView.TextOf(question));
        Assert.Contains("appsettings.json", GoalTileView.TextOf(question));

        // The live block, mid-round: the answer is in the view model rather than in the model behind
        // it, and copying has to see what is in the box.
        var asking = new GoalQuestionAnswer(1, new GoalQuestion { Question = "Sync or async?" })
        {
            Answer = "async",
        };
        Assert.Contains("Sync or async?", GoalTileView.TextOf(asking));
        Assert.Contains("async", GoalTileView.TextOf(asking));

        Assert.Equal("make the tests pass",
            GoalTileView.TextOf(new GoalMessage { Role = GoalMessageRole.User, Text = "make the tests pass" }));

        // Anything else is nothing, and the handler returns before it reaches a clipboard.
        Assert.Equal("", GoalTileView.TextOf(null));
        Assert.Equal("", GoalTileView.TextOf(new object()));
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
        // Truncating into the dialog moves
        // the payload past the ellipsis rather than removing it.
        var huge = "https://example.com/" + new string('a', CommandDisplay.MaxConsentable);

        Assert.Null(GoalMarkdownView.LinkToOpen(huge));
    }
}
