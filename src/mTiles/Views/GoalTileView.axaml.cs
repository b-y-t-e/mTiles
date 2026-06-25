using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
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
            _subscribedVm = null;
        }

        if (DataContext is GoalTileViewModel vm)
        {
            _subscribedVm = vm;
            vm.ScrollToEnd = () => ChatScroll.ScrollToEnd();
            vm.ConfirmAction = async message =>
            {
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null) return true;
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

    private void UpdatePhaseDot(GoalPhase phase)
    {
        var resourceKey = phase switch
        {
            GoalPhase.Clarify => "GoalPhaseClarify",
            GoalPhase.Plan => "GoalPhasePlan",
            GoalPhase.Implement => "GoalPhaseImplement",
            GoalPhase.Review => "GoalPhaseReview",
            GoalPhase.Summary => "GoalPhaseSummary",
            _ => "TextMuted"
        };

        if (this.TryFindResource(resourceKey, out var brush) && brush is IBrush b)
            PhaseDot.Fill = b;
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
