using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.ViewModels;

namespace mTiles.Services.Tiles;

/// <summary>The list of workspaces, as a tile of the window's layout. Permanent: there is one.</summary>
/// <param name="panel">The window's list, asked for when the tile is built rather than handed over at
/// registration — the catalog is built before the window's view model that owns the list.</param>
public sealed class WorkspacesTileKind(Func<WorkspacesPanelViewModel> panel) : TileKind<WorkspacesTileViewModel>
{
    /// <summary>How wide the list stands beside the layout when nothing says otherwise.</summary>
    /// <remarks>The width the panel had before it was a tile, and what <c>AppSettings</c> still starts
    /// from.</remarks>
    public const double DefaultWidth = 240;

    /// <summary>How tall the list is when it lies along the top or the bottom of the window.</summary>
    /// <remarks>One row of tabs: a list laid along the window is a strip to switch by, not a page to
    /// read, and a share of the window's height would give it a third of the screen.</remarks>
    public const double StripHeight = 40;

    public override string Id => TileKindIds.Workspaces;
    public override string DisplayName => "Workspaces";
    public override string IconId => "workspaces";
    public override string AccentKey => "AccentHover";
    public override bool IsPermanent => true;

    /// <summary>One of them, so no number.</summary>
    public override string NameFor(IReadOnlySet<string> used) => DisplayName;

    protected override WorkspacesTileViewModel Create(TileContext context, JsonObject? state) => new(panel());
}
