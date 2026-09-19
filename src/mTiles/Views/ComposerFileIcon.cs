using Avalonia.Data.Converters;
using Material.Icons;

namespace mTiles.Views;

/// <summary>Which picture a file chip wears: a folder for a directory, a page for anything else.</summary>
/// <remarks>Here rather than beside <c>ComposerFile</c> for the reason <see cref="SpecialDirectoryIcon"/> is:
/// which picture stands for a thing is a fact about the drawing, and <c>Material.Icons</c> has no business in
/// <c>ViewModels/</c>.</remarks>
public static class ComposerFileIcon
{
    public static readonly FuncValueConverter<bool, MaterialIconKind> Kind =
        new(isDirectory => isDirectory ? MaterialIconKind.FolderOutline : MaterialIconKind.FileDocumentOutline);
}
