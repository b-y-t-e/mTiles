using Material.Icons;
using mTiles.Models;

namespace mTiles.Views;

/// <summary>
/// What a tile's header shows before its name, and which accent it is drawn in.
/// </summary>
/// <remarks>
/// <para>The same six icons the empty tile's chooser offers, so the picture a user pressed to make the
/// tile is the picture the tile then wears. They are written out in the chooser as literal markup — a
/// chooser card is a big button with a label under it, with nothing to gain from indirection — which
/// leaves this the only place a <see cref="TileContentType"/> has to become an icon on its own.</para>
/// <para><see cref="AccentKey"/> returns the name of a resource rather than a brush: the tile accents
/// are the one part of the palette <c>ThemeBridge</c> does not overwrite today, and returning a key the
/// view hands to <c>GetResourceObservable</c> costs nothing while leaving them free to become derived
/// later without this file being the reason a header icon stayed the wrong colour.</para>
/// </remarks>
internal static class TileTypeIcon
{
    public static MaterialIconKind Kind(TileContentType type) => type switch
    {
        TileContentType.Terminal => MaterialIconKind.Console,
        TileContentType.Note => MaterialIconKind.NoteEditOutline,
        TileContentType.Todo => MaterialIconKind.CheckboxMarkedOutline,
        TileContentType.Git => MaterialIconKind.SourceBranch,
        TileContentType.Database => MaterialIconKind.DatabaseOutline,
        TileContentType.Goal => MaterialIconKind.BullseyeArrow,
        _ => MaterialIconKind.PlusBoxOutline,
    };

    public static string AccentKey(TileContentType type) => type switch
    {
        TileContentType.Terminal => "TileAccentTerminal",
        TileContentType.Note => "TileAccentNote",
        TileContentType.Todo => "TileAccentTodo",
        TileContentType.Git => "TileAccentGit",
        TileContentType.Database => "TileAccentDatabase",
        TileContentType.Goal => "TileAccentGoal",
        // An empty tile has no type yet, so it gets the one colour that says nothing about which one it
        // is going to become.
        _ => "TextFaint",
    };
}
