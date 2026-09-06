using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace mTiles.Views;

/// <summary>Every finding of one review, drawn by <see cref="OverlayHost"/> like any other dialog.</summary>
/// <remarks>
/// <para>It was a hand-written <c>Panel</c> inside <c>GoalTileView</c> — its own scrim, its own card,
/// its own close button — which made it the fourth implementation of the same thing and left it
/// covering only the tile rather than the window it is modal to.</para>
/// <para>Moving it out took three things that are worth knowing, because each is what a shared
/// dialog costs: its styles had to leave the tile (styles apply down the visual tree, and the host is
/// in <c>MainWindow</c>), its finding template had to stop naming a <c>Click</c> handler
/// (<see cref="CopyButton"/>), and the tile had to stop drawing it and start asking for it.</para>
/// </remarks>
public partial class GoalFindingsDialog : UserControl
{
    public GoalFindingsDialog() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
