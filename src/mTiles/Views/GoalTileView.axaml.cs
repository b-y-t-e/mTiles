using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
// Not unused, however it looks: SetTextAsync is an extension method on IClipboard living
// in this namespace, and the interface itself is never named here.
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;

namespace mTiles.Views;

public partial class GoalTileView : UserControl
{
    private GoalTileViewModel? _subscribedVm;

    public GoalTileView()
    {
        InitializeComponent();

        // Tunnelling, because the dialog is up over a tile whose composer and answer boxes have their
        // own Escape handling and would otherwise take the key first. The bubble phase would reach
        // this only if nothing below it wanted the key, which is exactly backwards: while a modal is
        // open it is the modal that Escape belongs to.
        AddHandler(KeyDownEvent, OnTileKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Escape closes the findings dialog, and only when one is open.</summary>
    /// <remarks>
    /// Marked handled only when it actually closed something. A tile that swallowed Escape whether or
    /// not it had a use for it would take it from the composer, where it clears the box.
    /// </remarks>
    private void OnTileKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (DataContext is not GoalTileViewModel { IsShowingFindings: true } vm) return;

        vm.CloseFindingsCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Clicking the scrim closes the dialog, as it does on every other modal here.</summary>
    /// <remarks>
    /// The scrim is its own element covering the tile, so a press that reaches it is a press outside
    /// the card - no need to ask whether the source is inside, the way a dialog that draws its own
    /// backdrop has to.
    /// </remarks>
    private void FindingsScrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is GoalTileViewModel vm) vm.CloseFindingsCommand.Execute(null);
        e.Handled = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm.Messages.CollectionChanged -= OnMessagesChanged;

            // ConfirmAction too, and for more than tidiness: the closure holds this view, so a view
            // model left with it keeps the view alive — and if that view model ever asks again, the
            // dialog opens over whatever this view is showing now, which is somebody else's tile.
            _subscribedVm.ConfirmAction = null;
            _subscribedVm = null;
        }

        if (DataContext is GoalTileViewModel vm)
        {
            _subscribedVm = vm;

            // The collection, and only the collection. There used to be a hook on the view model as
            // well, called where the workflow adds a message — which is most of them and not all: a
            // tile reopened from its file fills the transcript without going through it, and opened on
            // a finished run it showed the top of a conversation whose interesting end was several
            // screens down. Watching the collection covers both, and covers the hook's cases twice
            // over: every message cost two synchronous UpdateLayout passes over the whole transcript,
            // markdown views included, and four ScrollToEnd calls. One event is the whole answer.
            vm.Messages.CollectionChanged += OnMessagesChanged;
            ScrollTranscriptToEnd();
            vm.ConfirmAction = async message =>
            {
                // No window to ask in means no, the same answer the Settings dialog gives. The view
                // model already refuses when nothing is wired at all, and this is the only other way
                // the question can go unasked — answering yes here would have discarded a transcript
                // on the strength of a question nobody saw.
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null) return false;
                var box = MsBox.Avalonia.MessageBoxManager.GetMessageBoxStandard(
                    "Confirm", message,
                    MsBox.Avalonia.Enums.ButtonEnum.YesNo, MsBox.Avalonia.Enums.Icon.Question);
                var result = await box.ShowWindowDialogAsync(window);
                return result == MsBox.Avalonia.Enums.ButtonResult.Yes;
            };
            vm.PropertyChanged += OnVmPropertyChanged;
            UpdatePhaseDot(vm.CurrentPhase);

