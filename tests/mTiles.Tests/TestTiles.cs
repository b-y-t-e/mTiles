using mTiles.Services.Database;
using mTiles.Services.Tiles;

namespace mTiles.Tests;

/// <summary>
/// The tile catalog the application itself runs on, for a test.
/// </summary>
/// <remarks>
/// Deliberately the real registration (<c>App.BuildTileCatalog</c>) rather than a list of kinds this
/// file keeps in step with it: a test catalog would answer questions about itself, and the questions
/// worth asking — does every historical layout still open, does every kind round-trip — are about the
/// one the user gets.
/// </remarks>
internal static class TestTiles
{
    /// <summary>A yes/no answer read as the agent-switch question's: yes carries the context over, no stays.
    /// For the tests written when that question had only two answers.</summary>
    public static Func<string, Task<mTiles.ViewModels.HandoverAnswer>> AsHandover(Func<string, Task<bool>>? confirm) =>
        async message => confirm is not null && await confirm(message)
            ? mTiles.ViewModels.HandoverAnswer.WithContext
            : mTiles.ViewModels.HandoverAnswer.Cancel;

    /// <param name="settings">What the database service manager reads. It is constructed and never
    /// started: the database kind needs one to exist, not to be listening. The usage service is given
    /// no sources for the same reason — the usage kind needs one to exist, and a real list would have a
    /// layout test asking three services on the network what a subscription has left.</param>
    public static TileCatalog Catalog(mTiles.Services.SettingsService settings) =>
        mTiles.App.BuildTileCatalog(new DatabaseServiceManager(settings),
            new mTiles.Services.AiUsageService(settings, sources: _ => []),
            ConversationStore());

    /// <summary>A conversation store of its own, in a file no other test shares.</summary>
    public static mTiles.AgentSessions.Storage.SqliteConversationStore ConversationStore() =>
        new(Path.Combine(Path.GetTempPath(), $"mtiles-test-conversations-{Guid.NewGuid():N}.db"));

    /// <summary>The window's catalog, holding the list it is handed.</summary>
    public static TileCatalog WindowCatalog(mTiles.Services.SettingsService settings,
        mTiles.ViewModels.WorkspacesPanelViewModel panel) =>
        mTiles.App.BuildWindowTileCatalog(
            new mTiles.Services.AiUsageService(settings, sources: _ => []), () => panel);
}
