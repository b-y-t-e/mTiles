using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace mTiles.Views;

/// <summary>The application's own actions — a tile for the window, Settings, the phone, the update —
/// drawn once for every shape of the workspaces list, which differ only in which way the row runs and
/// how large its icons are.</summary>
public partial class WindowActionsBar : UserControl
{
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<WindowActionsBar, Orientation>(nameof(Orientation), Orientation.Horizontal);

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<WindowActionsBar, double>(nameof(IconSize), 16);

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public WindowActionsBar() => InitializeComponent();
}
