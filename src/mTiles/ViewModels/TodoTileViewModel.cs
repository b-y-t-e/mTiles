using mTiles.Models;
using mTiles.Services;

namespace mTiles.ViewModels;

/// <summary>
/// A checklist. Markdown underneath, with its own folder and its own view.
/// </summary>
/// <remarks>
/// See <see cref="NoteTileViewModel"/> for why this is a class of its own rather than a bodyless
/// subclass: the two differ by what they are called and where their files go, and until now neither of
/// those facts was written down anywhere but in a switch statement.
/// </remarks>
public sealed class TodoTileViewModel(string filePath, SettingsService? settingsService = null)
    : MarkdownTileViewModel(filePath, settingsService)
{
    /// <summary>The workspace folder todo lists live in.</summary>
    private const string FolderName = "todos";

    public override string KindId => TileKindIds.Todo;

    /// <summary>Where a todo list the user has just added is created.</summary>
    public static string NewFilePath(string workingDirectory) =>
        Path.Combine(WorkspacePaths.Combine(workingDirectory, FolderName), $"{Guid.NewGuid():N}.md");
}
