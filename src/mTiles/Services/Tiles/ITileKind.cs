using System.Text.Json.Nodes;
using mTiles.ViewModels;

namespace mTiles.Services.Tiles;

/// <summary>
/// One kind of tile: what it is called, what it looks like, how it is built and how it is saved.
/// </summary>
/// <remarks>
/// <para>An object rather than a value in a closed enum, because the enum was switched on in about
/// thirteen places and every seventh kind meant finding all of them. Adding one is now a class and a
/// line of registration.</para>
/// <para><b>Saving belongs here rather than on the tile.</b> Restoring has to: a goal tile takes its
/// file path in the constructor and a terminal needs its shell resolved before it starts, so
/// <see cref="Create"/> could never be the tile's own method. Putting <see cref="Save"/> anywhere else
/// would split one JSON shape across two classes. Both here keeps the format for one kind in one
/// testable place, and keeps <c>System.Text.Json</c> out of the view models entirely — they expose
/// ordinary properties and this reads them.</para>
/// </remarks>
public interface ITileKind
{
    /// <summary>The id that goes into the layout JSON. Lower case, stable, never shown to a user.</summary>
    string Id { get; }

    /// <summary>What the chooser and the tile header call it.</summary>
    string DisplayName { get; }

    /// <summary>An icon name, resolved to a drawing on the view side.</summary>
    string IconId { get; }

    /// <summary>The name of the resource this kind's glyph is drawn in — a key rather than a brush, so
    /// a theme switch reaches it and the accents stay free to become derived later.</summary>
    string AccentKey { get; }

    /// <summary>The prefix a new tile of this kind is named after, as in <c>Git#1</c>.</summary>
    string NamePrefix { get; }

    /// <summary>
    /// What to call a new tile of this kind, given the names this workspace has already used for that
    /// kind.
    /// </summary>
    /// <remarks>Here rather than in the workspace, which used to ask <c>kindId == Terminal</c> before
    /// naming anything: how a kind is named is a fact about the kind, and a seventh one that wants
    /// generated names instead of numbered ones is a class of its own rather than an edit to a view
    /// model. The default numbers; the terminal is the one that does not.</remarks>
    /// <param name="used">Every name this workspace has already given a tile of this kind, saved
    /// layout included. Names are never taken out of it, so a number is not handed out twice in one
    /// session because a tile was closed.</param>
    string NameFor(IReadOnlySet<string> used);

    /// <summary>
    /// What an empty tile must ask before this kind can be built. Empty when it can be built straight
    /// away, which is every kind but the terminal.
    /// </summary>
    /// <remarks>The step the empty tile draws is a row of cards and nothing else, so a kind describes
    /// it as a list of options rather than by owning a view: whichever one is picked becomes the
    /// <see cref="Create"/> state, which is the same route the chooser and a saved layout already
    /// share.</remarks>
    IReadOnlyList<TileSetupOption> SetupOptions(TileContext context);

    /// <summary>
    /// Builds a tile, from saved state or from nothing.
    /// </summary>
    /// <remarks>
    /// One way in, where there used to be three. Creating a fresh tile, creating one from a profile
    /// chosen in the chooser and restoring one from disk are the same act seen three times — choosing a
    /// profile <em>is</em> handing a new tile its initial state — and two branches that must produce
    /// identical results, with nothing checking that they do, is how they drift.
    /// </remarks>
    /// <param name="state">Null for a fresh tile.</param>
    ITile Create(TileContext context, JsonObject? state);

    /// <summary>What this tile needs written down in order to come back as itself. Null when nothing
    /// does.</summary>
    JsonObject? Save(ITile tile);
}
