using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace mTiles.Views;

/// <summary>The files a composer's text names — see <see cref="mTiles.ViewModels.ComposerFile"/>.</summary>
public partial class ComposerFileChips : UserControl
{
    public static readonly StyledProperty<IEnumerable?> FilesProperty =
        AvaloniaProperty.Register<ComposerFileChips, IEnumerable?>(nameof(Files));

    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<ComposerFileChips, ICommand?>(nameof(RemoveCommand));

    public IEnumerable? Files
    {
        get => GetValue(FilesProperty);
        set => SetValue(FilesProperty, value);
    }

    public ICommand? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    public ComposerFileChips() => InitializeComponent();
}
