using Avalonia;
using Avalonia.Controls;

namespace mTiles.Views;

/// <summary>The hint a tile draws over itself while something from the system is held above it.</summary>
/// <remarks>One definition for every tile that takes a drop, so the terminal and the Agent tile cannot
/// drift apart; each says only what the drop will do.</remarks>
public partial class FileDropHint : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<FileDropHint, string?>(nameof(Text));

    /// <summary>What letting go will do, in a few words.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public FileDropHint() => InitializeComponent();
}
