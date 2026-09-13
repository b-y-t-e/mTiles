using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.ViewModels;

namespace mTiles.Services.Tiles;

/// <summary>Where the open workspace is drawn, as a tile of the window's layout. Permanent: there is one.</summary>
public sealed class WorkspaceHostTileKind : TileKind<WorkspaceHostTileViewModel>
{
    public override string Id => TileKindIds.WorkspaceHost;
    public override string DisplayName => "Workspace";
    public override string IconId => "workspace-host";
    public override string AccentKey => "AccentHover";
    public override bool IsPermanent => true;

    /// <summary>One of them, so no number.</summary>
    public override string NameFor(IReadOnlySet<string> used) => DisplayName;

    protected override WorkspaceHostTileViewModel Create(TileContext context, JsonObject? state) => new();
}
