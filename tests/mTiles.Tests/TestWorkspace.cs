using mTiles.Models;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>A workspace built the way the window builds one, and the tiles a test puts in it.</summary>
internal static class TestWorkspace
{
    /// <summary>A workspace over <paramref name="directory"/>, or over the settings' own directory, on the
    /// application's real tile catalog.</summary>
    public static WorkspaceViewModel Open(TempSettings settings, string? directory = null)
    {
        directory ??= settings.Directory;
        Directory.CreateDirectory(directory);
        return new WorkspaceViewModel(new Workspace { Name = "test", DirectoryPath = directory }, settings.Layouts,
            settings.Service, TestTiles.Catalog(settings.Service));
    }

    /// <summary>Gives an empty tile content of that kind through the chooser, the route a user takes —
    /// picking the default shell where the kind asks which one.</summary>
    public static void Make(LeafTileNodeViewModel leaf, string kindId)
    {
        leaf.SelectKindCommand.Execute(kindId);
        if (leaf.IsChoosingSetup)
            leaf.SelectSetupOptionCommand.Execute(
                leaf.SetupOptions.FirstOrDefault(o => o.State is null) ?? leaf.SetupOptions.First());
    }

    /// <summary>The workspace's root made <paramref name="first"/>, split down, with the second half made
    /// <paramref name="second"/> — or left empty.</summary>
    public static (SplitTileNodeViewModel Split, LeafTileNodeViewModel First, LeafTileNodeViewModel Second)
        Split(WorkspaceViewModel workspace, string first, string? second)
    {
        var root = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
        Make(root, first);
        root.SplitVerticalCommand.Execute(null);

        var split = Assert.IsType<SplitTileNodeViewModel>(workspace.RootTile);
        var secondLeaf = Assert.IsType<LeafTileNodeViewModel>(split.Second);
        if (second is not null) Make(secondLeaf, second);

        return (split, Assert.IsType<LeafTileNodeViewModel>(split.First), secondLeaf);
    }
}
