using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.Services.Database;
using mTiles.ViewModels;

namespace mTiles.Services.Tiles;

/// <summary>
/// The HTTP bridge that lets an agent in a terminal tile query this machine's databases.
/// </summary>
/// <remarks>
/// The one kind with a dependency of its own rather than one out of <see cref="TileContext"/>: the
/// service manager is an application singleton, so it is handed to the kind once at registration
/// instead of to every tile at creation.
/// </remarks>
public sealed class DatabaseTileKind(DatabaseServiceManager databases) : TileKind<DatabaseTileViewModel>
{
    public override string Id => TileKindIds.Database;
    public override string DisplayName => "Database";
    public override string IconId => "database";
    public override string AccentKey => "TileAccentDatabase";

    /// <summary>Not the display name: the tiles have always been <c>DB#1</c>, and renaming them would
    /// rename tiles in layouts already on disk.</summary>
    public override string NamePrefix => "DB";

    protected override DatabaseTileViewModel Create(TileContext context, JsonObject? state) =>
        new(context.WorkingDirectory, context.Settings, databases)
        {
            TileSettingsChanged = context.RequestSave,
            OpenDatabaseSettings = () => context.OpenSettings?.Invoke(SettingsTabs.Database)
        };
}
