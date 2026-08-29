using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Speech;

namespace mTiles.ViewModels;

public partial class TerminalTileViewModel : ObservableObject, IBusyTile, ICustomBackgroundTile,
    ITileActions, ITextInputTile
{
    /// <inheritdoc />
    public string KindId => TileKindIds.Terminal;

    public string WorkingDirectory { get; }
    public ShellProfile Shell { get; }
    public string? UserProfileId { get; }

    /// <summary>Where the tile's identity is read from — see <see cref="TileId"/>.</summary>
    private readonly Func<string>? _tileId;
    /// <summary>
    /// The tile's persistent identity, which <c>${tileId}</c> in a profile script resolves to.
    /// </summary>
    /// <remarks>
    /// <para><b>Read through a function, and not stored here.</b> The id belongs to the
    /// <see cref="LeafTileNodeViewModel"/> that owns this content, and it moves under this object: "New
    /// session" replaces it while this terminal keeps running. While it was a settable property, four
    /// places had to cast the content back to a terminal and push the new value in — the serializer,
    /// both creation paths and the drag-and-drop swap — and any one of them forgotten meant a tile
    /// launching under somebody else's session id.</para>
    /// <para>The function answers for the tile it was built for, so this content must never be handed to
    /// a different one: dragging one tile onto another exchanges the two tiles' places in the tree
    /// rather than their contents, which is what keeps that true.</para>
    /// <para>Empty when nothing supplies one, which in practice means a test. The hazard that leaves —
    /// a blank id turning <c>claude -r ${tileId}</c> into a different command — is closed at the point
    /// of use in <see cref="TileScript.Resolve"/>.</para>
    /// </remarks>
    public string TileId => _tileId?.Invoke() ?? "";

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
        LaunchScripts? scripts = null, string? userProfileId = null, Func<string>? tileId = null)
    {
        _settingsService = settingsService;
        _tileId = tileId;
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

    /// <summary>How far the terminal's text sits inside the card.</summary>
    /// <remarks>A terminal is text against an edge and wants the gap; every other tile's content is its
    /// own chrome and runs to the card's edge instead.</remarks>
    public Thickness ContentInset { get; } = new(6, 0, 6, 6);

    /// <summary>What that gap is painted in — the terminal's own ANSI background.</summary>
    /// <remarks>A literal hex rather than a UI role token, because the palette does not derive this
    /// one: the inset has to be the colour of the thing inside it or it reads as a frame.</remarks>
    public string ContentBackground => Theme.Background;

    partial void OnThemeChanged(TerminalTheme value) => OnPropertyChanged(nameof(ContentBackground));

    /// <summary>What this tile offers its header.</summary>
    /// <remarks>
    /// <para>Restarting is here rather than on the tile that owns it because it is a thing done to a
    /// shell, and this is the object that has one. It is what removed the last cast back to this class
    /// from <see cref="LeafTileNodeViewModel"/>.</para>
    /// <para><b>It is destructive, so a phone is not offered it.</b> Restarting kills whatever the
    /// shell is running — a build, an agent halfway through a task — which is the definition
    /// <see cref="TileAction.IsDestructive"/> carries, and it is why the header asks
    /// <c>Restart shell?</c> before doing it. That question is the whole reason the flag belongs here:
    /// with it unset the same action went out to a paired device where nothing asked anything and
    /// nobody could see what was about to die, so one screen guarded it and the other did not. A phone
    /// cannot be shown what it would cost, and confirming there what you cannot see is theatre — so it
    /// is withheld rather than confirmed, which is the rule the whole action set is filtered by
    /// (<c>PhoneTileActions</c>).</para>
    /// <para><b>New session is deliberately not here.</b> It replaces the tile's persistent identity —
    /// the session an agent would otherwise resume — which belongs to the tile rather than to its
    /// content, and it is not something to do from a screen that cannot show which conversation is
    /// about to be left behind.</para>
    /// </remarks>
    public IReadOnlyList<TileAction> Actions =>
    [
        new(TileActionIds.Restart, "Restart shell", "restart",
            IsEnabled: CachedControl is Terminal.Avalonia.TerminalControl,
            IsDestructive: true),
    ];

    /// <inheritdoc />
    public Task<TileActionResult> InvokeAsync(string id)
    {
        if (id != TileActionIds.Restart)
            return Task.FromResult(TileActionResult.Refused($"This tile has no '{id}'."));

        if (CachedControl is not Terminal.Avalonia.TerminalControl terminal)
            return Task.FromResult(TileActionResult.Refused("This tile has no terminal to restart."));

        // No Kill() first. Not because it would stall — the restart kills the child itself, and that
        // call blocks the UI thread for as long as the child takes either way — but because killing
        // here races the restart: it would leave the launcher waiting on a session that had already
        // gone, and the previous chain seeing an exit it is entitled to relaunch. Sequencing the kill,
        // the wait and the start is precisely what RestartAsync exists for, and it serialises
        // overlapping restarts on top.
        TileLauncher.Launch(terminal, this);
        return Task.FromResult(TileActionResult.Ok);
    }

    /// <summary>Types text into the shell, submitting it when the caller asked for that.</summary>
    /// <remarks>
    /// A shell that has exited is refused rather than typed at: text sent to it goes nowhere, and saying
    /// so is the difference between the phone showing a reason and the user pressing again.
    /// <para>The carriage return is put on by <see cref="DictationTextSink.Type"/> and only when asked.
    /// A transcript has already had every control character turned into a space before it arrives, so
    /// that <c>\r</c> is the one that can reach the child, and it is there because the user asked for it
    /// rather than because a model heard a newline. Which is why it is not written out again here: that
    /// one line is the only branch in the whole feature that can <em>run a command</em>, and it is split
    /// from resolving the destination precisely so it can be tested without a shell. A second copy of it
    /// living on this class would be the copy nothing covers.</para>
    /// </remarks>
    public bool TrySendText(string text, bool submit) =>
        LiveTerminal is { } terminal && DictationTextSink.Type(terminal.SendText, text, submit);

    /// <summary>
    /// Presses one of the few keys something outside the tile may press.
    /// </summary>
    /// <remarks>
    /// Delivered by <see cref="TileKeyPress"/>, which owns the one map from <see cref="TileKey"/> to
    /// what a control that reads the keyboard understands — the terminal is only one of the two places
    /// a key can land, and the two must not answer differently.
    /// </remarks>
    public bool TryPressKey(TileKey key)
    {
        if (LiveTerminal is not { } terminal)
            return false;

        TileKeyPress.At(terminal, key);
        return true;
    }

    /// <summary>This tile's terminal, when there is one and its shell is still running.</summary>
    /// <remarks>A dead terminal is refused rather than written to — text sent to a shell that has
    /// exited goes nowhere, and both callers above have to be able to say so.</remarks>
    private Terminal.Avalonia.TerminalControl? LiveTerminal =>
        CachedControl is Terminal.Avalonia.TerminalControl { IsRunning: true } control ? control : null;

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
