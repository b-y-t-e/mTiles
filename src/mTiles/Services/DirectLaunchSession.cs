using System.Diagnostics;
using mTiles.Models;
using Terminal.Avalonia;

namespace mTiles.Services;

/// <summary>
/// Runs a profile's command chain in one terminal and keeps it alive: startup command, then the
/// fallback, then a plain interactive shell, and a relaunch when the tool that took exits.
/// <para>An instance owns exactly one tile's chain, and owning it is the point. Whoever replaces the
/// chain — a restart, a tile closing — disposes this first; without that the old chain relaunches into
/// the session the new one just opened, or resurrects a shell the user closed as an orphan with no UI.
/// </para>
/// </summary>
public sealed class DirectLaunchSession : IDisposable
{
    /// <summary>
    /// How long the chain waits before deciding things. Separated out so a test can drive the whole
    /// chain in milliseconds instead of sleeping through it — waiting out the real values is the
    /// difference between covering the relaunch rules and not covering them at all.
    /// </summary>
    /// <param name="FallbackTimeout">A command that dies inside this window is treated as "didn't
    /// work" — e.g. <c>claude --continue</c> with no session to continue — and the chain moves on.</param>
    /// <param name="MinLifetimeForRelaunch">A session that lasted less than this, measured from the
    /// moment its command was started, is a tool crash-looping rather than a user quitting: relaunching
    /// it would spin forever, so the tile is left dead instead.</param>
    /// <param name="Retry">Pause between one command failing and the next being tried.</param>
    /// <param name="Relaunch">Pause before a watched session that ended is started again.</param>
    public sealed record Timings(int FallbackTimeout, int MinLifetimeForRelaunch, int Retry, int Relaunch)
    {
        public static readonly Timings Default = new(FallbackTimeout: 5000, MinLifetimeForRelaunch: 10_000,
            Retry: 200, Relaunch: 500);
    }

    private readonly TerminalControl _terminal;
    private readonly string _workingDir;
    private readonly ShellProfile _shell;
    private readonly IReadOnlyList<string> _commands;
    private readonly bool _autoRelaunch;
    private readonly Timings _timings;

    /// <summary>Cancelled by <see cref="Dispose"/>. Every wait in the chain takes it, so stopping is
    /// immediate rather than "at the next checkpoint" — a chain in its five-second verdict wait would
    /// otherwise hold the tile's teardown for that long.</summary>
    private readonly CancellationTokenSource _stopped = new();

    /// <summary>The session this chain is watching, or 0 when it watches none. Compared against every
    /// exit report: a session it did not start is somebody else's business.</summary>
    private int _watchedSession;

    /// <summary>When the watched session's command was started — the start of the command, not the
    /// point at which it was judged to have worked. Timing it from the verdict would quietly make the
    /// crash-loop threshold <see cref="Timings.MinLifetimeForRelaunch"/> plus
    /// <see cref="Timings.FallbackTimeout"/>.</summary>
    private long _watchedSessionStarted;

    private bool _disposed;

    private DirectLaunchSession(TerminalControl terminal, string workingDir, ShellProfile shell,
        IReadOnlyList<string> commands, bool autoRelaunch, Timings timings)
    {
        _timings = timings;
        _terminal = terminal;
        _workingDir = workingDir;
        _shell = shell;
        _commands = commands;
        _autoRelaunch = autoRelaunch;
    }

    /// <summary>Starts the chain and returns the handle that owns it. Dispose it to stop relaunching.</summary>
    public static DirectLaunchSession Start(TerminalControl terminal, string workingDir, ShellProfile shell,
        string? startupScript, string? fallbackScript, string tileId, bool autoRelaunch = true,
        Timings? timings = null)
    {
        var session = new DirectLaunchSession(terminal, workingDir, shell,
            BuildCommands(startupScript, fallbackScript, tileId), autoRelaunch, timings ?? Timings.Default);
        _ = session.RunGuardedAsync();
        return session;
    }

