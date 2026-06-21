using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MTerminal.Models;
using MTerminal.Services;

namespace MTerminal.ViewModels;

public partial class MarkdownTileViewModel : ObservableObject, IFileContent, IDisposable
{
    [ObservableProperty]
    private string _fontFamily;

    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private string? _mdText;

    private string _filePath;
    private readonly SettingsService? _settingsService;
    private Timer? _saveTimer;
    private Timer? _reloadTimer;
    private bool _isLoading;
    private FileSystemWatcher? _watcher;
    private volatile bool _hasPendingChanges;
    private volatile string _lastSavedText = "";

    public string FilePath => _filePath;

    public MarkdownTileViewModel(string filePath, SettingsService? settingsService = null)
    {
        _filePath = filePath;
        _settingsService = settingsService;
        var s = settingsService?.Settings;
        _fontFamily = s?.FontFamily ?? AppDefaults.FontFamily;
        _fontSize = s?.FontSize ?? AppDefaults.FontSize;
        _isLoading = true;
        LoadFromFile();
        _isLoading = false;

        if (_settingsService != null)
            _settingsService.SettingsChanged += OnSettingsChanged;

        StartWatching();
    }

    partial void OnMdTextChanged(string? value)
    {
        ScheduleSave();
    }

    private void OnSettingsChanged()
    {
        var s = _settingsService!.Settings;
        if (s.FontFamily != FontFamily)
            FontFamily = s.FontFamily;
        if (Math.Abs(s.FontSize - FontSize) > AppDefaults.FontSizeEpsilon)
            FontSize = s.FontSize;
    }

    public void RenameFile(string newName)
    {
        var sanitized = IFileContent.SanitizeFileName(newName);
        if (string.IsNullOrEmpty(sanitized)) return;

        var dir = Path.GetDirectoryName(_filePath);
        if (dir == null) return;

        var newPath = Path.Combine(dir, sanitized + ".md");
        if (string.Equals(newPath, _filePath, StringComparison.OrdinalIgnoreCase)) return;

        _saveTimer?.Dispose();
        _saveTimer = null;
        SaveToFile(_filePath);

        try
        {
            _watcher?.Dispose();
            _watcher = null;

            if (File.Exists(_filePath))
                File.Move(_filePath, newPath, overwrite: false);

            _filePath = newPath;
            _hasPendingChanges = false;
            StartWatching();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("MarkdownTile rename failed: {0}", ex.Message);
            if (_hasPendingChanges) ScheduleSave();
            StartWatching();
        }
    }

    private void ScheduleSave()
    {
        if (_isLoading) return;
        _hasPendingChanges = true;
        var text = MdText ?? "";
        var path = _filePath;
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ =>
        {
            SaveContent(text, path);
            _lastSavedText = text;
            _hasPendingChanges = false;
        }, null, AppDefaults.SaveDebounceMs, Timeout.Infinite);
    }

    private void LoadFromFile()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var text = File.ReadAllText(_filePath);
            _lastSavedText = text;
            MdText = text;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("MarkdownTile load failed: {0}", ex.Message);
        }
    }

    private void SaveToFile(string path)
    {
        var text = MdText ?? "";
        SaveContent(text, path);
        _lastSavedText = text;
    }

    private static void SaveContent(string text, string path)
    {
        FileHelper.WriteWithRetry(path, p => File.WriteAllText(p, text));
    }

    private void StartWatching()
    {
        var dir = Path.GetDirectoryName(_filePath);
        var name = Path.GetFileName(_filePath);
        if (dir == null || name == null) return;

        try
        {
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileChanged;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("MarkdownTile watcher failed: {0}", ex.Message);
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        _reloadTimer?.Dispose();
        _reloadTimer = new Timer(_ =>
            Dispatcher.UIThread.Post(ReloadFromFile), null, AppDefaults.WatcherDebounceMs, Timeout.Infinite);
    }

    private void ReloadFromFile()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var text = File.ReadAllText(_filePath);
            if (text == _lastSavedText || text == MdText)
                return;

            _isLoading = true;
            _lastSavedText = text;
            MdText = text;
            _isLoading = false;
        }
        catch (Exception ex)
        {
            _isLoading = false;
            System.Diagnostics.Trace.TraceWarning("MarkdownTile reload failed: {0}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_settingsService != null)
            _settingsService.SettingsChanged -= OnSettingsChanged;
        _saveTimer?.Dispose();
        _reloadTimer?.Dispose();
        SaveToFile(_filePath);
        _watcher?.Dispose();
    }
}
