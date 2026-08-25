using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GoalTileViewModel.CurrentPhase) && sender is GoalTileViewModel vm)
            UpdatePhaseDot(vm.CurrentPhase);
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
