using Avalonia.Controls;

namespace mTiles.Views;

/// <summary>
/// The usage dashboard.
/// </summary>
/// <remarks>No code behind it beyond loading the markup, and that is the point of a read-only tile: it
/// wires no confirmation, holds no clipboard and reaches for no window — everything it draws is a
/// binding, and everything it can do is a command on the view model.</remarks>
public partial class UsageTileView : UserControl
{
    public UsageTileView() => InitializeComponent();
}
