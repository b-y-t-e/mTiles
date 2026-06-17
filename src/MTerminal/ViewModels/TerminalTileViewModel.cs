using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MTerminal.Models;
using MTerminal.Services;

namespace MTerminal.ViewModels;

public partial class TerminalTileViewModel : ObservableObject, IDisposable
{
    public string WorkingDirectory { get; }
    public ShellProfile Shell { get; }

    [ObservableProperty]
    private string _fontFamily;

    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private TerminalTheme _theme;

    private readonly SettingsService _settingsService;

    internal Control? CachedControl { get; set; }
    internal bool IsLaunched { get; set; }

    public TerminalTileViewModel(string workingDirectory, ShellProfile? shell, SettingsService settingsService)
    {
        _settingsService = settingsService;
        var s = _settingsService.Settings;
        WorkingDirectory = workingDirectory;
        Shell = shell ?? ShellDetector.ResolveDefault(s);
        _theme = TerminalTheme.GetByName(s.ColorThemeName);
        _fontFamily = s.TerminalFontFamily;
        _fontSize = s.TerminalFontSize;

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        var s = _settingsService.Settings;
        var newTheme = TerminalTheme.GetByName(s.ColorThemeName);
        if (newTheme.Name != Theme.Name)
            Theme = newTheme;
        if (s.TerminalFontFamily != FontFamily)
            FontFamily = s.TerminalFontFamily;
        if (Math.Abs(s.TerminalFontSize - FontSize) > AppDefaults.FontSizeEpsilon)
            FontSize = s.TerminalFontSize;
    }

    public void Dispose()
    {
        _settingsService.SettingsChanged -= OnSettingsChanged;
        if (CachedControl is Iciclecreek.Terminal.TerminalControl tc)
        {
            try { tc.Kill(); } catch { }
        }
    }
}
