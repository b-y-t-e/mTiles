using Avalonia.Platform.Storage;
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
using mTiles.Controls;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;

namespace mTiles.Views;

public partial class GoalTileView : UserControl, IFocusTargetView
{
    /// <summary>The composer, docked to the foot of the tile - the one place a goal is acted on from.
    /// </summary>
    public InputElement? PreferredFocusTarget => InputBox;

    private GoalTileViewModel? _subscribedVm;

    /// <summary>What keeps the reader in place — and what a send asks for the end of.</summary>
    private readonly TranscriptAnchor _anchor;

    public GoalTileView()
    {
        InitializeComponent();
        _anchor = TranscriptAnchor.Attach(ChatScroll);
        JumpToBottom.Attach(ChatScroll, _anchor, this);
        TranscriptPaging.Attach(Transcript, ChatScroll, _anchor, dc => (dc as GoalTileViewModel)?.Messages);
        TeachThePickers();

        // One line for the strip, giving up words before width in the order GoalStripLayout writes
        // down - the Agent tile's strip does the same with its own. Nothing keeps the fitter but the
        // handlers it hangs on these controls, which is exactly as long as it is needed.
        new RowFitter(StripRow, GoalStripLayout.Steps,
            ExecutionAgentPicker, PermissionModePicker, EffortPicker);
        // The status bar under the composer has its own order - see GoalStatusBarLayout.
        new RowFitter(StatusRow, GoalStatusBarLayout.Steps, StatusView, Badges)
            .Watch(StatusView, StripStatus.TextProperty)
            .Watch(Badges, BoundsProperty);

        // The keys and gestures every conversation's composer answers to — see ComposerInput.
        ComposerInput.Attach(InputBox, SendFromComposer, () => IsPickingAFile, Composer, AttachImage,
            AttachFilesAsync, PasteLongTextAsync);
        // The plan field is the same text in a second box - both are bound to InputText and to its caret -
        // so a long paste is folded there too: it is the one place a review's worth of notes gets pasted.
        ComposerInput.Attach(PlanBox, () => (DataContext as GoalTileViewModel)?.ApproveOrChangeCommand.Execute(null),
            () => IsPickingAFile, pasteLongText: PasteLongTextAsync);
        ComposerHistoryInput.Attach(InputBox,
            () => (DataContext as GoalTileViewModel)?.SentFromComposer ?? [],
            () => IsPickingAFile, HistoryPicker);

        // Anywhere on the tile, as on the Agent tile — attached to the composer, never sent.
        ImageDrop.Attach(this, DropHint,
            items => DataContext is GoalTileViewModel && (items.HasPicture || items.Files.Count > 0),
            AttachDroppedAsync);
    }

    private async Task AttachDroppedAsync(DroppedItems items)
    {
        if (items.Bitmap is { } dropped)
            using (dropped) AttachImage(dropped);
        await AttachFilesAsync(items.Files);
    }

