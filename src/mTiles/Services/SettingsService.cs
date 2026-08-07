using System.Diagnostics;
using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services;

public sealed class SettingsService
{
    private readonly string _filePath;
    private Timer? _debounceTimer;

    public AppSettings Settings { get; private set; } = new();

    public event Action? SettingsChanged;

    public SettingsService() : this(null) { }

    /// <param name="settingsFilePath">Where the settings live. Defaults to the user's own file; a test
    /// passes a temporary one, because this constructor both reads <em>and writes</em> (seeding the
    /// default profiles saves) and no test may edit the settings of whoever is running it. Internal for
    /// that reason: it exists for the test assembly, and the application has no business choosing.</param>
    internal SettingsService(string? settingsFilePath)
    {
        _filePath = settingsFilePath ?? AppPaths.GetSettingsFilePath();

        // A bare file name has no directory part, and `GetDirectoryName` answers that with an empty
        // string rather than null — which `CreateDirectory` rejects. So the check is for empty, and it
        // is a real case rather than a defensive one: "settings.json" is a legal argument.
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        Load();
        MigrateLegacySettings();
        SeedDefaultProfiles();
    }

    /// <summary>
    /// Carries answers forward from settings written by an older version.
    /// <para>Renaming a property means the old value is simply not read, and the new one starts at its
    /// default. That is harmless for a font size. It is not harmless for
    /// <see cref="AppSettings.GitIgnoreMTerminalDir"/>, whose default writes to the user's repository:
    /// somebody who turned the old switch off had said no, and an update is not the moment to stop
    /// hearing it.</para>
    /// </summary>
    private void MigrateLegacySettings()
    {
        if (Settings.LegacyGitHideMTerminalDir is { } wanted)
        {
            Settings.GitIgnoreMTerminalDir = wanted;
            Settings.LegacyGitHideMTerminalDir = null;   // read once; the next save drops it
            Save();
        }
    }

    public void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options) ?? new AppSettings();
        }
        catch
        {
            Settings = new AppSettings();
        }
    }

    private void SeedDefaultProfiles()
    {
        var defaults = new List<UserShellProfile>
        {
            new()
            {
                Name = "Claude Code",
                ShellName = "",
                RequiredAiToolBinaryName = "claude",
                StartupScript = "claude --resume ${tileId}",
                FallbackScript = "claude --session-id ${tileId}"
            },
            new()
            {
                Name = "Pi Agent",
                ShellName = "",
                RequiredAiToolBinaryName = "pi",
                StartupScript = "pi --session-id ${tileId}",
                FallbackScript = "pi --session-id ${tileId}"
            },
            new()
            {
                Name = "Open Claude",
                ShellName = "",
                RequiredAiToolBinaryName = "openclaude",
                StartupScript = "openclaude --resume ${tileId}",
                FallbackScript = "openclaude --session-id ${tileId}"
            },
            new()
            {
                Name = "OpenCode",
                ShellName = "",
                RequiredAiToolBinaryName = "opencode",
                StartupScript = "opencode --session ${tileId}",
                FallbackScript = "opencode"
            },
            new()
            {
                Name = "Codex",
                ShellName = "",
                RequiredAiToolBinaryName = "codex",
                StartupScript = "codex resume ${tileId}",
                FallbackScript = "codex"
            }
        };

        var dirty = false;
        foreach (var profile in defaults)
        {
            var exists = Settings.ShellProfiles
                .Any(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
            if (exists)
                continue;
            Settings.ShellProfiles.Add(profile);
            dirty = true;
        }

        if (dirty) Save();
    }

    /// <summary>
    /// Writes the settings out now.
    /// <para>Serialised against itself, because two writers really do meet: the debounce timer fires on
    /// a thread-pool thread while the window closing calls this directly on the UI thread. Two
    /// <c>WriteAllText</c> on one path is a sharing violation — caught on the timer's side, and
    /// <em>unhandled</em> on the closing side, which is the worst moment to throw.</para>
    /// </summary>
    public void Save()
    {
        var json = JsonSerializer.Serialize(Settings, JsonDefaults.Options);
        lock (_writeLock)
            File.WriteAllText(_filePath, json);
    }

    private readonly Lock _writeLock = new();

    /// <summary>
    /// Writes the settings out shortly after the last change, rather than on every keystroke.
    /// <para>The write is wrapped because it happens on a thread-pool thread half a second later, with
    /// nobody left to catch anything: an unhandled exception there <em>terminates the process</em>. And
    /// it can throw for ordinary reasons — a settings directory on a network profile or a removed
    /// drive, or simply one deleted between the edit and the write. Losing a settings save is a
    /// nuisance; losing the application, with every terminal in it, is not.</para>
    /// </summary>
    public void DebouncedSave()
    {
        // Locked because settings change from more than one place — a tile's own timer, the settings
        // dialog, the window closing — and swapping the field unguarded can drop a timer nobody
        // disposes or dispose one another thread is about to use.
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => TrySave(), null, AppDefaults.SettingsDebounceMs, Timeout.Infinite);
        }
    }

    private readonly Lock _debounceLock = new();

    private void TrySave()
    {
        try
        {
            Save();
        }
        catch (Exception ex)
        {
            // Reported rather than swallowed: settings that silently stop persisting look like settings
            // that do not work, and there is nothing else anywhere to say otherwise.
            Trace.TraceWarning("Saving settings to '{0}' failed: {1}", _filePath, ex);
        }
    }

    public void NotifyChanged()
    {
        SettingsChanged?.Invoke();
        DebouncedSave();
    }

}
