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
        SeedDefaultProfiles();
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

    public void Save()
    {
        var json = JsonSerializer.Serialize(Settings, JsonDefaults.Options);
        File.WriteAllText(_filePath, json);
    }

    public void DebouncedSave()
    {
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ => Save(), null, AppDefaults.SettingsDebounceMs, Timeout.Infinite);
    }

    public void NotifyChanged()
    {
        SettingsChanged?.Invoke();
        DebouncedSave();
    }

}
