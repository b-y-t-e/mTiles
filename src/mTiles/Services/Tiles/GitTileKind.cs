using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.ViewModels;

namespace mTiles.Services.Tiles;

/// <summary>The workspace's repository: changes, diff, history.</summary>
public sealed class GitTileKind : TileKind<GitTileViewModel>
{
    /// <summary>Whether the diff sits beside the list of changes. The one thing this tile remembers,
    /// and it has been under this key since before kinds existed.</summary>
    public const string ShowDiffPanelKey = "showDiffPanel";

    public override string Id => TileKindIds.Git;
    public override string DisplayName => "Git";
    public override string IconId => "source-branch";
    public override string AccentKey => "TileAccentGit";

    protected override GitTileViewModel Create(TileContext context, JsonObject? state)
    {
        var tile = new GitTileViewModel(context.WorkingDirectory, context.Settings)
        {
            ShowDiffPanel = state.Bool(ShowDiffPanelKey, fallback: true)
        };

        // After the value, not before: assigning it is what raises the change the callback answers, and
        // a restore that told the workspace its layout had changed would schedule a save of a layout
        // nobody had touched.
        tile.TileSettingsChanged = context.RequestSave;
        return tile;
    }

    /// <summary>Only when it is off. The default costs nothing to leave unwritten and keeps the file
    /// free of a line per tile saying what every tile already does.</summary>
    protected override JsonObject? Save(GitTileViewModel tile) =>
        tile.ShowDiffPanel ? null : new JsonObject { [ShowDiffPanelKey] = false };
}
