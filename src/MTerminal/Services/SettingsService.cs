using System.Text.Json;
using MTerminal.Models;

namespace MTerminal.Services;

public sealed class SettingsService
{
    private readonly string _filePath;
    private Timer? _debounceTimer;

    public AppSettings Settings { get; private set; } = new();

    public event Action? SettingsChanged;

    public SettingsService()
    {
        var appDir = AppPaths.GetAppDataDirectory();
        Directory.CreateDirectory(appDir);
        _filePath = AppPaths.GetSettingsFilePath();
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
            },
            new()
            {
                Name = "Pi Agent",
                ShellName = "",
                RequiredAiToolBinaryName = "pi",
                StartupScript = "pi --session ${tileId}",
                FallbackScript = "pi"
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
