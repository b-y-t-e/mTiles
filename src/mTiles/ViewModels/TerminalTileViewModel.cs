using System.Diagnostics;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;
using mTiles.Services;

namespace mTiles.ViewModels;

public partial class TerminalTileViewModel : ObservableObject, IDisposable, IBusyTile
{
    public string WorkingDirectory { get; }
    public ShellProfile Shell { get; }
    public string? UserProfileId { get; }
    /// <summary>
    /// The tile's persistent identity, which <c>${tileId}</c> in a profile script resolves to.
    /// <para>A settable property with an empty default, and not a constructor parameter, because the id
    /// belongs to the <see cref="LeafTileNodeViewModel"/> that owns this content and is stamped on it
    /// straight after <see cref="TileFactory"/> builds it — and the factory is type-agnostic, so giving
    /// it the id means giving it to every kind of tile. Worth doing deliberately, not as a side effect
    /// of something else; until then the hazard the setter leaves — a blank id silently expanding to
    /// nothing — is closed at the point of use in <see cref="TileScript.Resolve"/>.</para>
    /// </summary>
    public string TileId { get; set; } = "";

    /// <summary>What this tile was created to run. Only a fallback for when the profile it came from
    /// has since been deleted — <see cref="ResolveCurrentScripts"/> prefers the profile's current
    /// scripts, so editing a profile takes effect without recreating the tile.</summary>
    private readonly LaunchScripts _ownScripts;

    [ObservableProperty]
    private string _fontFamily;

    [ObservableProperty]
    private double _fontSize;


    [ObservableProperty]
    private TerminalTheme _theme;

    private readonly SettingsService _settingsService;

    /// <summary>The tile's "working" light. Owned here, driven by the terminal's output, and reported
    /// through <see cref="IsBusy"/> — the tile knows that it has one, not how it decides.</summary>
    private readonly OutputActivityLight _activityLight = new();

    /// <summary>Whether the shell in this tile is producing output — what the workspace list shows as
    /// "working".</summary>
    public bool IsBusy => _activityLight.IsOn;

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
        _activityLight.Attach(terminal);
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
        // Hand over first, dispose second. The other order leaves the field pointing at the old chain if
        // stopping it throws — and the tile then holds a chain it has already tried to end while the new
        // one, already started, is unreachable and can never be stopped at all.
        var previous = _launchSession;
        _launchSession = launch;

        // And the failure stops here. There is nothing a caller can do about a chain that refuses to
        // stop, and every caller is in the middle of something that must finish: a relaunch that would
        // otherwise abandon the tile without starting anything, or a tile being closed. Reported,
        // because a chain still running with nothing owning it is invisible everywhere else.
        try { previous?.Dispose(); }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Stopping the previous launch chain failed (tile {0}): {1}", TileId, ex);
        }
    }

    public TerminalTileViewModel(string workingDirectory, ShellProfile? shell, SettingsService settingsService,
        LaunchScripts? scripts = null, string? userProfileId = null)
    {
        _settingsService = settingsService;
        var s = _settingsService.Settings;
        WorkingDirectory = workingDirectory;
        Shell = shell ?? ShellDetector.ResolveDefault(s);
        _ownScripts = scripts ?? LaunchScripts.None;
        UserProfileId = userProfileId;
        _theme = TerminalTheme.GetByName(s.ColorThemeName);
        _fontFamily = s.TerminalFontFamily;
        _fontSize = s.TerminalFontSize;

        _settingsService.SettingsChanged += OnSettingsChanged;
        _activityLight.Changed += (_, _) => OnPropertyChanged(nameof(IsBusy));
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

    /// <summary>The scripts as the profile defines them <em>now</em> — edited settings take effect on
    /// the next launch, without recreating the tile.</summary>
    public LaunchScripts ResolveCurrentScripts()
    {
        if (UserProfileId == null)
            return _ownScripts;

        var profile = _settingsService.Settings.ShellProfiles
            .FirstOrDefault(p => p.Id == UserProfileId);
        if (profile == null)
            return _ownScripts;

        return LaunchScripts.FromProfile(profile.StartupScript, profile.FallbackScript);
    }

    public void Dispose()
    {
        _settingsService.SettingsChanged -= OnSettingsChanged;
        // Before the terminal goes: disposing it kills the child, and a launch chain still watching
        // would answer that exit by starting a shell nothing can ever show or close again. Guarded like
        // the steps below — a chain that fails to stop must not cost us the terminal's disposal.
        Attempt(() => ReplaceLaunchSession(null), "Stopping the launch chain failed");
        // Before the terminal is disposed of as well: its own teardown writes, and a handler still
        // attached would light a tile that is on its way out.
        Attempt(_activityLight.Dispose, "Detaching the activity watch failed");

        if (CachedControl is Terminal.Avalonia.TerminalControl tc)
        {
            // Ending the child comes first and nothing may get in front of it: this is the call that
            // stops the shell, so anything that throws earlier leaves it running with no UI left to
            // reach it. Dispose, not Kill — Kill ends the child but leaves the control's timers and
            // session state behind, and the control is not reused after this anyway.
            // Each step swallows its own failure, so no step can cost the ones after it. Silence is
            // what they must not do: an orphaned shell is invisible everywhere except the log.
            Attempt(tc.Dispose, "Ending the shell failed; it may be orphaned");
            Attempt(() => TerminalClipboardCoordinator.Unregister(tc), "Deregistering the terminal failed");
        }
        // Unconditionally, outside the type test: a tile holding a control it no longer owns would go
        // on handing it to views.
        CachedControl = null;
    }

    /// <summary>Runs one teardown step, reporting a failure instead of letting it cost the steps after
    /// it. <paramref name="whatFailed"/> is plain text, never a format string: a helper whose whole job
    /// is not to throw must not be able to throw a FormatException over a stray brace.</summary>
    private void Attempt(Action step, string whatFailed)
    {
        try { step(); }
        catch (Exception ex)
        {
            Trace.TraceWarning("{0} (tile {1}): {2}", whatFailed, TileId, ex);
        }
    }
}
