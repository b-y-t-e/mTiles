namespace MTerminal.Models;

public sealed class WorkspaceState
{
    public string WorkspaceId { get; set; } = string.Empty;
    public TileNode? RootTile { get; set; }

    // Backward compat: old files use "RootPane" — read-only, never written back
    public TileNode? RootPane { get => null; set { RootTile ??= value; } }
}
