using Avalonia.Data.Converters;
using Material.Icons;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>Which picture a file chip wears: a folder for a directory, a note for a pasted note, a page for
/// anything else.</summary>
/// <remarks>Here rather than beside <c>ComposerFile</c> for the reason <see cref="SpecialDirectoryIcon"/> is:
/// which picture stands for a thing is a fact about the drawing, and <c>Material.Icons</c> has no business in
/// <c>ViewModels/</c>.</remarks>
public static class ComposerFileIcon
{
    public static readonly FuncValueConverter<ComposerFile?, MaterialIconKind> Kind =
        new(file => file switch
        {
            { IsDirectory: true } => MaterialIconKind.FolderOutline,
            { IsPastedNote: true } => MaterialIconKind.TextBoxOutline,
            _ => MaterialIconKind.FileDocumentOutline,
        });
}
