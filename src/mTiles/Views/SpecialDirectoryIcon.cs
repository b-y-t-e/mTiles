using Avalonia.Data.Converters;
using Material.Icons;
using mTiles.Services;

namespace mTiles.Views;

/// <summary>
/// The glyph beside a workspace row's path — a house, a disk, a cog.
/// </summary>
/// <remarks>
/// <para>Here rather than on the view model for the reason <c>TileIcons</c> is here: which picture
/// stands for a kind of place is a fact about the drawing, and <c>Material.Icons</c> has no business
/// in <c>ViewModels/</c>. The view model answers <see cref="SpecialDirectoryKind"/> and this turns it
/// into something to draw.</para>
/// <para>A converter rather than a static call, because the row is a <c>DataTemplate</c> in an
/// <c>ItemsControl</c> and there is no code-behind holding each row to ask on its behalf. The
/// <c>FuncValueConverter</c> as a static field is the shape <c>DiffModeIconConverter</c> already uses
/// from markup.</para>
/// <para>Every kind is drawn but two: an ordinary project folder shows its branch or the offer to make
/// one, and a path nothing can make sense of has no picture that would be true. Both fall to a plain
/// folder, which is what a row would show if the visibility rule and this one ever disagreed — a
/// generic glyph rather than a blank where a glyph should be. The <c>_</c> is deliberate rather than a
/// case per kind: a kind added to the enum and forgotten here is drawn as a folder, which is wrong and
/// legible, where a thrown converter is an empty row and a line in a log nobody reads.</para>
/// </remarks>
public static class SpecialDirectoryIcon
{
    public static readonly FuncValueConverter<SpecialDirectoryKind, MaterialIconKind> Kind =
        new(kind => kind switch
        {
            SpecialDirectoryKind.Home => MaterialIconKind.Home,
            SpecialDirectoryKind.Desktop => MaterialIconKind.Monitor,
            SpecialDirectoryKind.Documents => MaterialIconKind.FileDocumentOutline,
            SpecialDirectoryKind.Downloads => MaterialIconKind.Download,
            SpecialDirectoryKind.Pictures => MaterialIconKind.Image,
            SpecialDirectoryKind.Music => MaterialIconKind.Music,
            SpecialDirectoryKind.Videos => MaterialIconKind.Movie,
            SpecialDirectoryKind.DriveRoot => MaterialIconKind.Harddisk,

            // A cog rather than a folder wearing one: this is drawn at ten pixels, where two shapes in
            // one glyph is a smudge and the folder is the half that says nothing — every row on this
            // line is a folder.
            SpecialDirectoryKind.System => MaterialIconKind.Cog,

            _ => MaterialIconKind.FolderOutline,
        });
}
