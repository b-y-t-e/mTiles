using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MTerminal.Models;
using MTerminal.Services;

namespace MTerminal.ViewModels;

public partial class TodoTileViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private string _fontFamily;

    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private double _checkSize = 20.0;

    [ObservableProperty]
    private Thickness _itemPadding = new(2, 1);

    public ObservableCollection<TodoItem> Items { get; } = [];

    private readonly string _filePath;
    private readonly SettingsService? _settingsService;
    private Timer? _saveTimer;
    private bool _isLoading;

    public string FilePath => _filePath;

    public TodoTileViewModel(string filePath, SettingsService? settingsService = null)
    {
        _filePath = filePath;
        _settingsService = settingsService;
        var s = settingsService?.Settings;
        _fontFamily = s?.NoteFontFamily ?? "Cascadia Mono, Consolas, monospace";
        _fontSize = s?.NoteFontSize ?? 14;
        UpdateSizeMetrics();
        _isLoading = true;
        LoadFromFile();
        _isLoading = false;

        if (Items.Count == 0)
            Items.Add(CreateItem());

        if (_settingsService != null)
            _settingsService.SettingsChanged += OnSettingsChanged;
    }

    private void UpdateSizeMetrics()
    {
        var scale = FontSize / 14.0;
        CheckSize = FontSize * 1.4;
        ItemPadding = new Thickness(3 * scale, 2 * scale);
    }

    private void OnSettingsChanged()
    {
        var s = _settingsService!.Settings;
        if (s.NoteFontFamily != FontFamily)
            FontFamily = s.NoteFontFamily;
        if (Math.Abs(s.NoteFontSize - FontSize) > 0.01)
        {
            FontSize = s.NoteFontSize;
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

    private void ScheduleSave()
    {
        if (_isLoading) return;
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                var snapshot = Items.ToList();
                Task.Run(() => SaveToFile(snapshot));
            }), null, 2000, Timeout.Infinite);
    }

    private void LoadFromFile()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<TodoFileData>(json, JsonDefaults.Options);
            if (data?.Items != null)
            {
                foreach (var item in data.Items)
                    Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("TodoTile load failed: {0}", ex.Message);
        }
    }

    private void SaveToFile(List<TodoItem> snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir != null) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(new TodoFileData { Items = snapshot }, JsonDefaults.Options);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("TodoTile save failed: {0}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_settingsService != null)
            _settingsService.SettingsChanged -= OnSettingsChanged;
        _saveTimer?.Dispose();
        SaveToFile([.. Items]);
    }

    private sealed class TodoFileData
    {
        public List<TodoItem> Items { get; set; } = [];
    }
}
