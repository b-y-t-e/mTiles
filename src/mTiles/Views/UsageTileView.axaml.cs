using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>
/// The usage dashboard.
/// </summary>
/// <remarks>
/// <para>Everything it draws is a binding and everything it can do is a command on the view model: it
/// wires no confirmation, holds no clipboard and reaches for no window.</para>
/// <para><b>The one thing it decides for itself is which way round it is.</b> That is a fact about the
/// room this control was given, which nothing above it knows and no view model should be told — so the
/// width is read here, the shape it implies is <see cref="UsageLayout"/>'s to say, and the markup binds
/// to the answer. Where each item then lands is <see cref="UsageWindowsPanel"/>'s.</para>
/// </remarks>
public partial class UsageTileView : UserControl
{
    /// <summary>Whether the cards stack rather than laying their line out across the tile.</summary>
    public static readonly StyledProperty<bool> IsVerticalLayoutProperty =
        AvaloniaProperty.Register<UsageTileView, bool>(nameof(IsVerticalLayout));

    public bool IsVerticalLayout
    {
        get => GetValue(IsVerticalLayoutProperty);
        set => SetValue(IsVerticalLayoutProperty, value);
    }

    public UsageTileView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Subscribe();
        Subscribe();
    }

    /// <summary>The margins the cards are drawn inside — the tile's width is not the room they get.</summary>
    /// <remarks>Kept in step with the <c>ItemsControl</c>'s own <c>Margin</c> in the markup, whose right
    /// side is the overlaying scrollbar's width rather than a gutter anybody chose.</remarks>
    private const double CardMargins = 8 + 14;

    private INotifyPropertyChanged? _watched;

    private void Subscribe()
    {
        if (_watched is not null) _watched.PropertyChanged -= OnViewModelChanged;

        _watched = DataContext as UsageTileViewModel;
        if (_watched is not null) _watched.PropertyChanged += OnViewModelChanged;

        Apply();
    }

    /// <summary>Both halves of the question move: the width here, the accounts on the view model.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UsageTileViewModel.WindowsPerAccount)
            or nameof(UsageTileViewModel.HasBarWindows)) Apply();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) Apply();
    }

    private void Apply()
    {
        if (DataContext is not UsageTileViewModel tile) return;

        IsVerticalLayout = UsageLayout.IsVerticalFor(Bounds.Width - CardMargins,
            tile.WindowsPerAccount, tile.HasBarWindows);
    }
}
