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
    private TerminalTheme _theme;

    private readonly SettingsService _settingsService;

    internal Control? CachedControl { get; private set; }
    internal bool IsLaunched { get; set; }

    /// <summary>
    /// Takes ownership of the terminal the view built for this tile: the tile keeps it across view
    /// rebuilds and workspace switches, and ends it in <see cref="Dispose"/>.
    /// <para>Registration for window-level Ctrl+C happens here rather than in the view so that it is
    /// symmetric with the unregistration on disposal — one object, one owner, one pair of calls.</para>
    /// </summary>
    internal void AttachControl(Terminal.Avalonia.TerminalControl terminal)
    {
        CachedControl = terminal;
        TerminalClipboardCoordinator.Register(terminal);
    }

    /// <summary>The launch that currently owns this tile's terminal, when the profile runs a command
    /// chain. Private because the invariant is "at most one, and the old one is stopped first" — an
    /// assignment from outside can only break it, and a chain nobody stopped goes on relaunching into
    /// a terminal that has moved on.</summary>
    private IDisposable? _launchSession;

    /// <summary>Hands this tile over to a new launch, stopping whatever was running it. Pass null to
    /// stop without starting anything.</summary>
    internal void ReplaceLaunchSession(IDisposable? launch)
    {
        _launchSession?.Dispose();
        _launchSession = launch;
    }

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
        // Before the terminal goes: disposing it kills the child, and a launch chain still watching
        // would answer that exit by starting a shell nothing can ever show or close again.
        ReplaceLaunchSession(null);

        if (CachedControl is Terminal.Avalonia.TerminalControl tc)
        {
            TerminalClipboardCoordinator.Unregister(tc);
            // Dispose, not Kill: Kill ends the child but leaves the control's timers and session state
            // behind. The control is not reusable afterwards, which is right — this tile is gone.
            try { tc.Dispose(); }
            catch (Exception ex)
            {
                // Swallowed so closing a tile always closes it — but never silently: this is the call
                // that ends the child, so a failure here means a shell still running with no UI left to
                // reach it, and the log is the only place that would ever say so.
                System.Diagnostics.Trace.TraceWarning(
                    "Terminal dispose failed; the shell for tile {0} may be orphaned: {1}", TileId, ex);
            }
        }
        CachedControl = null;
    }
}
