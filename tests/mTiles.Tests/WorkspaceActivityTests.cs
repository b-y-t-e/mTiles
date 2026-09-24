using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The two things the workspaces panel gained: a light that says something is working in there, and a
/// star that pins a workspace to the top.
/// </summary>
public class WorkspaceActivityTests
{
    /// <summary>A closed tile is not working, whatever it was doing a moment before: a tile taken out
    /// of a workspace that is not its root changes nothing else the panel listens to, so the last thing
    /// it says has to be the truth.</summary>
    [Fact]
    public void A_closed_tile_stops_saying_it_is_working()
    {
        var busy = new AlwaysBusyContent();
        var tile = new LeafTileNodeViewModel(TileKindIds.Note, busy, "", new TileActivationScope());
        Assert.True(tile.IsBusy);

        var announced = false;
        tile.PropertyChanged += (_, e) => announced |= e.PropertyName == nameof(LeafTileNodeViewModel.IsBusy);

        tile.Dispose();

        Assert.False(tile.IsBusy);
        Assert.True(announced);
    }

    /// <summary>The star writes the row's own value and only then tells the store — so a row nobody
    /// wired a store to still flips, and the store still writes when the value it is handed is one the
    /// row has already set on the very same workspace.</summary>
    [Fact]
    public void The_star_flips_the_row_whether_or_not_anything_stores_it()
    {
        var unwired = new WorkspaceItemViewModel(new Workspace { Name = "Alpha" });
        unwired.IsFavorite = true;
        Assert.True(unwired.IsFavorite);

        using var directory = new TempDirectory();
        var path = directory["workspaces.json"];
        var service = new WorkspaceService(path);
        var workspace = service.AddWorkspace(Path.GetTempPath(), "Beta");
        var row = new WorkspaceItemViewModel(workspace)
        {
            FavoriteChanged = (item, value) => service.SetFavorite(item.Id, value)
        };

        row.IsFavorite = true;

        // Stored where the rest of the list is, and read back with it: a pin that did not survive the
        // application closing would be worth nothing.
        Assert.True(new WorkspaceService(path).Workspaces.Single(w => w.Id == workspace.Id).IsFavorite);
    }

    /// <summary>Pinned rows go to the top — the rest of the rule, pinning outranking the name, is in
    /// <c>DefaultWorkspaceTests</c> — and are still a list, read by name among themselves.</summary>
    [Fact]
    public void Favourites_are_ordered_among_themselves_by_name()
    {
        var rows = new List<WorkspaceItemViewModel>
        {
            Row("Zulu", isFavorite: true), Row("Alpha"), Row("Delta", isFavorite: true)
        };

        rows.Sort(WorkspaceDisplayOrder.Compare);

        Assert.Equal(["Delta", "Zulu", "Alpha"], rows.Select(r => r.Name));
    }

    private static WorkspaceItemViewModel Row(string name, bool isFavorite = false) =>
        new(new Workspace { Name = name, DirectoryPath = name, IsFavorite = isFavorite });

    /// <summary>Content that is working and never stops — enough to prove what the tile says about it.
    /// </summary>
    private sealed class AlwaysBusyContent : CommunityToolkit.Mvvm.ComponentModel.ObservableObject, IBusyTile
    {
        public string KindId => TileKindIds.Note;
        public TileActivity Activity => TileActivity.Working;
        public void Dispose() { }
    }
}
