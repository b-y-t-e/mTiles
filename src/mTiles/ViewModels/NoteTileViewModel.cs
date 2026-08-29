using mTiles.Models;
using mTiles.Services;

namespace mTiles.ViewModels;

/// <summary>
/// A page of markdown the user keeps beside their work.
/// </summary>
/// <remarks>
/// <para>It used to be a bodyless subclass of <see cref="MarkdownTileViewModel"/>, distinguishable from
/// <see cref="TodoTileViewModel"/> by nothing but its CLR type — which is what a view's type switch
/// needed in order to pick between two views, and nothing else. Now it says what it is
/// (<see cref="KindId"/>) and where its files go, which is what the two have always differed by.</para>
/// <para>The file format is unchanged and so are the folders: a note written by an earlier version opens
/// as the same note.</para>
/// </remarks>
public sealed class NoteTileViewModel(string filePath, SettingsService? settingsService = null)
    : MarkdownTileViewModel(filePath, settingsService)
{
    /// <summary>The workspace folder notes live in.</summary>
    private const string FolderName = "notes";

    public override string KindId => TileKindIds.Note;

    /// <summary>Where a note the user has just added is created.</summary>
    /// <remarks>A GUID rather than the tile's name: the tile is named after this file is made, and
    /// renaming the tile renames the file — see <see cref="MarkdownTileViewModel.RenameFile"/>.</remarks>
    public static string NewFilePath(string workingDirectory) =>
        Path.Combine(WorkspacePaths.Combine(workingDirectory, FolderName), $"{Guid.NewGuid():N}.md");
}
