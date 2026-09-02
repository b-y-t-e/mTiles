using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.ViewModels;

namespace mTiles.Services.Tiles;

/// <summary>What every account has left, and when it comes back.</summary>
/// <remarks>
/// <para><see cref="Save"/> answers nothing, and that is the whole of this tile's persistence: it holds
/// no work, no file and no conversation, so there is nothing per tile worth writing down. What it draws
/// comes from the accounts in <c>settings.json</c> and from the service that asks them.</para>
/// <para>The service is handed in rather than reached for, the same way <see cref="DatabaseTileKind"/>
/// takes its manager: there is no container here, and a kind that built its own would give every
/// workspace a second poller.</para>
/// </remarks>
public sealed class UsageTileKind(AiUsageService usage) : TileKind<UsageTileViewModel>
{
    public override string Id => TileKindIds.Usage;
    public override string DisplayName => "Usage";
    public override string IconId => "gauge";
    public override string AccentKey => "TileAccentUsage";

    protected override UsageTileViewModel Create(TileContext context, JsonObject? state) =>
        new(usage, context.OpenSettings);
}
