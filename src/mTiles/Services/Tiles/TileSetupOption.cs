using System.Text.Json.Nodes;

namespace mTiles.Services.Tiles;

/// <summary>
/// One choice an empty tile offers before a kind of its own can be built.
/// </summary>
/// <param name="Label">What the card says.</param>
/// <param name="IconId">An icon name, resolved to a drawing on the view side exactly as
/// <see cref="ITileKind.IconId"/> is.</param>
/// <param name="AccentKey">The resource this option's glyph is drawn in.</param>
/// <param name="State">The initial state the tile is built from — the same object a saved layout hands
/// <see cref="ITileKind.Create"/>, because choosing an option <em>is</em> handing a new tile its state.
/// Null for the option that means "however this kind starts by default".</param>
public sealed record TileSetupOption(string Label, string IconId, string AccentKey, JsonObject? State);
