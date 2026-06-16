using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MTerminal.Models;
using MTerminal.Services;

namespace MTerminal.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    public static string[] Themes { get; } = ["Dark", "Light"];
    public static string CustomShellOption => "Custom...";
    public static string[] TerminalThemeNames { get; } = TerminalTheme.BuiltIn.Select(t => t.Name).ToArray();

    private readonly SettingsService _settingsService;

    public ObservableCollection<string> ShellOptions { get; } = [];

    [ObservableProperty]
    private string _terminalThemeName;

    [ObservableProperty]
    private string _terminalFontFamily;

    [ObservableProperty]
    private double _terminalFontSize;

    [ObservableProperty]
    private string _editorFontFamily;

    [ObservableProperty]
    private double _editorFontSize;

    [ObservableProperty]
    private string _theme;

    [ObservableProperty]
    private string _selectedShell;

    [ObservableProperty]
    private string _customShellPath;

    [ObservableProperty]
    private string _customShellArgs;

    [ObservableProperty]
    private bool _isCustomShell;


    public SettingsViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        var s = settingsService.Settings;
        _terminalThemeName = s.TerminalThemeName;
        _terminalFontFamily = s.TerminalFontFamily;
        _terminalFontSize = s.TerminalFontSize;
        _editorFontFamily = s.EditorFontFamily;
        _editorFontSize = s.EditorFontSize;
        _theme = s.Theme;
        _customShellPath = s.CustomShellPath;
        _customShellArgs = s.CustomShellArgs;

        var detected = ShellDetector.Detect();
        foreach (var shell in detected)
            ShellOptions.Add(shell.Name);
        ShellOptions.Add(CustomShellOption);

        if (!string.IsNullOrEmpty(s.CustomShellPath))
        {
            _selectedShell = CustomShellOption;
            _isCustomShell = true;
        }
        else if (!string.IsNullOrEmpty(s.DefaultShellName) && ShellOptions.Contains(s.DefaultShellName))
        {
            _selectedShell = s.DefaultShellName;
        }
        else
        {
            _selectedShell = ShellOptions.Count > 1 ? ShellOptions[0] : CustomShellOption;
        }
    }

    partial void OnTerminalThemeNameChanged(string value) { _settingsService.Settings.TerminalThemeName = value; _settingsService.NotifyChanged(); }
    partial void OnTerminalFontFamilyChanged(string value) { _settingsService.Settings.TerminalFontFamily = value; _settingsService.NotifyChanged(); }
    partial void OnTerminalFontSizeChanged(double value) { _settingsService.Settings.TerminalFontSize = value; _settingsService.NotifyChanged(); }
    partial void OnEditorFontFamilyChanged(string value) { _settingsService.Settings.EditorFontFamily = value; _settingsService.NotifyChanged(); }
    partial void OnEditorFontSizeChanged(double value) { _settingsService.Settings.EditorFontSize = value; _settingsService.NotifyChanged(); }
    partial void OnThemeChanged(string value) { _settingsService.Settings.Theme = value; _settingsService.NotifyChanged(); }

    partial void OnSelectedShellChanged(string value)
    {
        IsCustomShell = value == CustomShellOption;
        if (IsCustomShell)
        {
            _settingsService.Settings.DefaultShellName = "";
        }
        else
        {
            _settingsService.Settings.DefaultShellName = value;
            _settingsService.Settings.CustomShellPath = "";
            _settingsService.Settings.CustomShellArgs = "";
        }
        _settingsService.NotifyChanged();
    }

    partial void OnCustomShellPathChanged(string value) { _settingsService.Settings.CustomShellPath = value; _settingsService.NotifyChanged(); }
    partial void OnCustomShellArgsChanged(string value) { _settingsService.Settings.CustomShellArgs = value; _settingsService.NotifyChanged(); }
}
