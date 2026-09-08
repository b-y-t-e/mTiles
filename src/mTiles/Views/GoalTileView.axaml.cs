using System.Diagnostics;
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
    }


    /// <summary>The dialog this tile has open, if any.</summary>
    private GoalFindingsDialog? _findings;

    /// <summary>
    /// Opens and closes the findings dialog, which <see cref="OverlayHost"/> draws.
    /// </summary>
    /// <remarks>
    /// <para>It used to be a hand-written <c>Panel</c> in this view's markup, with its own scrim, card
    /// and close button — the fourth implementation of the same dialog. What it cost while it was:
    /// Alt+Space over a list of findings dictated into the very Goal tile the dialog was covering,
    /// because it covered only the tile and nothing knew it was modal.</para>
    /// <para>Two directions, and both are needed. The flag opening it is the badge being clicked; the
    /// task completing is the user closing it by the host's own X or Escape, which has to put the flag
    /// down or the badge would refuse to open it a second time.</para>
    /// </remarks>
    private async void ApplyFindingsModality(bool showing)
    {
        if (!showing)
        {
            if (_findings is { } open)
            {
                _findings = null;
                OverlayHost.CloseWith(open, null);
            }
            return;
        }

        if (_findings is not null || OverlayHost.For(this) is not { } host)
            return;

        var dialog = new GoalFindingsDialog { DataContext = DataContext };
        _findings = dialog;

        // async void: nothing may escape to the dispatcher, where it is a crash rather than a dialog
        // that failed to open.
        try
        {
            await host.ShowAsync<object>(dialog, width: 760);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"The findings dialog failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_findings, dialog))
            {
                _findings = null;
                (DataContext as GoalTileViewModel)?.CloseFindingsCommand.Execute(null);
            }
        }
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
                return await MessageDialog.ConfirmAsync(this, "Confirm", message,
                    whenUnavailable: false);
            };
            vm.PropertyChanged += OnVmPropertyChanged;
            UpdatePhaseDot(vm.CurrentPhase);

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
    /// scrolled up. So it is taken on the first call of the turn and kept. Which also settles what was
    /// previously decided four times against an extent that the first of the four had already
    /// changed.</para>
    /// </remarks>
    private void FollowTheEndSoon()
    {
        if (_scrollQueued) return;
        _scrollQueued = true;
        _scrollWanted = IsNearTheEnd();

        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (_scrollWanted) ScrollTranscriptToEnd();
        }, DispatcherPriority.Loaded);
    }

    private bool _scrollQueued;
    private bool _scrollWanted;

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
    /// <remarks>The rule itself is <see cref="TranscriptFollow"/> — pure, so it can be argued in a
    /// table test; this only reads the three numbers off the scroller, which cannot be.</remarks>
    private bool IsNearTheEnd() =>
        TranscriptFollow.ShouldFollow(
            ChatScroll.Extent.Height, ChatScroll.Viewport.Height, ChatScroll.Offset.Y);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GoalTileViewModel vm) return;

        if (e.PropertyName == nameof(GoalTileViewModel.CurrentPhase))
            UpdatePhaseDot(vm.CurrentPhase);

        if (e.PropertyName == nameof(GoalTileViewModel.ShowQuestions) && vm.ShowQuestions)
            FocusFirstAnswer();

        if (e.PropertyName == nameof(GoalTileViewModel.IsShowingFindings))
            ApplyFindingsModality(vm.IsShowingFindings);

        if (e.PropertyName is not { } name) return;

        // Everything the tile asks of the user is a block at the end of the conversation, so each of
        // these changes the length of the thing being scrolled without adding a message — and the
        // follow-to-the-bottom rule is driven by the message collection, which never hears about them.
        //
        // Followed on the ordinary terms and no others: if the reader is at the end they see the block
        // arrive, and if they are reading further up nothing moves. A block appearing used to overrule
        // that, on the reasoning that a plan waiting to be approved is worth interrupting for — but
        // being pulled away from what you are reading is the thing this rule exists to prevent, and it
        // does not become acceptable because the tile has something to say. The block is still there
        // when the reader arrives at the bottom.
        if (Showing(vm, name) is not null || FollowsTheEnd.Contains(name))
            FollowTheEndSoon();
    }

    /// <summary>
    /// What each of the four blocks is showing now, and null for a name that is not one of them.
    /// </summary>
    /// <remarks>
    /// <para>Read rather than listed, so a name here that cannot be answered does not compile — the
    /// alternative was a second set beside the first, where "in the set" and "how to read it" drift.</para>
    /// <para>Only whether the block is showing, which is all that is asked of it: what it is for is
    /// saying that this property is one of the four, so that a block arriving or leaving asks the
    /// transcript to follow on the ordinary terms. Nothing here overrules a reader's position any
    /// more.</para>
    /// <para><c>CanDetectGoal</c> and <c>IsRunning</c> are not here because they are not requests, but
    /// both are in <see cref="FollowsTheEnd"/> and reach the same call: with the overruling gone the
    /// two lists differ only in what they are called, and they are kept apart because the next thing
    /// added to either has to be read as one or the other.</para>
    /// <para>Internal so it can be stated in a test, as <see cref="TextOf"/> is.</para>
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