    private async void AttachButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        await AttachFilesAsync(await ComposerImages.PickAsync(storage));
        InputBox.Focus();
    }

    /// <summary>A paste too long for the composer, folded into a note — see <c>PastedNote</c>.</summary>
    private Task PasteLongTextAsync(string text) =>
        DataContext is GoalTileViewModel vm ? vm.AttachPastedTextAsync(text) : Task.CompletedTask;

    /// <summary>Attaches every file in the order given — the Agent tile's rule, see
    /// <see cref="ComposerImages.AttachAllAsync"/>.</summary>
    private Task AttachFilesAsync(IEnumerable<Avalonia.Platform.Storage.IStorageItem> files) =>
        ComposerImages.AttachAllAsync(files,
            (picture, _) => AttachImage(picture),
            path => DataContext is GoalTileViewModel vm ? vm.AttachFileAsync(path) : Task.CompletedTask);

    /// <summary>Teaches this tile's five pickers how to read its own lists, and where a pick goes.</summary>
    /// <remarks>The rows are the view model's lists as they stand — agent choices and the words of the
    /// two scales — so nothing is kept in step. The mode row carries the vocabulary's own sentence, the
    /// one the Agent tile's composer shows, so the two tiles explain a mode in the same words; the
    /// effort row carries the three levels its one word stands for, which is what lets the strip offer
    /// an effort per role from a single control. A pick is written through the view model's own
    /// setters, which is where bypass is asked about.</remarks>
    private void TeachThePickers()
    {
        ExecutionAgentPicker.OptionSelector = item => item is GoalAgentChoice agent
            ? new PickerOption { Id = agent.InstanceId, Title = agent.Label, Detail = agent.Agent.DisplayName }
            : null;
        ReviewAgentPicker.OptionSelector = item => item is GoalAgentSlotChoice reviewer
            ? new PickerOption { Id = reviewer.InstanceId, Title = reviewer.Label }
            : null;
        PermissionModePicker.OptionSelector = item => item is string label ? SettingPickerRows.Mode(label) : null;
        GateModePicker.OptionSelector = item => item is string label ? SettingPickerRows.GateMode(label) : null;
        TestTimingPicker.OptionSelector = item => item is string label ? SettingPickerRows.TestTiming(label) : null;
        EffortPicker.OptionSelector = item => item is string label ? SettingPickerRows.EffortPreset(label) : null;
        PlanningAgentPicker.OptionSelector = item => item is GoalAgentSlotChoice planner
            ? new PickerOption { Id = planner.InstanceId, Title = planner.Label }
            : null;

        ExecutionAgentPicker.SelectionRequested += (_, e) => WithVm(vm => vm.ExecutionAgentInstanceId = e.Option.Id);
        ReviewAgentPicker.SelectionRequested += (_, e) => WithVm(vm => vm.ReviewAgentInstanceId = e.Option.Id);
        PermissionModePicker.SelectionRequested += (_, e) => WithVm(vm => vm.PermissionModeLabel = e.Option.Id);
        GateModePicker.SelectionRequested += (_, e) => WithVm(vm => vm.GateModeLabel = e.Option.Id);
        TestTimingPicker.SelectionRequested += (_, e) => WithVm(vm => vm.Criteria.TestTimingLabel = e.Option.Id);
        EffortPicker.SelectionRequested += (_, e) => WithVm(vm => vm.EffortPresetLabel = e.Option.Id);
        PlanningAgentPicker.SelectionRequested += (_, e) => WithVm(vm => vm.PlanningAgentInstanceId = e.Option.Id);
    }

    private void WithVm(Action<GoalTileViewModel> apply)
    {
        if (DataContext is GoalTileViewModel vm) apply(vm);
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
            _subscribedVm.SentByUser -= GoToEnd;

            // ConfirmAction too, and for more than tidiness: the closure holds this view, so a view
            // model left with it keeps the view alive — and if that view model ever asks again, the
            // dialog opens over whatever this view is showing now, which is somebody else's tile.
            _subscribedVm.ConfirmAction = null;
            _subscribedVm = null;
        }

        if (DataContext is GoalTileViewModel vm)
        {
            _subscribedVm = vm;
            vm.SentByUser += GoToEnd;

            // The transcript is read off disk in this view model's own constructor, long before any
            // view is bound to it, so there is no event to wait for: what is on screen at this moment
            // is a whole run that somebody has just opened, and its end is what they came for.
            GoToEnd();

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

        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GoalTileViewModel vm) return;

        if (e.PropertyName == nameof(GoalTileViewModel.ShowQuestions) && vm.ShowQuestions)
            FocusFirstAnswer();

        if (e.PropertyName == nameof(GoalTileViewModel.IsShowingFindings))
            ApplyFindingsModality(vm.IsShowingFindings);
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
    /// Puts the panel's number fields back to what the tile is really using, once the user has left one.
    /// <para>These are text boxes bound to integers, and Avalonia surfaces a failed conversion as a
    /// binding error rather than as data validation — so the property is simply never set, the
    /// <c>:error</c> pseudo-class never fires, and "50x" sits in the box looking like a setting. This
    /// makes it go away at the moment the user stops typing, which is late enough not to fight anyone
    /// entering "10" one digit at a time.</para>
    /// </summary>
    private void NumberBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GoalTileViewModel vm)
            vm.RefreshNumberFields();
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

    /// <summary>Takes the reader to the end because they have just sent something.</summary>
    private void GoToEnd() => _anchor.GoToEnd();

    /// <summary>Enter in the composer.</summary>
    /// <remarks>
    /// Only with something typed, and that is the whole rule: Enter is what sends what is in the box, and
    /// on an empty box it has always been a no-op. Wired straight to the primary segment it stopped being
    /// one — an empty box beside uncommitted changes reads as "Detect goal", so a stray Enter on a fresh
    /// tile started a paid run (the tile has nothing to discard, so the confirmation lets it through in
    /// silence) that nobody asked for. Detection is a click, not a keystroke; the primary command still
    /// dispatches for both, so a typed goal goes the one way its label says.
    /// </remarks>
    private void SendFromComposer()
    {
        if (DataContext is GoalTileViewModel { HasTypedGoal: true } vm &&
            vm.PrimaryActionCommand.CanExecute(null))
            vm.PrimaryActionCommand.Execute(null);
    }

    /// <summary>Hands a pasted image to the tile, as PNG bytes.</summary>
    /// <remarks>Encoded here rather than in the view model: what Avalonia hands back is a decoded bitmap,
    /// and turning one into bytes needs the imaging stack. The view model is given something it can be
    /// handed by a test.</remarks>
    private void AttachImage(Avalonia.Media.Imaging.Bitmap bitmap)
    {
        if (DataContext is not GoalTileViewModel vm) return;
        using var png = new MemoryStream();
        bitmap.Save(png);
        vm.AttachImageCommand.Execute(png.ToArray());
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
