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
    /// <summary>The whole point of the window: output arrives in bursts many times a second, and a light
    /// wired straight to it would be a flicker rather than an answer.</summary>
    [Fact]
    public void Activity_outlasts_the_moment_it_was_seen()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var window = new ActivityWindow(TimeSpan.FromSeconds(2));

        Assert.False(window.IsActive(start));      // nothing has happened yet

        window.Stamp(start);
        Assert.True(window.IsActive(start));
        Assert.True(window.IsActive(start.AddSeconds(1.9)));
        Assert.False(window.IsActive(start.AddSeconds(2)));
    }

    /// <summary>And it has to be extendable, or a command printing steadily for a minute would go dark
    /// two seconds in.</summary>
    [Fact]
    public void A_further_sign_of_work_extends_the_window()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var window = new ActivityWindow(TimeSpan.FromSeconds(2));

        window.Stamp(start);
        window.Stamp(start.AddSeconds(1.5));

        Assert.True(window.IsActive(start.AddSeconds(3)));
        Assert.False(window.IsActive(start.AddSeconds(3.5)));
    }

    /// <summary>A closed tile is not working, whatever it was doing a moment before: a tile taken out
    /// of a workspace that is not its root changes nothing else the panel listens to, so the last thing
    /// it says has to be the truth.</summary>
    [Fact]
    public void A_closed_tile_stops_saying_it_is_working()
    {
        var busy = new AlwaysBusyContent();
        var tile = new LeafTileNodeViewModel(TileContentType.Note, busy, "", new TileActivationScope());
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

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "workspaces.json");
        try
        {
            var service = new WorkspaceService(path);
            var workspace = service.AddWorkspace(Path.GetTempPath(), "Beta");
            var row = new WorkspaceItemViewModel(workspace)
            {
                FavoriteChanged = (item, value) => service.SetFavorite(item.Id, value)
            };

            row.IsFavorite = true;

            Assert.True(new WorkspaceService(path).Workspaces.Single(w => w.Id == workspace.Id).IsFavorite);
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A pin is worth nothing if it does not survive the application closing, so it is stored
    /// where the rest of the list is and read back with it.</summary>
    [Fact]
    public void A_favourite_survives_a_reload()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "workspaces.json");
        try
        {
            var service = new WorkspaceService(path);
            var workspace = service.AddWorkspace(Path.GetTempPath(), "Alpha");

            service.SetFavorite(workspace.Id, true);

            var reloaded = new WorkspaceService(path);
            Assert.True(reloaded.Workspaces.Single(w => w.Id == workspace.Id).IsFavorite);
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Pinning is only half of it: the list has to move the row, or the star claims an order
    /// the panel does not show.</summary>
    [Fact]
    public void A_pinned_workspace_moves_to_the_top()
    {
        var rows = new List<WorkspaceItemViewModel>
        {
            Row("Alpha"), Row("Bravo"), Row("Charlie", isFavorite: true)
        };

        rows.Sort(WorkspaceDisplayOrder.Compare);

        Assert.Equal(["Charlie", "Alpha", "Bravo"], rows.Select(r => r.Name));
    }

    /// <summary>Pinned rows are still a list, and a list read by name is the only one nobody has to
    /// learn.</summary>
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
        public bool IsBusy => true;
    }
}
