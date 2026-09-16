using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.ViewModels;

namespace mTiles.Services.Tiles;

/// <summary>Web pages in tabs, reopened where they were left.</summary>
/// <remarks>The tabs are saved as a list beside the single <c>address</c> the first version wrote, which
/// is still written (the page on screen) so a build from before tabs opens the tile on that page rather
/// than on its home page.</remarks>
public sealed class BrowserTileKind : TileKind<BrowserTileViewModel>
{
    internal const string AddressKey = "address";
    internal const string TabsKey = "tabs";
    internal const string SelectedKey = "selectedTab";

    public override string Id => TileKindIds.Browser;
    public override string DisplayName => "Browser";
    public override string IconId => "web";
    public override string AccentKey => "TileAccentBrowser";

    /// <summary>Closes on the first press: nothing in it is lost that the addresses do not bring back.</summary>
    public override bool ClosesWithoutAsking => true;

    protected override BrowserTileViewModel Create(TileContext context, JsonObject? state)
    {
        var tabs = (state?[TabsKey] as JsonArray)?
            .Select(node => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
            .OfType<string>()
            .ToList() ?? [];
        if (tabs.Count == 0 && state.String(AddressKey) is { } single)
            tabs.Add(single);

        var selected = state?[SelectedKey] is JsonValue index && index.TryGetValue<int>(out var i) ? i : 0;
        return new BrowserTileViewModel(context.Settings, tabs, selected, context.RequestSave);
    }

    protected override JsonObject? Save(BrowserTileViewModel tile) => new()
    {
        [AddressKey] = tile.Address.ToString(),
        [TabsKey] = new JsonArray(tile.Tabs.Select(tab => (JsonNode?)JsonValue.Create(tab.Address.ToString())).ToArray()),
        [SelectedKey] = tile.Tabs.IndexOf(tile.SelectedTab),
    };
}
