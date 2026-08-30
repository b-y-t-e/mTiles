using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Shells;
using mTiles.Services.Speech;

namespace mTiles.ViewModels;

public partial class TerminalTileViewModel : ObservableObject, IBusyTile, ICustomBackgroundTile,
    ITileActions, ITextInputTile, IProcessTile
{
    /// <inheritdoc />
    /// <remarks>Virtual for the one kind that is this tile with a different source of scripts — see
    /// <see cref="AgentTileViewModel"/>. Everything a shell tile does, an agent tile does identically;
    /// what differs is where its commands come from and what it calls itself in the layout.</remarks>
    public virtual string KindId => TileKindIds.Terminal;

    public string WorkingDirectory { get; }
    public ShellInstallation Shell { get; }

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

    /// <summary>What this tile was created to run.</summary>
    /// <remarks>Nothing but a bare interactive shell for a shell tile — the scripts that used to come
    /// from a profile are an agent's own commands now, and an agent tile answers with them by overriding
    /// <see cref="ResolveCurrentScripts"/>. Kept as a constructor argument because a test drives the
    /// launch chain through it.</remarks>
    private readonly LaunchScripts _ownScripts;

    /// <summary>A command typed into this tile once, at its first launch, and never again.</summary>
    /// <remarks>What puts one here is the install command an agent's <c>InstallPlan</c> names, agreed
    /// to once in a dialog that showed it. <see cref="ResolveCurrentScripts"/> <em>consumes</em> it, so
    /// Restart shell — a button that promises a fresh shell and nothing else — does not run somebody's
    /// package manager a second time. Saving it was already refused; being asked at every launch is the
    /// same grant by the other route.</remarks>
    private string? _pendingStartupScript;

    [ObservableProperty]
    private string _fontFamily;

    [ObservableProperty]
    private double _fontSize;


    [ObservableProperty]
    private TerminalTheme _theme;

    /// <summary>
    /// Why this tile is not running anything, in words the user can act on — empty while there is no
    /// such reason, which is every ordinary launch.
    /// </summary>
    /// <remarks><b>A launch that cannot be made as configured is refused rather than made differently.</b>
    /// The one thing that sets this today is an agent instance asking for a model that cannot be
    /// resolved: starting anyway would run the session on some other model without saying so, which is
    /// the whole reason <c>AiModelChoice.FirstLoaded</c> exists. Written by
    /// <see cref="PrepareForLaunchAsync"/>, read by <c>TileLauncher</c> — which starts nothing while it
    /// is set — and shown over the tile's own content by <c>TerminalTileView</c>.</remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLaunchProblem))]
    private string _launchProblem = "";

    /// <summary>Whether there is a reason to show instead of a terminal.</summary>
    /// <remarks>A property rather than a converter on the string: two views would otherwise each spell
    /// "is this empty" for themselves.</remarks>
    public bool HasLaunchProblem => LaunchProblem.Length > 0;

    /// <summary>
    /// What this tile had to be launched <em>as</em>, when that is not what it was configured as.
    /// </summary>
    /// <remarks><b>Not a <see cref="LaunchProblem"/>: this one launches.</b> The difference is whether
    /// there is a way to carry on that is faithful to what the user asked for — a model that cannot be
    /// resolved has none, while an agent instance that has been deleted leaves a tile that can still run
    /// its agent. Set once, when the tile is built (<c>AgentTileKind</c>), rather than at every launch:
    /// it is an answer about the layout, not about the session, and a line that came back on every
    /// restart would be a warning nobody could put down. Dismissible for the same reason — the tile
    /// underneath is running.</remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLaunchNotice))]
    private string _launchNotice = "";

    /// <summary>Whether there is something to say above a tile that is nonetheless working.</summary>
    public bool HasLaunchNotice => LaunchNotice.Length > 0;

    /// <summary>Puts the notice away. It is not asked again, and nothing about the tile changes.</summary>
    [RelayCommand]
    private void DismissLaunchNotice() => LaunchNotice = "";

    private readonly SettingsService _settingsService;

    /// <summary>The tile's "working" light. Owned here, driven by the terminal's output, and reported
    /// through <see cref="IsBusy"/> — the tile knows that it has one, not how it decides.</summary>
    private readonly OutputActivityLight _activityLight = new();

    /// <summary>Whether the shell in this tile is producing output — what the workspace list shows as
    /// "working".</summary>
    public bool IsBusy => _activityLight.IsOn;

    /// <summary>The shell this tile is running right now, or zero when it is running nothing.</summary>
    /// <remarks>Written from the pty's own callbacks, which are not the UI thread — hence
    /// <see cref="Volatile"/> either side rather than an <c>[ObservableProperty]</c>. Zero rather than a
    /// nullable field so both readings are a single word.</remarks>
    private int _childProcessId;

    /// <inheritdoc />
    public int? ChildProcessId => Volatile.Read(ref _childProcessId) is var id && id != 0 ? id : null;

    /// <summary>Takes note of the shell a new session just started.</summary>
    internal void TrackChildProcess(int processId) => Volatile.Write(ref _childProcessId, processId);

    /// <summary>
    /// Forgets a shell that has exited — but only if it is still the one this tile is running.
    /// </summary>
    /// <remarks>Compared rather than cleared outright: a relaunch starts the next shell and the one that
    /// died is reported afterwards, and an unconditional clear would then blank the id of the session
    /// that is very much alive. A pid is reused by the operating system, so an id nobody clears is worse
    /// than none: it is a number that has since become somebody else's process.</remarks>
    internal void ForgetChildProcess(int processId) =>
        Interlocked.CompareExchange(ref _childProcessId, 0, processId);

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

    /// <summary>Which launch of this tile is the current one, and whether the tile is still there to
    /// launch into at all.</summary>
    /// <remarks>A counter rather than a flag, because "is this still my launch" has two ways of being
    /// false and both are reachable while a launch is waiting on something slow: the tile was closed,
    /// or the user pressed Restart shell. Written from the dispatcher only, but read through
    /// <see cref="Volatile"/> so an awaited continuation cannot see a stale word.</remarks>
    private int _launchGeneration;

    private volatile bool _disposed;

    /// <summary>Claims this tile for a launch that is starting now, and gives it the token it is known
    /// by. Every earlier launch stops being the current one at this call.</summary>
    internal int BeginLaunch() => Interlocked.Increment(ref _launchGeneration);

    /// <summary>
    /// Whether the launch that took <paramref name="generation"/> may still start a session in this
    /// tile.
    /// </summary>
    /// <remarks>Asked after anything a launch awaits, which is the whole point: an agent's preparation
    /// is a real model call taking up to a minute, and in that window the tile can be closed or
    /// relaunched. Without this, a cancelled preparation still finished normally, started a session in a
    /// terminal that had already been disposed of, and left the chain owning a tile whose
    /// <see cref="Dispose"/> had already run — a shell nothing can ever stop. Two launches at once is
    /// the same fault by the other route.</remarks>
    internal bool IsCurrentLaunch(int generation) =>
        !_disposed && Volatile.Read(ref _launchGeneration) == generation;

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

    public TerminalTileViewModel(string workingDirectory, ShellInstallation? shell, SettingsService settingsService,
        LaunchScripts? scripts = null, Func<string>? tileId = null, string? oneTimeStartup = null)
    {
        _pendingStartupScript = string.IsNullOrWhiteSpace(oneTimeStartup) ? null : oneTimeStartup;
        _settingsService = settingsService;
        _tileId = tileId;
        var s = _settingsService.Settings;
        WorkingDirectory = workingDirectory;
        Shell = shell ?? ShellTerminalCatalog.ResolveDefault(s);
        _ownScripts = scripts ?? LaunchScripts.None;
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

    /// <summary>What this tile launches, asked at every launch rather than captured once.</summary>
    /// <remarks>A shell tile runs a shell; the question only has an interesting answer for an agent
    /// tile, which overrides this so that an instance edited in Settings takes effect on the next
    /// restart.</remarks>
    public virtual LaunchScripts ResolveCurrentScripts()
    {
        if (_pendingStartupScript is not { } once) return _ownScripts;
        _pendingStartupScript = null;
        return _ownScripts with { Startup = once };
    }

    /// <summary>
    /// The variables this tile's commands run with, where a <c>null</c> value <b>unsets</b> one.
    /// </summary>
    /// <remarks>Nothing for a shell — a tile the user opened runs in the environment they have — and
    /// what an agent tile puts here is a provider's address and key. That route rather than the startup
    /// script, because a script is typed into a live prompt and lands in the scrollback and in the
    /// shell's history file.</remarks>
    public virtual IReadOnlyDictionary<string, string?>? LaunchEnvironment => null;

    /// <summary>
    /// Anything that has to happen <em>before</em> the tile's commands are resolved, on the launch
    /// that is about to run.
    /// </summary>
    /// <remarks>Nothing for a shell, which is why this answers with a completed task and
    /// <see cref="TileLauncher"/> carries straight on when it does. What needs it is an agent whose
    /// conversation has to be brought into being before the command that resumes it can be written —
    /// agy's pre-create — and doing that here rather than in the launcher keeps the launcher from
    /// knowing which kinds of tile have sessions.</remarks>
    public virtual Task PrepareForLaunchAsync() => Task.CompletedTask;

    /// <summary>The tile's commands have been started.</summary>
    /// <param name="startedAt">When, which is what a capture that reads a file left behind needs in
    /// order not to adopt a session some other tile created earlier.</param>
    public virtual void OnLaunched(DateTimeOffset startedAt) { }

    /// <summary>What a derived tile has to let go of, before the shell underneath it is ended.</summary>
    /// <remarks>A hook rather than a virtual <see cref="Dispose"/>: the order of the steps below is the
    /// whole of why this method is careful, and an override that forgot to call the base would take a
    /// tile's shell with it. Guarded with the rest, so a derived tile's failure cannot cost the
    /// terminal its disposal.</remarks>
    protected virtual void OnDisposing() { }

    public void Dispose()
    {
        // First of all, and before anything that can be awaited elsewhere: a launch still waiting on
        // its preparation asks this before it starts anything, and a tile that is going away must
        // answer no from the moment it starts going rather than from the moment it has gone.
        _disposed = true;

        Attempt(OnDisposing, "Tearing down the tile's own work failed");
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
