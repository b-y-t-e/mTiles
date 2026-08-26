using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
// Not unused, however it looks: SetTextAsync is an extension method on IClipboard living
// in this namespace, and the interface itself is never named here.
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using mTiles.Models;
using mTiles.ViewModels;

namespace mTiles.Views;

public partial class GoalTileView : UserControl
{
    private GoalTileViewModel? _subscribedVm;

    public GoalTileView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm.ScrollToEnd = null;

            // ConfirmAction too, and for more than tidiness: the closure holds this view, so a view
            // model left with it keeps the view alive — and if that view model ever asks again, the
            // dialog opens over whatever this view is showing now, which is somebody else's tile.
            _subscribedVm.ConfirmAction = null;
            _subscribedVm = null;
        }

        if (DataContext is GoalTileViewModel vm)
        {
            _subscribedVm = vm;
            vm.ScrollToEnd = () => ChatScroll.ScrollToEnd();
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

            // A tile can arrive already asking — reopened from a file whose questions were never
            // answered — and no property changes on the way in to say so.
            UpdateAskRow(vm.ShowQuestions);
        }
    }

    /// <summary>
    /// Puts one message on the clipboard.
    /// </summary>
    /// <remarks>
    /// <para>In the view rather than in the view model, because a clipboard belongs to a window: reaching
    /// one needs a <c>TopLevel</c>, and a view model that holds one holds the window open. The
    /// alternative was a command on the tile reached from inside the item template by walking up out of
    /// its <c>ItemsControl</c> — the binding that has already failed silently once in this file.</para>
    /// <para>The icon becomes a tick for a moment. A copy button that does nothing visible leaves the
    /// user pressing it again, and the second press is indistinguishable from the first not having
    /// worked.</para>
    /// </remarks>
    private async void CopyMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not GoalMessage message) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        try
        {
            await clipboard.SetTextAsync(message.Text);
        }
        catch (Exception ex)
        {
            // A clipboard can be held by another application. Not worth a dialog over a convenience.
            System.Diagnostics.Trace.TraceWarning($"Copying a message failed: {ex.Message}");
            return;
        }

        if (button.Content is not MaterialIcon icon) return;

        icon.Kind = MaterialIconKind.Check;
        await Task.Delay(CopiedFeedback);

        // Checked again: a transcript row is reused as the list changes, so by now this button may be
        // showing a different message — and it is still the same button, so it is still the tick that
        // has to come off.
        if (icon.Kind == MaterialIconKind.Check) icon.Kind = MaterialIconKind.ContentCopy;
    }

    /// <summary>How long the tick stays. Long enough to be seen, short enough that a second copy of the
    /// next message does not find the button still congratulating itself about the last one.</summary>
    private static readonly TimeSpan CopiedFeedback = TimeSpan.FromSeconds(1.1);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GoalTileViewModel vm) return;

        if (e.PropertyName == nameof(GoalTileViewModel.CurrentPhase))
            UpdatePhaseDot(vm.CurrentPhase);

        if (e.PropertyName == nameof(GoalTileViewModel.ShowQuestions))
        {
            UpdateAskRow(vm.ShowQuestions);
            if (vm.ShowQuestions) FocusFirstAnswer();
        }
    }

    /// <summary>
    /// How tall the question panel is, in pixels, until the user drags it somewhere else.
    /// </summary>
    /// <remarks>
    /// Tall enough for one question with its reasons and offered answers, which is what most rounds
    /// ask, and short enough to leave the conversation visible — the whole argument for a panel rather
    /// than a wall of text in the transcript.
    /// </remarks>
    private const double DefaultAskHeight = 230;

    /// <summary>What the row goes back to when the panel returns. It is not a setting: a height dragged
    /// for three long questions is not the right height for the next tile, and remembering it across
    /// sessions would make an answer to one round the default for every round after it.</summary>
    private double _askHeight = DefaultAskHeight;

    /// <summary>The row the question panel sits in. Taken from the end rather than by index: it is the
    /// last row by construction, and a number would go quietly wrong the day a row is added above it in
    /// the markup — at run time, in a handler, with nothing to catch it.</summary>
    private RowDefinition AskRow => AskGrid.RowDefinitions[^1];

    /// <summary>
    /// Gives the question row its height, and takes it away again.
    /// </summary>
    /// <remarks>
    /// <para>The row cannot simply be left at its dragged size: a <c>RowDefinition</c> holds a height
    /// whatever its child's visibility says, so a tile that had been asked three questions kept an
    /// empty band under the transcript for the rest of the session.</para>
    /// <para>The height is read back before it is collapsed rather than caught from a drag event,
    /// because that is true however the row got its size — a drag, a later default, or anything else
    /// that ever sets it. There is no event to miss.</para>
    /// </remarks>

    private void UpdateAskRow(bool asking)
    {
        var row = AskRow;

        // Read before anything is written, on every path. Whoever last set this row — the splitter,
        // an earlier clamp, the default — what it is showing now is the truth, and taking it only on
        // the way out meant a resize put back the height from before the user last dragged it.
        if (row.ActualHeight > 1)
            _askHeight = row.ActualHeight;

        row.Height = asking ? new GridLength(Fits(_askHeight)) : new GridLength(0);
    }

    private double Fits(double wanted) => FitsIn(wanted, AskGrid.Bounds.Height);

    /// <summary>
    /// The asked-for height, or as much of it as a tile this tall can spare.
    /// </summary>
    /// <remarks>
    /// <para>A fixed 230 was fine until the tile was smaller than that. A grid row with an absolute
    /// height takes it whether or not there is room: on a tile 200 pixels tall the transcript vanished
    /// entirely and the Send button went with it, off the bottom edge — a panel you cannot answer,
    /// covering a conversation you cannot read. Three fifths, so what the panel is for is always still
    /// visible behind it. Before the first layout there is no height to measure and the wanted value
    /// stands.</para>
    /// <para>Static and taking the height as an argument, so the rule can be stated without a window:
    /// it is arithmetic, and the thing worth pinning is the ratchet — shrinking the tile clamps the
    /// panel, and growing it again must not <em>expand</em> a panel the user never dragged, because a
    /// clamped height is what gets read back as "what the user wanted" on the next pass.</para>
    /// </remarks>
    internal static double FitsIn(double wanted, double available) =>
        available > 1 ? Math.Min(wanted, available * 0.6) : wanted;

    /// <summary>
    /// Re-clamps the panel when the tile is resized.
    /// </summary>
    /// <remarks>
    /// A height that fitted when it was dragged does not fit after the tile is halved, and a row with an
    /// absolute height does not give any of it back on its own.
    /// </remarks>
    private void AskGrid_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is GoalTileViewModel { ShowQuestions: true }) UpdateAskRow(asking: true);
    }

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
        if (DataContext is not GoalTileViewModel vm) return;

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
        if (DataContext is not GoalTileViewModel vm) return;

        // Through the command, so the "answer at least one" rule is the same one whichever way the
        // answers are sent.
        vm.SendAnswersCommand.Execute(null);
        e.Handled = true;
    }

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            if (DataContext is GoalTileViewModel vm && vm.SubmitCommand.CanExecute(null))
            {
                vm.SubmitCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
