using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MTerminal.Models;
using MTerminal.Services;

namespace MTerminal.ViewModels;

public partial class TodoTileViewModel : ObservableObject, IFileContent, IDisposable
{
    private static readonly Regex MdLineRegex = new(@"^(?:- )?\[([ xX])\] (.*)$", RegexOptions.Compiled);

    [ObservableProperty]
    private string _fontFamily;

    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private double _checkSize = 20.0;

    [ObservableProperty]
    private Thickness _itemPadding = new(2, 1);

    public ObservableCollection<TodoItem> Items { get; } = [];

    private string _filePath;
    private readonly SettingsService? _settingsService;
    private Timer? _saveTimer;
    private Timer? _reloadTimer;
    private bool _isLoading;
    private FileSystemWatcher? _watcher;
    private bool _hasPendingChanges;

    public string FilePath => _filePath;

    public TodoTileViewModel(string filePath, SettingsService? settingsService = null)
    {
        _filePath = filePath;
        _settingsService = settingsService;
        var s = settingsService?.Settings;
        _fontFamily = s?.FontFamily ?? AppDefaults.FontFamily;
        _fontSize = s?.FontSize ?? AppDefaults.FontSize;
        UpdateSizeMetrics();
        _isLoading = true;
        LoadFromFile();
        _isLoading = false;

        if (Items.Count == 0)
            Items.Add(CreateItem());

        if (_settingsService != null)
            _settingsService.SettingsChanged += OnSettingsChanged;

        StartWatching();
    }

    private void UpdateSizeMetrics()
    {
        var scale = FontSize / AppDefaults.FontSize;
        CheckSize = FontSize * AppDefaults.CheckSizeRatio;
        ItemPadding = new Thickness(3 * scale, 2 * scale);
    }

    private void OnSettingsChanged()
    {
        var s = _settingsService!.Settings;
        if (s.FontFamily != FontFamily)
            FontFamily = s.FontFamily;
        if (Math.Abs(s.FontSize - FontSize) > AppDefaults.FontSizeEpsilon)
        {
            FontSize = s.FontSize;
            UpdateSizeMetrics();
        }
    }

    public string InsertItemAfter(int index)
    {
        var item = CreateItem();
        var insertAt = index + 1;

        var firstDoneIdx = -1;
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].IsDone) { firstDoneIdx = i; break; }
        }

        if (firstDoneIdx >= 0 && insertAt > firstDoneIdx)
            insertAt = firstDoneIdx;

        Items.Insert(insertAt, item);
        ScheduleSave();
        return item.Id;
    }

    [RelayCommand]
    private void ToggleItem(string? id)
    {
        if (id == null) return;
        var idx = -1;
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].Id == id) { idx = i; break; }
        }
        if (idx < 0) return;

        var item = Items[idx];
        item.IsDone = !item.IsDone;
        Items.RemoveAt(idx);

        if (item.IsDone)
        {
            Items.Add(item);
        }
        else
        {
            var insertIdx = Items.Count;
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].IsDone) { insertIdx = i; break; }
            }
            Items.Insert(insertIdx, item);
        }

        ScheduleSave();
    }

    [RelayCommand]
    private void RemoveItem(string? id)
    {
        if (id == null) return;
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].Id == id)
            {
                Items.RemoveAt(i);
                if (Items.Count == 0)
                    Items.Add(CreateItem());
                ScheduleSave();
                return;
            }
        }
    }

    public void OnItemTextChanged()
    {
        ScheduleSave();
    }

    private static TodoItem CreateItem(string text = "")
    {
        return new TodoItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = text,
            IsDone = false
        };
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
        SaveToFile([.. Items], _filePath);

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
            System.Diagnostics.Trace.TraceWarning("TodoTile rename failed: {0}", ex.Message);
            if (_hasPendingChanges) ScheduleSave();
            StartWatching();
        }
    }

    private void ScheduleSave()
    {
        if (_isLoading) return;
        _hasPendingChanges = true;
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                var snapshot = Items.ToList();
                var path = _filePath;
                Task.Run(() =>
                {
                    SaveToFile(snapshot, path);
                    _hasPendingChanges = false;
                });
            }), null, AppDefaults.SaveDebounceMs, Timeout.Infinite);
    }

    private static List<TodoItem> ParseMarkdown(string[] lines)
    {
        var items = new List<TodoItem>();
        foreach (var line in lines)
        {
            var match = MdLineRegex.Match(line);
            if (match.Success)
            {
                items.Add(new TodoItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Text = match.Groups[2].Value,
                    IsDone = match.Groups[1].Value != " "
                });
            }
        }
        return items;
    }

    private void LoadFromFile()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            foreach (var item in ParseMarkdown(File.ReadAllLines(_filePath)))
                Items.Add(item);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("TodoTile load failed: {0}", ex.Message);
        }
    }

    private void SaveToFile(List<TodoItem> snapshot, string path)
    {
        var lines = snapshot.Select(item => $"[{(item.IsDone ? "x" : " ")}] {item.Text}");
        var w = _watcher;
        if (w != null) w.EnableRaisingEvents = false;
        FileHelper.WriteWithRetry(path, p => File.WriteAllLines(p, lines));
        if (w != null) w.EnableRaisingEvents = true;
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
            System.Diagnostics.Trace.TraceWarning("TodoTile watcher failed: {0}", ex.Message);
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_hasPendingChanges) return;

        _reloadTimer?.Dispose();
        _reloadTimer = new Timer(_ =>
            Dispatcher.UIThread.Post(ReloadFromFile), null, AppDefaults.WatcherDebounceMs, Timeout.Infinite);
    }

    private void ReloadFromFile()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var newItems = ParseMarkdown(File.ReadAllLines(_filePath));

            _isLoading = true;
            Items.Clear();
            foreach (var item in newItems)
                Items.Add(item);
            if (Items.Count == 0)
                Items.Add(CreateItem());
            _isLoading = false;
        }
        catch (Exception ex)
        {
            _isLoading = false;
            System.Diagnostics.Trace.TraceWarning("TodoTile reload failed: {0}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_settingsService != null)
            _settingsService.SettingsChanged -= OnSettingsChanged;
        _watcher?.Dispose();
        _saveTimer?.Dispose();
        _reloadTimer?.Dispose();
        SaveToFile([.. Items], _filePath);
    }
}
