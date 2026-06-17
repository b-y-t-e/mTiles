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
