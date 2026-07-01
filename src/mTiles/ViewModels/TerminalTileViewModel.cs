using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;
using mTiles.Services;

namespace mTiles.ViewModels;

public partial class TerminalTileViewModel : ObservableObject, IDisposable
{
    public string WorkingDirectory { get; }
    public ShellProfile Shell { get; }
    public string? StartupScript { get; }
    public string? FallbackScript { get; }
    public string? UserProfileId { get; }
    public string TileId { get; set; } = "";
    public bool IsDirectLaunch { get; }

    [ObservableProperty]
    private string _fontFamily;

    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private bool _copyOnSelect;

    [ObservableProperty]
    private TerminalTheme _theme;

    private readonly SettingsService _settingsService;

    internal Control? CachedControl { get; set; }
    internal bool IsLaunched { get; set; }

    public TerminalTileViewModel(string workingDirectory, ShellProfile? shell, SettingsService settingsService,
        string? startupScript = null, string? fallbackScript = null, string? userProfileId = null,
        bool isDirectLaunch = false)
    {
        _settingsService = settingsService;
        var s = _settingsService.Settings;
        WorkingDirectory = workingDirectory;
        Shell = shell ?? ShellDetector.ResolveDefault(s);
        StartupScript = string.IsNullOrWhiteSpace(startupScript) ? null : startupScript;
        FallbackScript = string.IsNullOrWhiteSpace(fallbackScript) ? null : fallbackScript;
        UserProfileId = userProfileId;
        IsDirectLaunch = isDirectLaunch;
        _theme = TerminalTheme.GetByName(s.ColorThemeName);
        _fontFamily = s.TerminalFontFamily;
        _fontSize = s.TerminalFontSize;
        _copyOnSelect = s.TerminalCopyOnSelect;

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
        if (s.TerminalCopyOnSelect != CopyOnSelect)
            CopyOnSelect = s.TerminalCopyOnSelect;
    }

    public (string? startupScript, string? fallbackScript, bool isDirectLaunch) ResolveCurrentScripts()
    {
        if (UserProfileId == null)
            return (StartupScript, FallbackScript, IsDirectLaunch);

        var profile = _settingsService.Settings.ShellProfiles
            .FirstOrDefault(p => p.Id == UserProfileId);
        if (profile == null)
            return (StartupScript, FallbackScript, IsDirectLaunch);

        var startup = string.IsNullOrWhiteSpace(profile.StartupScript) ? null : profile.StartupScript;
        var fallback = string.IsNullOrWhiteSpace(profile.FallbackScript) ? null : profile.FallbackScript;
        return (startup, fallback, !string.IsNullOrEmpty(profile.FallbackScript));
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
