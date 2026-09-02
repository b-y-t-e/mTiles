using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.ViewModels;

namespace mTiles.Services.Tiles;

/// <summary>The iterative goal loop: clarify, plan, implement, review, repeat.</summary>
public sealed class GoalTileKind : TileKind<GoalTileViewModel>
{
    /// <summary>Where this goal's own session file lives, under the workspace's <c>goals/</c> folder.
    /// A tile without one has never had a goal typed into it and gets a fresh file.</summary>
    public const string FilePathKey = "filePath";

    public override string Id => TileKindIds.Goal;
    public override string DisplayName => "Goal";
    public override string IconId => "goal";
    public override string AccentKey => "TileAccentGoal";

    protected override GoalTileViewModel Create(TileContext context, JsonObject? state) =>
        state.String(FilePathKey) is { } filePath
            ? new GoalTileViewModel(filePath, context.WorkingDirectory, context.Settings,
                context.GitWatcher)
            : new GoalTileViewModel(context.WorkingDirectory, context.Settings, context.GitWatcher);

    protected override JsonObject? Save(GoalTileViewModel tile) =>
        new() { [FilePathKey] = tile.FilePath };
}
