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
}
