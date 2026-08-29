namespace mTiles.Models;

/// <summary>
/// The ids the kinds shipped with this application are registered under, and the one rule that reads
/// the ids written by every version before them.
/// </summary>
/// <remarks>
/// <para>Constants rather than an enum: the point of the change they came from is that a kind is an
/// object in a registry and not a member of a closed set. These exist so that the six the application
/// itself knows about — the seeded first-run layout, a migration, a test — can be named without a string
/// literal per site. A kind somebody adds later has an id and no constant here, and nothing in this file
/// needs to learn about it.</para>
/// <para>In <c>Models</c> because this is the vocabulary of the layout file, which is what
/// <see cref="TileNode"/> is.</para>
/// </remarks>
public static class TileKindIds
{
    public const string Terminal = "terminal";
    public const string Note = "note";
    public const string Todo = "todo";
    public const string Git = "git";
    public const string Database = "database";
    public const string Goal = "goal";

    /// <summary>A tile that has not been given content yet.</summary>
    /// <remarks>
    /// <b>Empty is not a kind.</b> It is the absence of one, and it stays that way: the chooser and its
    /// placeholder glyph are what the view draws for an empty id. Registering a pseudo-kind for
    /// "nothing" would put a class in the catalog that can never build a tile.
    /// </remarks>
    public const string None = "";

    /// <summary>
    /// The id a layout written before tile kinds existed meant.
    /// </summary>
    /// <remarks>
    /// A lower-casing and nothing else, which is the whole of the migration for this value:
    /// <c>JsonDefaults.Options</c> registers <c>JsonStringEnumConverter</c>, so
    /// <see cref="TileContentType"/> has always been a <em>string</em> on disk. Going from an enum to a
    /// kind id is a change of type in C# over identical bytes in the file — there is no
    /// number-to-name conversion anywhere in this.
    /// </remarks>
    public static string FromLegacy(TileContentType type) =>
        type == TileContentType.Empty ? None : type.ToString().ToLowerInvariant();

    /// <summary>
    /// What an older build would have called this kind, or null when it has no name there.
    /// </summary>
    /// <remarks>
    /// <para>The way back, and the reason it exists is <see cref="TileNode"/>'s dual write: a layout
    /// this build saves keeps the old fields beside the new ones, so an installation rolled back by
    /// Velopack still opens it. Without that, the older build reads every leaf as an empty tile and the
    /// first splitter drag saves that emptiness over the user's layout.</para>
    /// <para>Derived from <see cref="FromLegacy"/> rather than written out a second time, so the two
    /// cannot drift, and answering <c>null</c> for anything it does not find is the whole point: a kind
    /// registered after this enum was closed has no legacy name, and an older build could not have built
    /// it anyway. It reads as an empty tile there — the same answer this build gives a kind it does not
    /// have.</para>
    /// </remarks>
    public static TileContentType? ToLegacy(string? kind) =>
        Enum.GetValues<TileContentType>()
            .Where(type => type != TileContentType.Empty)
            .Cast<TileContentType?>()
            .FirstOrDefault(type => FromLegacy(type!.Value) == kind);
}
