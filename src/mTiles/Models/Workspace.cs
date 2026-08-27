namespace mTiles.Models;

public sealed class Workspace
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;

    /// <summary>Whether the user pinned this workspace to the top of the panel.</summary>
    /// <remarks>A preference about the list, not about the directory, so it lives with the rest of the
    /// list in <c>workspaces.json</c> rather than in the workspace's own layout file — a workspace that
    /// has never been opened has no layout file to hold it.</remarks>
    public bool IsFavorite { get; set; }
}
