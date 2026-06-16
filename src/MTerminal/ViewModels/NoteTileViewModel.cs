using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MTerminal.Services;

namespace MTerminal.ViewModels;

public partial class NoteTileViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string _fontFamily;

    [ObservableProperty]
    private double _fontSize;

    private readonly string _filePath;
    private readonly SettingsService? _settingsService;
    private Timer? _saveTimer;
    private bool _isLoading;

    public string FilePath => _filePath;

    internal Control? CachedControl { get; set; }

    public NoteTileViewModel(string filePath, SettingsService? settingsService = null)
    {
        _filePath = filePath;
        _settingsService = settingsService;
        var s = settingsService?.Settings;
        _fontFamily = s?.NoteFontFamily ?? "Cascadia Mono, Consolas, monospace";
        _fontSize = s?.NoteFontSize ?? 14;
        _isLoading = true;
        LoadFromFile();
        _isLoading = false;

        if (_settingsService != null)
            _settingsService.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        var s = _settingsService!.Settings;
        if (s.NoteFontFamily != FontFamily)
            FontFamily = s.NoteFontFamily;
        if (Math.Abs(s.NoteFontSize - FontSize) > 0.01)
            FontSize = s.NoteFontSize;
    }

    partial void OnTextChanged(string value)
    {
        if (_isLoading) return;
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ => SaveToFile(), null, 2000, Timeout.Infinite);
    }

    private void LoadFromFile()
    {
        if (File.Exists(_filePath))
        {
            try { Text = File.ReadAllText(_filePath); }
            catch { }
        }
    }

    private void SaveToFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, Text);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("NoteTile save failed: {0}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_settingsService != null)
            _settingsService.SettingsChanged -= OnSettingsChanged;
        _saveTimer?.Dispose();
        SaveToFile();
    }
}
