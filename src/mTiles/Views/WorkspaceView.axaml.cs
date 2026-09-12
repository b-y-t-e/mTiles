using Avalonia.Controls;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>The workspace's canvas: its tile tree, laid on the surface that answers its drops.</summary>
public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();

        // The workspace says what its root is, rather than the surface reading it back off the tree
        // view's binding: an edge drop reads the root again straight after its own detach, and the view
        // model is where that answer is true first.
        DropSurface.ReadRoot = () => (DataContext as WorkspaceViewModel)?.RootTile;
    }
}
