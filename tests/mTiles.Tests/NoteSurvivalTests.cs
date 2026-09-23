using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The rules that decide whether a note or a todo list survives: its layout being written, its file being
/// found after the state directory's rename, and a tile never writing over a file it has not read.
/// </summary>
public sealed class NoteSurvivalTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static TileNode Leaf(string id) => new() { TileId = id };

    [Fact]
    public void A_save_scheduled_for_one_workspace_does_not_cancel_another_s()
    {
        var persistence = new PersistenceService(Path.Combine(_dir.Path, "layouts"));

        persistence.DebouncedSaveLayout("a", () => Leaf("tile-a"));
        persistence.DebouncedSaveLayout("b", () => Leaf("tile-b"));
        persistence.FlushLayout("a");
        persistence.FlushLayout("b");

        Assert.Equal("tile-a", persistence.LoadLayout("a")?.RootTile?.TileId);
        Assert.Equal("tile-b", persistence.LoadLayout("b")?.RootTile?.TileId);
    }

    [Fact]
    public void Flushing_a_layout_with_nothing_pending_writes_nothing()
    {
        var persistence = new PersistenceService(Path.Combine(_dir.Path, "layouts"));

        persistence.FlushLayout("a");

        Assert.Null(persistence.LoadLayout("a"));
    }

    [Fact]
    public void A_layout_is_written_whole_and_leaves_no_temporary_file()
    {
        var layouts = Path.Combine(_dir.Path, "layouts");
        var persistence = new PersistenceService(layouts);

        persistence.SaveLayout("a", Leaf("first"));
        persistence.SaveLayout("a", Leaf("second"));

        Assert.Equal("second", persistence.LoadLayout("a")?.RootTile?.TileId);
        Assert.Empty(Directory.GetFiles(layouts, "*.tmp"));
    }

    [Fact]
    public void A_path_into_the_old_state_directory_finds_the_file_where_it_was_moved()
    {
        var moved = Path.Combine(_dir.Path, WorkspacePaths.DirName, "Note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.WriteAllText(moved, "text");
        var stored = Path.Combine(_dir.Path, WorkspacePaths.LegacyDirName, "Note.md");

        var path = MarkdownTileKind.StoredPath(new JsonObject { [MarkdownTileKind.FilePathKey] = stored }, _dir.Path);

        Assert.Equal(moved, path);
    }

    [Fact]
    public void A_stored_path_that_still_exists_is_used_as_it_is()
    {
        var stored = Path.Combine(_dir.Path, "Note.md");
        File.WriteAllText(stored, "text");

        var path = MarkdownTileKind.StoredPath(new JsonObject { [MarkdownTileKind.FilePathKey] = stored }, _dir.Path);

        Assert.Equal(stored, path);
    }

    [Fact]
    public void A_tile_closed_untouched_leaves_its_file_alone()
    {
        var file = Path.Combine(_dir.Path, "Note.md");
        File.WriteAllText(file, "somebody's text");
        var stamp = File.GetLastWriteTimeUtc(file);

        new NoteTileViewModel(file).Dispose();

        Assert.Equal("somebody's text", File.ReadAllText(file));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void Renaming_onto_a_name_whose_file_exists_takes_a_free_name_and_leaves_that_file_alone()
    {
        var taken = Path.Combine(_dir.Path, "Note#2.md");
        File.WriteAllText(taken, "somebody's note");
        var tile = new NoteTileViewModel(Path.Combine(_dir.Path, "Note#1.md"));

        tile.RenameFile("Note#2");
        var renamedTo = tile.FilePath;
        tile.Dispose();
        if (File.Exists(renamedTo)) File.Delete(renamedTo);

        Assert.Equal(Path.Combine(_dir.Path, "Note#2 (2).md"), renamedTo);
        Assert.Equal("somebody's note", File.ReadAllText(taken));
    }

    [Fact]
    public void Text_typed_over_a_file_that_could_not_be_read_is_kept_beside_it()
    {
        var path = Path.Combine(_dir.Path, "Note.md");
        File.WriteAllText(path, "somebody's note");

        NoteTileViewModel tile;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            tile = new NoteTileViewModel(path);
            tile.MdText = "typed meanwhile";
        }

        // Disposed once the file is writable again: what stops the overwrite is the tile, not the lock.
        tile.Dispose();

        Assert.Equal("somebody's note", File.ReadAllText(path));
        Assert.Equal("typed meanwhile", File.ReadAllText(MarkdownTileViewModel.RecoveryPathFor(path)));
    }
}