    internal static IReadOnlyList<string> BuildCommands(string? startupScript, string? fallbackScript, string tileId)
    {
        var commands = new List<string>();
        if (!string.IsNullOrWhiteSpace(startupScript))
            commands.Add(startupScript.Trim().Replace("${tileId}", tileId));
        if (!string.IsNullOrWhiteSpace(fallbackScript))
            commands.Add(fallbackScript.Trim().Replace("${tileId}", tileId));
        return commands;
    }

    private async Task RunGuardedAsync()
    {
        // Nothing awaits this chain — it is what the tile does on its own — so an exception here would
        // otherwise surface as an unobserved task, or take the dispatcher down from the relaunch path.
        try
        {
            await RunAsync(_stopped.Token);
        }
        catch (OperationCanceledException)
        {
            // Disposed mid-chain. Expected, and not a failure of anything.
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Direct launch chain failed: {0}", ex);
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        foreach (var command in _commands)
        {
            var (exe, args) = _shell.CommandLine(command);
            long started = Environment.TickCount64;
            await ShellStarter.StartAsync(_terminal, _workingDir, exe, args, cancellationToken: stop);

            if (!await DiedQuicklyAsync(stop))
            {
                Watch(started);    // it took — this is the tile's tool now
                return;
            }

            await Task.Delay(_timings.Retry, stop);
        }

        // Nothing in the chain survived: fall back to a plain interactive shell — and don't watch it.
        // An interactive shell that exits is the user typing `exit`, which must not be undone.
        await ShellStarter.StartAsync(_terminal, _workingDir, _shell.ExecutablePath, _shell.Args,
            cancellationToken: stop);
    }

    /// <summary>
    /// True when the command died inside the fallback window; false when it is still
    /// running, which is this chain's definition of "it worked".
    /// <para>A stop is neither: it propagates. Answering "it worked" to a cancelled wait is how a
    /// disposed chain would go on to watch — and relaunch — the session it was just told to let go.</para>
    /// </summary>
    private async Task<bool> DiedQuicklyAsync(CancellationToken stop)
    {
        using var timeout = new CancellationTokenSource(_timings.FallbackTimeout);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(stop, timeout.Token);
        try
        {
            await _terminal.WhenNotRunningAsync(either.Token);
            return true;
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            return false;   // our own timeout ran out, i.e. the command outlived it
        }
    }

    private void Watch(long commandStarted)
    {
        if (!_autoRelaunch || _disposed) return;

        _watchedSession = _terminal.SessionId;
        _watchedSessionStarted = commandStarted;
        _terminal.Exited += OnExited;
    }

    private void OnExited(object? sender, SessionExitedEventArgs e)
    {
        // Not the session this chain started: a restart's kill, or a session opened by whoever replaced
        // us. Relaunching on it is how one tile ends up running two competing chains.
        if (e.SessionId != _watchedSession) return;

        _terminal.Exited -= OnExited;
        _watchedSession = 0;

        if (Environment.TickCount64 - _watchedSessionStarted < _timings.MinLifetimeForRelaunch)
            return;

        _ = RelaunchAsync();
    }

    private async Task RelaunchAsync()
    {
        try
        {
            await Task.Delay(_timings.Relaunch, _stopped.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_terminal.IsDisposed) return;
        await RunGuardedAsync();
    }

    /// <summary>Stops the chain: no more relaunches, and any wait in flight is abandoned at once.
    /// Idempotent, and safe on a terminal that is already gone.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _terminal.Exited -= OnExited;
        _watchedSession = 0;
        // Cancelled, deliberately not disposed. A relaunch already in flight reads this token again
        // after its delay, and both `Token` and `CreateLinkedTokenSource` throw once the source has been
        // disposed — an ObjectDisposedException logged as a chain failure, instead of the quiet stop
        // this is. Nothing is registered on it that would need releasing.
        _stopped.Cancel();
    }
}
