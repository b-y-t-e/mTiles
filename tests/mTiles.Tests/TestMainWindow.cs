using mTiles.Services;
using mTiles.ViewModels;

namespace mTiles.Tests;

/// <summary>A <see cref="MainWindowViewModel"/> over files in a scratch directory, for a test.</summary>
internal static class TestMainWindow
{
    /// <summary>
    /// A window over two workspaces, <c>First</c> and <c>Second</c>, that exist before it is built — the
    /// panel reads the service when it is built, so adding them afterwards would leave it looking at an
    /// empty list.
    /// </summary>
    /// <param name="directory">Where the settings, the workspace list and the layouts are kept.</param>
    /// <param name="windowLayout">Given, the window gets its own tile layout, kept under this
    /// application directory; left out, it has none.</param>
    /// <param name="settings">The settings to use, for a test that also needs them; otherwise a file in
    /// <paramref name="directory"/>.</param>
    /// <param name="memoryProbe">What the memory reading asks.</param>
    /// <param name="workspaces">False for a window with no workspaces at all.</param>
    public static MainWindowViewModel Create(string directory, TempAppData? windowLayout = null,
        SettingsService? settings = null, IProcessMemoryProbe? memoryProbe = null, bool workspaces = true)
    {
        var list = new WorkspaceService(Path.Combine(directory, "workspaces.json"));
        if (workspaces)
        {
            list.AddWorkspace(Path.Combine(directory, "first"), "First");
            list.AddWorkspace(Path.Combine(directory, "second"), "Second");
        }

        settings ??= new SettingsService(Path.Combine(directory, "settings.json"));
        var usage = new AiUsageService(settings, sources: _ => []);

        return new MainWindowViewModel(list, new PersistenceService(Path.Combine(directory, "layouts")),
            settings, TestTiles.Catalog(settings),
            memoryProbe: memoryProbe,
            windowCatalog: windowLayout is null ? null : panel => mTiles.App.BuildWindowTileCatalog(usage, panel),
            windowPersistence: windowLayout is null
                ? null
                : new PersistenceService(Path.Combine(windowLayout.Root, "window")));
    }
}
