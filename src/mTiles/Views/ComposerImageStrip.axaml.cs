using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace mTiles.Views;

/// <summary>The images a composer's text names by their markers — see <see cref="mTiles.Models.INumberedImage"/>.</summary>
public partial class ComposerImageStrip : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ImagesProperty =
        AvaloniaProperty.Register<ComposerImageStrip, IEnumerable?>(nameof(Images));

    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<ComposerImageStrip, ICommand?>(nameof(RemoveCommand));

    public IEnumerable? Images
    {
        get => GetValue(ImagesProperty);
        set => SetValue(ImagesProperty, value);
    }

    public ICommand? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    public ComposerImageStrip() => InitializeComponent();
}
