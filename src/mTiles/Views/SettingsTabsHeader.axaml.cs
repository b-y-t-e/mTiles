using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace mTiles.Views;

/// <summary>The settings dialog's tabs, for <see cref="OverlayHost.IOverlayHeader"/>.</summary>
public partial class SettingsTabsHeader : UserControl
{
    public SettingsTabsHeader() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
