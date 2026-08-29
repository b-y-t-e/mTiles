using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.ViewModels;

namespace mTiles.Services.Tiles;

/// <summary>A page of markdown, kept in the workspace's own <c>notes/</c> folder.</summary>
public sealed class NoteTileKind : TileKind<NoteTileViewModel>
{
    public override string Id => TileKindIds.Note;
    public override string DisplayName => "Note";
    public override string IconId => "note";
    public override string AccentKey => "TileAccentNote";

    protected override NoteTileViewModel Create(TileContext context, JsonObject? state) =>
        new(state.String(MarkdownTileKind.FilePathKey)
            ?? NoteTileViewModel.NewFilePath(context.WorkingDirectory), context.Settings);

    protected override JsonObject? Save(NoteTileViewModel tile) =>
        new() { [MarkdownTileKind.FilePathKey] = tile.FilePath };
}
