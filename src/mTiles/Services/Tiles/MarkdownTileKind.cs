using System.Text.Json.Nodes;

namespace mTiles.Services.Tiles;

/// <summary>What the two markdown kinds have in common on disk.</summary>
/// <remarks>
/// One key, and the reason it is shared rather than named twice: a note and a todo record the same
/// thing — where their file is — and the migration reads the old <c>NoteFilePath</c> and
/// <c>TodoFilePath</c> into it. Two spellings of one key would have made that migration kind-aware for
/// no gain.
/// </remarks>
internal static class MarkdownTileKind
{
    public const string FilePathKey = "filePath";

    /// <summary>The file a saved note or todo names, moved with the workspace's state directory.</summary>
    /// <remarks>A layout written before the rename holds an absolute path into <c>.mterminal/</c>, and
    /// <see cref="WorkspacePaths"/> moves that directory to <c>.mtiles/</c> without rewriting anybody's layout
    /// — so the tile opened on a file that was no longer there, showed an empty page, and its text sat in the
    /// other directory. The stored path is used as it is whenever it still exists.</remarks>
    public static string? StoredPath(JsonObject? state, string workingDirectory)
    {
        var stored = state.String(FilePathKey);
        if (stored is null || File.Exists(stored)) return stored;

        // Asked first, because asking is what moves the old directory into place.
        WorkspacePaths.Dir(workingDirectory);

        foreach (var separator in new[] { '\\', '/' })
        {
            var legacy = separator + WorkspacePaths.LegacyDirName + separator;
            var at = stored.LastIndexOf(legacy, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            var moved = stored[..at] + separator + WorkspacePaths.DirName + separator
                        + stored[(at + legacy.Length)..];
            if (File.Exists(moved)) return moved;
        }

        return stored;
    }
}