            // What the tile is already asking, before anything has changed. Without it a reopened tile
            // waiting on a plan reads its first RefreshAsk as the plan arriving — which is harmless
            // here only because attaching scrolls to the end anyway, and would stop being harmless the
            // day it did not.
            _showing.Clear();
            foreach (var name in AskFlags)
                _showing[name] = Showing(vm, name) ?? false;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            FollowTheEndSoon();
    }

    /// <summary>
    /// Decides now, scrolls once.
    /// </summary>
    /// <remarks>
    /// <para>One change to the view model reaches here several times: setting <c>IsRunning</c> raises
    /// <c>CanDetectGoal</c>, <c>HasFinishedRunActions</c>, the three ask flags and then itself, four of
    /// which this view follows — so every boundary of a run paid for four synchronous
    /// <c>UpdateLayout</c> passes over the whole transcript, markdown views included, and eight
    /// <c>ScrollToEnd</c> calls, to end where the first one already was. Exactly the cost that was
    /// taken out of the per-message path by watching the collection instead of a hook, and it grew back
    /// on the other side.</para>
    /// <para><b>The decision cannot be deferred with the work.</b> Whether to follow at all is "was the
    /// reader at the bottom <em>before</em> this arrived", and the answer is only readable while the
    /// new content is still unmeasured — a turn later the extent has grown and every reader looks
    /// scrolled up. So it is taken on the first call of the turn and kept; the later calls of the same
    /// turn can only add <c>force</c>, never take the answer back. Which also settles what was
    /// previously decided four times against an extent that the first of the four had already
    /// changed.</para>
    /// </remarks>
    private void FollowTheEndSoon(bool force = false)
    {
        _scrollForce |= force;

        if (_scrollQueued) return;
        _scrollQueued = true;
        _scrollWanted = force || IsNearTheEnd();

        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            var wanted = _scrollWanted || _scrollForce;
            _scrollForce = false;

            if (wanted) ScrollTranscriptToEnd();
        }, DispatcherPriority.Loaded);
    }

    private bool _scrollQueued;
    private bool _scrollWanted;
    private bool _scrollForce;

    /// <summary>
    /// Goes to the end of the transcript, unconditionally.
    /// </summary>
    /// <remarks>
    /// <para><b>Whether</b> to follow is not asked here and must not be — it is
    /// <see cref="FollowTheEndSoon"/>'s, taken while the new content is still unmeasured. This is only
    /// the doing, and it is reached having already been decided. It had a <c>force</c> parameter with a
    /// guard behind it, left over from when the two were one method; both callers passed true, so the
    /// guard was unreachable and the paragraph explaining it described a rule that had moved. A third
    /// caller written against that paragraph would have got a decision taken a turn late, against an
    /// extent that had already grown — which is the one thing the rule exists to prevent.</para>
    /// <para>The scroll happens twice, and both are needed. <c>UpdateLayout</c> forces the new message
    /// to be measured so <c>ScrollToEnd</c> has the real extent to scroll to — without it the call used
    /// the old one and stopped a message short, which is the bug this replaced. The posted one catches
    /// what sizes late: a rendered markdown answer arrives at its final height after its own pass, and
    /// <c>Loaded</c> is the priority that runs once layout is done.</para>
    /// </remarks>
    private void ScrollTranscriptToEnd()
    {
        ChatScroll.UpdateLayout();
        ChatScroll.ScrollToEnd();
        Dispatcher.UIThread.Post(ChatScroll.ScrollToEnd, DispatcherPriority.Loaded);
    }

    /// <summary>Whether the reader is watching the run rather than reading back through it.</summary>
    private bool IsNearTheEnd() =>
        ChatScroll.Extent.Height - ChatScroll.Viewport.Height - ChatScroll.Offset.Y <= StuckToBottom;

    /// <summary>How far off the bottom still counts as watching the run rather than reading the
    /// history. A line and a half: enough that a message arriving as the last one is measured does not
    /// break the follow, and short enough that a deliberate scroll up does.</summary>
    private const double StuckToBottom = 48;

    /// <summary>
    /// Puts one thing in the conversation on the clipboard — a message, a finding, or a question with
    /// what was answered to it.
    /// </summary>
    /// <remarks>
    /// <para>One handler for all of them, because a copy button is the same button wherever it is and
    /// the only thing that differs is what its own <c>DataContext</c> happens to be. Four handlers doing
    /// this would be four copies of the clipboard's failure path and of the tick, which is where the
    /// third one quietly stops matching the other two.</para>
    /// <para>In the view rather than in the view model, because a clipboard belongs to a window: reaching
    /// one needs a <c>TopLevel</c>, and a view model that holds one holds the window open. The
    /// alternative was a command on the tile reached from inside the item template by walking up out of
    /// its <c>ItemsControl</c> — the binding that has already failed silently once in this file.</para>
    /// <para>The icon becomes a tick for a moment. A copy button that does nothing visible leaves the
    /// user pressing it again, and the second press is indistinguishable from the first not having
    /// worked.</para>
    /// </remarks>
    private async void CopyItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        await CopyAsync(button, TextOf(button.DataContext));
    }

    /// <summary>
    /// Every finding in this review, as one block on the clipboard.
    /// </summary>
    /// <remarks>
    /// The findings and not the message: the verdict line above them is this tile's own bookkeeping,
    /// and what the list is being taken away for is the defects in it. The message-level button beside
    /// the verdict is still the one that hands over the whole review.
    /// </remarks>
    private async void CopyAllFindings_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not GoalMessage message) return;
        await CopyAsync(button, GoalTranscript.Copyable(message.Findings));
    }

    /// <summary>
    /// The problems in this review — blockers, errors and warnings — without the suggestions.
    /// </summary>
    /// <remarks>
    /// The set is <see cref="GoalMessage.Problems"/>, which is also what the button's own count comes
    /// from, so the label and the clipboard cannot disagree about which findings those are. Suggestions are the
    /// part of a review a reader skims and a tracker does not want; taking them out is most of why a
    /// second button earns its place beside the first.
    /// </remarks>
    private async void CopyProblems_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not GoalMessage message) return;
        await CopyAsync(button, GoalTranscript.Copyable(message.Problems));
    }

    /// <summary>
    /// Puts text on the clipboard and answers on the button that asked for it.
    /// </summary>
    /// <remarks>
    /// One body for every copy button in the tile, whatever it copies: the clipboard's failure path and
    /// the tick that says it worked are the parts that quietly stop matching when each button carries
    /// its own copy of them. The tick lands on the button's icon wherever it is — on its own as the
    /// content, or beside a word inside a panel — because the label is what must not move: a word
    /// swapped for "Copied" changes the control's width in the middle of a list.
    /// </remarks>
    private async Task CopyAsync(Button button, string text)
    {
        if (text.Length == 0) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            // A clipboard can be held by another application. Not worth a dialog over a convenience.
            System.Diagnostics.Trace.TraceWarning($"Copying failed: {ex.Message}");
            return;
        }

        if (IconIn(button.Content) is not { } icon) return;

        icon.Kind = MaterialIconKind.Check;
        await Task.Delay(CopiedFeedback);

        // Checked again: a row is reused as the list changes, so by now this button may be showing a
        // different message — and it is still the same button, so it is still the tick that has to come
        // off.
        if (icon.Kind == MaterialIconKind.Check) icon.Kind = MaterialIconKind.ContentCopy;
    }

    /// <summary>The icon a copy button answers on: its whole content, or the one beside its label.
    /// </summary>
    private static MaterialIcon? IconIn(object? content) => content switch
    {
        MaterialIcon icon => icon,
        Panel panel => panel.Children.OfType<MaterialIcon>().FirstOrDefault(),
        _ => null,
    };

    /// <summary>
    /// What each of the four things a copy button can be attached to reads as, on the clipboard.
    /// </summary>
    /// <remarks>
    /// <para>Every case goes through <see cref="GoalTranscript"/>, so a finding copied on its own reads
    /// exactly as it does inside the review it came from, and a question copied on its own reads as it
    /// does inside the round. Two spellings of the same thing is the failure this avoids — and it is not
    /// hypothetical: a message's own <c>Text</c> is only the head of a review, so copying that alone
    /// once handed somebody a verdict with the defects it counted missing.</para>
    /// <para>Internal so the mapping can be stated in a test without a window: what a button hands over
    /// is a decision, and the clipboard it hands it to is not.</para>
    /// </remarks>
    internal static string TextOf(object? data) => data switch
    {
        GoalMessage message => GoalTranscript.Copyable(message),
        GoalFinding finding => GoalTranscript.Copyable(finding),
        GoalQuestion question => GoalTranscript.Copyable(question),

        // The live block, where the answer is still being typed: the view model's own snapshot, which is
        // also what the record is written from, so what is copied mid-round and what is copied out of
        // the record afterwards are made by one method.
        GoalQuestionAnswer asking => GoalTranscript.Copyable(asking.Snapshot()),
        _ => "",
    };

    /// <summary>How long the tick stays. Long enough to be seen, short enough that a second copy of the
    /// next message does not find the button still congratulating itself about the last one.</summary>
    private static readonly TimeSpan CopiedFeedback = TimeSpan.FromSeconds(1.1);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GoalTileViewModel vm) return;

        if (e.PropertyName == nameof(GoalTileViewModel.CurrentPhase))
            UpdatePhaseDot(vm.CurrentPhase);

        if (e.PropertyName == nameof(GoalTileViewModel.ShowQuestions) && vm.ShowQuestions)
            FocusFirstAnswer();

        if (e.PropertyName is not { } name) return;

        // Everything the tile asks of the user is now a block at the end of the conversation, so each of
        // these changes the length of the thing being scrolled without adding a message — and the
        // follow-to-the-bottom rule is driven by the message collection. Without this the block appears
        // below the fold on a full transcript, which is the one moment it is the only thing worth
        // looking at.
        //
        // A block that has just *appeared* is followed whether or not the reader had scrolled up, which
        // is the one place that rule is overruled. While the bars were docked it did not arise: they
        // were on screen at any offset. Now a plan waiting to be approved, or a composer coming back,
        // arrives off screen while the composer that was there vanishes — leaving a tile that looks
        // like it is doing nothing and offers nowhere to type. That is not a message streaming past
        // during a run, which is what the rule protects a reader from; it is the tile stopping and
        // needing an answer, and it happens a handful of times in a run rather than a dozen.
        if (Showing(vm, name) is { } showing)
        {
            // Through Appeared rather than spelled out again here. The rule is one line, which is
            // exactly what makes a second copy of it cheap to write and invisible once written: the
            // tests pin Appeared, so a condition added to a copy in this handler would ship green.
            // What this method owns is the *remembering* — Appeared needs a previous value and a view
            // model has none.
            var appeared = Appeared(vm, name, _showing.GetValueOrDefault(name));
            _showing[name] = showing;

            // A block going away still changes where the end is, so the ordinary follow applies to it —
            // it just does not overrule anybody.
            FollowTheEndSoon(force: appeared);
            return;
        }

        if (FollowsTheEnd.Contains(name))
            FollowTheEndSoon();
    }

    /// <summary>
    /// What each of the four blocks is showing now, and null for a name that is not one of them.
    /// </summary>
    /// <remarks>
    /// <para>Read rather than listed, so a name here that cannot be answered does not compile — the
    /// alternative was a second set beside the first, where "in the set" and "how to read it" drift.</para>
    /// <para>Only the current state, deliberately: turning it into <em>arriving</em> needs the previous
    /// one, and where that is remembered is the view. Kept apart so this half stays pure and the
    /// transition can be stated in a test — see <see cref="Appeared"/>.</para>
    /// <para><c>CanDetectGoal</c> is deliberately not here, though it shows a block like the rest. It is
    /// fed by the git watcher, so it turns over when a file changes in a terminal tile next door — and
    /// yanking somebody's reading position because of an edit made somewhere else is worse than the
    /// offer arriving quietly. <c>IsRunning</c> is not here either: the waiting dots are information
    /// about a run, not a request, and they arrive a dozen times to the requests' handful.</para>
    /// <para>Internal so the rule can be stated in a test, as <see cref="TextOf"/> is: which changes
    /// overrule a reader's scroll position is a decision, and the scroller it overrules is not.</para>
    /// </remarks>
    internal static bool? Showing(GoalTileViewModel vm, string name) => name switch
    {
        nameof(GoalTileViewModel.ShowQuestions) => vm.ShowQuestions,
        nameof(GoalTileViewModel.ShowApproval) => vm.ShowApproval,
        nameof(GoalTileViewModel.ShowComposer) => vm.ShowComposer,
        nameof(GoalTileViewModel.HasFinishedRunActions) => vm.HasFinishedRunActions,
        _ => null,
    };

    /// <summary>
    /// Whether this change is one of the tile's requests <em>arriving</em>.
    /// </summary>
    /// <remarks>
    /// <para>The rule this view is written to, and the only statement of it — <c>OnVmPropertyChanged</c>
    /// calls this rather than repeating the comparison, which it did for one round: a one-line rule is
    /// the cheapest kind to copy and the hardest kind to notice twice, and with the tests pinning this
    /// one a change to the other would have shipped green.</para>
    /// <para>For a while the code said something else again: it forced on a block that was
    /// <em>showing</em> rather than one that had appeared, which is a different sentence every time a
    /// notification is raised for a value that did not move — and this view model raises all three ask
    /// flags together, unconditionally, several times a run. Only <em>false to true</em> counts: a
    /// block disappearing is not a reason to move anybody's view, and a block that was already there
    /// has not asked for anything.</para>
    /// </remarks>
    internal static bool Appeared(GoalTileViewModel vm, string name, bool wasShowing) =>
        Showing(vm, name) is true && !wasShowing;

    /// <summary>What each block was showing when it was last heard from. Seeded on attach, so the first
    /// notification about a tile that is already asking is not read as the ask arriving.</summary>
    private readonly Dictionary<string, bool> _showing = [];

    /// <summary>
    /// What, changing, moves the end of the conversation without being a request in its own right.
    /// </summary>
    /// <remarks>
    /// A set rather than a chain of comparisons, because the failure it guards against is a block added
    /// to the markup and forgotten here: one place to look, next to nothing else. It is the *end* being
    /// followed rather than each block in turn — whichever of them appears, the answer is the same.
    /// </remarks>
    private static readonly HashSet<string> FollowsTheEnd =
    [
        nameof(GoalTileViewModel.IsRunning),
        nameof(GoalTileViewModel.CanDetectGoal),
    ];

    /// <summary>The four <see cref="Showing"/> answers, for seeding. Named once; the switch is what
    /// decides, and a name here that it does not know seeds false and is never asked about again.
    /// </summary>
    private static readonly string[] AskFlags =
    [
        nameof(GoalTileViewModel.ShowQuestions),
        nameof(GoalTileViewModel.ShowApproval),
        nameof(GoalTileViewModel.ShowComposer),
        nameof(GoalTileViewModel.HasFinishedRunActions),
    ];

    /// <summary>
    /// Enter in the plan box sends, as it does in the composer and in an answer box.
    /// </summary>
    /// <remarks>
    /// This box takes line breaks, so Shift+Enter is the one that adds one. An empty box approves —
    /// the command decides that, not this, so Enter means the same thing the button says it does.
    /// </remarks>
    private void PlanBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        if (DataContext is not GoalTileViewModel vm || IsPickingAFile) return;

        vm.ApproveOrChangeCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// Puts the caret in the first answer box when the panel arrives.
    /// </summary>
    /// <remarks>
    /// <para>The questions replace the composer, which is where the caret was: without this the panel
    /// appears and the next keystroke goes nowhere, so answering starts with a click nobody should have
    /// to make. Only the first box — the rest are a Tab away, and moving the caret for the user more
    /// than once is taking the keyboard off them.</para>
    /// <para>Driven by the panel appearing, not by the list being attached to the tree. The list is
    /// attached once, when the tile is built, which is before any question exists and never again — so
    /// the second round of questions, and every round after it, got no focus at all.</para>
    /// </remarks>
    private void FocusFirstAnswer()
    {
        // After layout: the container for the first question does not exist the moment the flag flips.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            QuestionList.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus(),
            Avalonia.Threading.DispatcherPriority.Background);
    }


    /// <summary>
    /// Every phase class, so the dot can be told which one it is by setting all of them. The list is
    /// the reason <c>Classes.Clear()</c> is not used: clearing takes out whatever else was put on the
    /// element, which today is nothing and tomorrow is a bug nobody connects to this method.
    /// </summary>
    private static readonly (GoalPhase Phase, string Class)[] PhaseClasses =
    [
        (GoalPhase.Clarify, "phase-clarify"),
        (GoalPhase.Plan, "phase-plan"),
        (GoalPhase.Implement, "phase-implement"),
        (GoalPhase.Review, "phase-review"),
        (GoalPhase.Summary, "phase-summary"),
        (GoalPhase.Goal, "phase-goal"),
    ];

    /// <summary>
    /// The phase becomes a style class, not a brush: the class carries a <c>DynamicResource</c> fill,
    /// so the dot follows a theme change on its own. Resolving the brush here painted it once, with
    /// whatever the palette held at the time.
    /// </summary>
    private void UpdatePhaseDot(GoalPhase phase)
    {
        // A phase the enum does not know — a hand-edited file saying 99 — falls back to the Goal
        // marker rather than to no class at all, which is a dot with no fill.
        var known = PhaseClasses.Any(c => c.Phase == phase) ? phase : GoalPhase.Goal;

        foreach (var (p, cls) in PhaseClasses)
            PhaseDot.Classes.Set(cls, p == known);
    }

    /// <summary>
    /// Puts the criteria fields back to what the tile is really using, once the user has left one.
    /// <para>These are text boxes bound to integers, and Avalonia surfaces a failed conversion as a
    /// binding error rather than as data validation — so the property is simply never set, the
    /// <c>:error</c> pseudo-class never fires, and "50x" sits in the box looking like a setting. This
    /// makes it go away at the moment the user stops typing, which is late enough not to fight anyone
    /// entering "10" one digit at a time.</para>
    /// </summary>
    private void NumberBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GoalTileViewModel vm)
            vm.Criteria.Refresh();
    }

    /// <summary>
    /// The composer draws the field's border, so it has to show the field's focus as well.
    /// </summary>
    private void InputBox_FocusChanged(object? sender, RoutedEventArgs e)
        => Composer.Classes.Set("focused", InputBox.IsFocused);

    /// <summary>
    /// The composer looks like one field with a prompt in it, so the whole of it has to behave like
    /// one: clicking the padding, or the prompt glyph, puts the caret in the box. Clicks that land on
    /// the field or the Send button are left alone — those already do the right thing.
    /// </summary>
    private void Composer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source &&
            (source.FindAncestorOfType<TextBox>(includeSelf: true) != null ||
             source.FindAncestorOfType<Button>(includeSelf: true) != null))
        {
            return;
        }

        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
    }

    /// <summary>
    /// Enter in a question's answer box sends every answer, as Enter in the composer sends the message.
    /// </summary>
    /// <remarks>
    /// The box refuses line breaks, so without this Enter was the one key that did nothing at all in a
    /// panel whose entire purpose is typing answers — and the button is at the bottom of a list that
    /// may be scrolled away from the question being answered.
    /// </remarks>
    private void AnswerBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        if (DataContext is not GoalTileViewModel vm || IsPickingAFile) return;

        // Through the command, so the "answer at least one" rule is the same one whichever way the
        // answers are sent.
        vm.SendAnswersCommand.Execute(null);
        e.Handled = true;
    }

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && !IsPickingAFile)
        {
            // Only with something typed, and that is the whole rule: Enter is what sends what is in
            // the box, and on an empty box it has always been a no-op. Wired straight to the primary
            // segment it stopped being one — an empty box beside uncommitted changes reads as
            // "Detect goal", so a stray Enter on a fresh tile started a paid run (the tile has nothing
            // to discard, so the confirmation lets it through in silence) that nobody asked for.
            // Detection is a click, not a keystroke; the primary command still dispatches for both, so
            // a typed goal goes the one way its label says.
            if (DataContext is GoalTileViewModel { HasTypedGoal: true } vm &&
                vm.PrimaryActionCommand.CanExecute(null))
            {
                vm.PrimaryActionCommand.Execute(null);
                e.Handled = true;
            }
        }

        if (e.Key != Key.V) return;

        // Alt+V is the image whatever else is on the clipboard, and nothing else wants the key — the
        // box ignores it — so it is marked handled and taken outright. Ctrl+V is deliberately *not*
        // marked: the box's own paste has to go on working, and whether there is an image to take
        // instead cannot be known here, because reading a clipboard is asynchronous and the key has
        // been dispatched long before the answer comes back. Letting both run is safe precisely
        // because the two are exclusive — the image is taken only when there is no text, which is the
        // case in which the box's paste does nothing at all.
        if (e.KeyModifiers == KeyModifiers.Alt)
        {
            e.Handled = true;
            _ = AttachClipboardImageAsync(evenWhenThereIsText: true);
        }
        else if (e.KeyModifiers == KeyModifiers.Control)
        {
            _ = AttachClipboardImageAsync(evenWhenThereIsText: false);
        }
    }

    /// <summary>
    /// Hands the clipboard's image to the tile, as PNG bytes.
    /// </summary>
    /// <remarks>
    /// <para><b>Text wins when the clipboard holds both</b>, which is the rule the terminal tile
    /// already follows: a copy from a browser or a screenshot tool routinely puts text and an image on
    /// the clipboard at once, and pasting the picture instead of the words the user selected is the
    /// more surprising of the two mistakes. <b>Alt+V</b> is the way past it, exactly as it is in a
    /// terminal tile.</para>
    /// <para>Encoded here rather than in the view model: what Avalonia hands back is a decoded bitmap,
    /// and turning one into bytes needs the imaging stack. The view model is given something it can be
    /// handed by a test.</para>
    /// </remarks>
    private async Task AttachClipboardImageAsync(bool evenWhenThereIsText)
    {
        if (DataContext is not GoalTileViewModel vm) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        try
        {
            if (!evenWhenThereIsText && await clipboard.TryGetTextAsync() is { Length: > 0 }) return;
            if (await clipboard.TryGetBitmapAsync() is not { } bitmap) return;

            using (bitmap)
            {
                using var png = new MemoryStream();
                bitmap.Save(png);
                vm.AttachImageCommand.Execute(png.ToArray());
            }
        }
        catch (Exception ex)
        {
            // A clipboard can be held by another application, and an image on it can be one this
            // machine cannot decode. Neither is worth a dialog over a paste that can be tried again.
            System.Diagnostics.Trace.TraceWarning($"Reading an image from the clipboard failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether the <c>@</c> suggestions are up, in which case Enter takes the file rather than sends.
    /// </summary>
    /// <remarks>
    /// <see cref="FileMentionBehavior"/> already takes Enter in the tunnel phase and marks it handled,
    /// which is what actually stops these handlers running. This is the second lock on the same door,
    /// and it is worth having: what it guards against is sending a goal with a half-typed <c>@go</c> in
    /// it, and that is not undone by pressing the key again.
    /// </remarks>
    private bool IsPickingAFile =>
        DataContext is GoalTileViewModel { FileMentions.IsOpen: true };
}
