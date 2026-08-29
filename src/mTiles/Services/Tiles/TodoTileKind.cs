using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.ViewModels;

namespace mTiles.Services.Tiles;

/// <summary>A checklist, kept in the workspace's own <c>todos/</c> folder.</summary>
public sealed class TodoTileKind : TileKind<TodoTileViewModel>
{
    public override string Id => TileKindIds.Todo;
    public override string DisplayName => "Todo";
    public override string IconId => "checklist";
    public override string AccentKey => "TileAccentTodo";

    protected override TodoTileViewModel Create(TileContext context, JsonObject? state) =>
        new(state.String(MarkdownTileKind.FilePathKey)
            ?? TodoTileViewModel.NewFilePath(context.WorkingDirectory), context.Settings);

    protected override JsonObject? Save(TodoTileViewModel tile) =>
        new() { [MarkdownTileKind.FilePathKey] = tile.FilePath };
}
