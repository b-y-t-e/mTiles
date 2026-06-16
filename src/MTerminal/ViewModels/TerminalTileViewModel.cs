using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MTerminal.Models;
using MTerminal.Services;

namespace MTerminal.ViewModels;

public partial class TerminalTileViewModel : ObservableObject, IDisposable
{
    public string WorkingDirectory { get; }
    public ShellProfile Shell { get; }
    public string FontFamily { get; }
    public double FontSize { get; }

    [ObservableProperty]
    private TerminalTheme _theme;

    private readonly SettingsService _settingsService;

    internal Control? CachedControl { get; set; }
    internal bool IsLaunched { get; set; }

    public TerminalTileViewModel(string workingDirectory, ShellProfile? shell = null, SettingsService? settingsService = null)
    {
        _settingsService = settingsService ?? new SettingsService();
        var s = _settingsService.Settings;
        WorkingDirectory = workingDirectory;
        Shell = shell ?? ShellDetector.ResolveDefault(s);
        _theme = TerminalTheme.GetByName(s.TerminalThemeName);
        FontFamily = s.TerminalFontFamily;
        FontSize = s.TerminalFontSize;

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        var newTheme = TerminalTheme.GetByName(_settingsService.Settings.TerminalThemeName);
        if (newTheme.Name != Theme.Name)
            Theme = newTheme;
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
