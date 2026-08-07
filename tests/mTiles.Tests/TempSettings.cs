using mTiles.Services;

namespace mTiles.Tests;

/// <summary>
/// A <see cref="SettingsService"/> pointed at a throwaway file.
/// <para>Not a convenience: constructing the real one both reads <em>and writes</em> — seeding the
/// default profiles saves — so a test that took the default path would edit the settings of whoever is
/// running it. Shared so that stays true of every test, including the next one somebody writes.</para>
/// </summary>
internal sealed class TempSettings : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

    public TempSettings()
    {
        Service = new SettingsService(Path.Combine(_directory, "settings.json"));
        Workspaces = new WorkspaceService(Path.Combine(_directory, "workspaces.json"));
        Layouts = new PersistenceService(Path.Combine(_directory, "workspaces"));
    }

    public SettingsService Service { get; }

    /// <summary>The other two services that write into the same place, for tests that need a whole main
    /// window rather than only its settings.</summary>
    public WorkspaceService Workspaces { get; }

    public PersistenceService Layouts { get; }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* a temp directory nobody will look at again */ }
    }
}
