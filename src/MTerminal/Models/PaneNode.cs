using Avalonia.Layout;

namespace MTerminal.Models;

public sealed class PaneNode
{
    public bool IsLeaf { get; set; }

    public PaneContentType ContentType { get; set; }
    public string? PaneName { get; set; }
    public string? ShellName { get; set; }
    public string? EditorFilePath { get; set; }

    public Orientation SplitOrientation { get; set; } = Orientation.Vertical;
    public double SplitRatio { get; set; } = 0.5;
    public PaneNode? First { get; set; }
    public PaneNode? Second { get; set; }
}
