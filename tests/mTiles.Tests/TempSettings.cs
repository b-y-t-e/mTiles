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
    private readonly string _directory;

    /// <summary>A directory nobody else is using.</summary>
    public TempSettings()
        : this(Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N")))
    {
    }

    /// <summary>
    /// The same directory as an earlier instance, for a test that has to reopen what it saved.
    /// </summary>
    /// <remarks>Reopening is the only way to assert what actually reached the file, as distinct from
    /// what is still in memory — and both instances then clean up the same directory, which is
    /// harmless because the second delete finds nothing.</remarks>
    public TempSettings(string directory)
    {
        _directory = directory;
        SettingsFile = Path.Combine(_directory, "settings.json");
        Service = new SettingsService(SettingsFile);
        Workspaces = new WorkspaceService(Path.Combine(_directory, "workspaces.json"));
        Layouts = new PersistenceService(Path.Combine(_directory, "workspaces"));
    }

    /// <summary>Where this instance keeps everything, for a test that reopens it.</summary>
    public string Directory => _directory;

    /// <summary>The settings file itself, for a test asserting what is <em>not</em> in it.</summary>
    public string SettingsFile { get; }

    public SettingsService Service { get; }

    /// <summary>The other two services that write into the same place, for tests that need a whole main
    /// window rather than only its settings.</summary>
    public WorkspaceService Workspaces { get; }

    public PersistenceService Layouts { get; }

    public void Dispose()
    {
        try { if (System.IO.Directory.Exists(_directory)) System.IO.Directory.Delete(_directory, recursive: true); }
        catch { /* a temp directory nobody will look at again */ }
    }
}
