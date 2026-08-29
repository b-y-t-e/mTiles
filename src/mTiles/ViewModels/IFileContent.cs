namespace mTiles.ViewModels;

/// <summary>Tile content that owns a file, so the file follows the tile's name.</summary>
public interface IFileContent : ITile
{
    void RenameFile(string newName);

    static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Where(c => !invalid.Contains(c))).Trim();
    }
}
