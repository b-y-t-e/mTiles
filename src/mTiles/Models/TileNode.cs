using Avalonia.Layout;

namespace mTiles.Models;

public sealed class TileNode
{
    public bool IsLeaf { get; set; }

    public TileContentType ContentType { get; set; }
    public string? TileId { get; set; }
    public string? TileName { get; set; }
    public string? ShellName { get; set; }
    public string? UserProfileId { get; set; }
    public string? NoteFilePath { get; set; }
    public string? TodoFilePath { get; set; }
    public string? GoalFilePath { get; set; }
    public bool IsActive { get; set; }

    public Dictionary<string, object?>? Settings { get; set; }

    public Orientation SplitOrientation { get; set; } = Orientation.Vertical;
    public double SplitRatio { get; set; } = 0.5;
    public TileNode? First { get; set; }
    public TileNode? Second { get; set; }
}
