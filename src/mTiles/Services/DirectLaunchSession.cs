using System.Diagnostics;
using Avalonia.Threading;
using mTiles.Models;
using mTiles.Services.Shells;
using Terminal.Avalonia;

namespace mTiles.Services;

/// <summary>
/// Runs a profile's command chain in one terminal and keeps it alive: startup command, then the
/// fallback, then a plain interactive shell, and a relaunch when the tool that took exits.
/// <para>An instance owns exactly one tile's chain, and owning it is the point. Whoever replaces the
/// chain — a restart, a tile closing — disposes this first; without that the old chain relaunches into
/// the session the new one just opened, or resurrects a shell the user closed as an orphan with no UI.
/// </para>
/// <para>The chain is one loop that starts a command and waits for it to end, which
/// <see cref="TerminalControl.WhenSessionEndedAsync"/> makes possible by reporting the exit code of a
/// <em>named</em> session. Subscribing to an event after starting cannot be made correct from here:
/// the command can end in the gap, and a report already delivered is one no subscription will see.</para>
/// </summary>
internal sealed class DirectLaunchSession : IDisposable
{
    private readonly TerminalControl _terminal;
    private readonly string _workingDir;
    private readonly ShellInstallation _shell;

    private readonly IReadOnlyList<string> _commands;
    private readonly ChainPolicy _policy;

    /// <summary>The variables every command in this chain runs with, where a <c>null</c> value unsets
    /// one. The route a provider's key takes: a startup script is typed into a live prompt, so it lands
    /// in the scrollback and in the shell's history file, and a key must never go that way.</summary>
    private readonly IReadOnlyDictionary<string, string?>? _environment;

    /// <summary>Cancelled by <see cref="Dispose"/>. Every wait in the chain takes it, so stopping is
    /// immediate rather than "at the next checkpoint" — a chain waiting on a tool that runs for hours
    /// would otherwise hold the tile's teardown for exactly that long.</summary>
    private readonly CancellationTokenSource _stopped = new();

    private DirectLaunchSession(TerminalControl terminal, string workingDir, ShellInstallation shell,
        IReadOnlyList<string> commands, ChainPolicy policy,
        IReadOnlyDictionary<string, string?>? environment)
    {
        _environment = environment;
        policy.Validate();
        _policy = policy;
        _terminal = terminal;
        _workingDir = workingDir;
        _shell = shell;
        _commands = commands;
    }

    /// <summary>Starts the chain and returns the handle that owns it. Dispose it to stop relaunching.</summary>
    public static DirectLaunchSession Start(TerminalControl terminal, string workingDir, ShellInstallation shell,
        LaunchScripts scripts, string tileId, ChainPolicy? policy = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        // Here, where the caller can see it. The chain reaches the control's own thread check only
        // inside a task nobody awaits, so a call from the wrong thread would be caught, traced, and
        // presented to the user as a tile that simply never does anything.
        Dispatcher.UIThread.VerifyAccess();

        var commands = BuildCommands(scripts, tileId);

        var session = new DirectLaunchSession(terminal, workingDir, shell,
            commands, policy ?? ChainPolicy.Default, environment);
        _ = session.RunGuardedAsync();
        return session;
    }

    /// <summary>
    /// The commands to run, in order. It takes <see cref="LaunchScripts"/> rather than two strings
    /// because "blank is no script" is that type's rule to keep — a second copy of it here is a second
    /// chance for the two to disagree, which is the bug the type was introduced to end.
    /// <para><b>A multi-line script is one command, not several.</b> It goes to the shell whole, as
    /// <c>shell -c "line1\nline2"</c>, and every shell in <c>ShellTerminalCatalog</c> treats the newline
    /// as a separator, so a <c>cd</c> on one line affects the next. That is not a general property of
    /// shells — it is why the one that does not is not in the catalog.</para>
    /// <para>The interactive path does the opposite — <see cref="ShellStarter.BuildStartupInput"/> types
    /// one line at a time — because there a person's keyboard is being simulated at a live prompt. The
    /// asymmetry is real and deliberate.</para>
    /// </summary>
    internal static IReadOnlyList<string> BuildCommands(LaunchScripts scripts, string tileId)
    {
        var commands = new List<string>();
        if (scripts.Startup is { } startup)
            commands.Add(TileScript.Resolve(startup.Trim(), tileId));
        if (scripts.Fallback is { } fallback)
            commands.Add(TileScript.Resolve(fallback.Trim(), tileId));
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
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // The tile went away mid-chain: either this was disposed (cancellation) or the terminal was
            // (a start or a wait racing the close). Both are the ordinary way a chain ends and neither
            // is a failure — logging them as one buries the warnings that do mean something.
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Direct launch chain failed: {0}", ex);
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        // One budget for the whole chain, never reset by anything the chain itself does — see
        // RelaunchBudget for why a per-command one is not a bound at all.
        var budget = new RelaunchBudget(_policy.MaxRelaunches, _policy.RelaunchWindow);

        for (int index = 0; index < _commands.Count;)
        {
            // Checked each turn, not only after a failure: the tile can close at any point in a chain
            // that runs for as long as the user's tool does, and starting a session in a disposed
            // control throws.
            if (_terminal.IsDisposed) return;

            var command = _commands[index];
            var (exe, args) = _shell.CommandLineFor(command);
            int session;
            try
            {
                // The id comes from the start itself. Reading SessionId afterwards answers "what is
                // running now", and what this chain needs is "what did I start" — the two differ the
                // moment anything else touches the terminal.
                session = await ShellStarter.StartAsync(_terminal, _workingDir, exe, args,
                    environment: _environment, cancellationToken: stop);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or ObjectDisposedException))
            {
                // A spawn fails for ordinary reasons — a tool that is not installed, a working directory
                // that has gone — and that is precisely what the next link in the chain is for. Letting
                // it out would abandon the tile instead of falling back, which is the opposite of what
                // the chain exists to provide.
                // Not a disposed terminal, though: that is the tile going away, and every command after
                // this one would fail the same way. The whole exception, not just its message — the
                // type and the stack are what tell a bad path apart from a bad profile.
                // The position in the chain, never the command itself. A profile's startup script is
                // where people keep API tokens, and these logs sit in %APPDATA% for a week; the profile
                // is on disk anyway, so "command 1 of 2" is as much as anyone needs to look it up.
                Trace.TraceWarning("Launch of command {0} of {1} failed, trying the next: {2}",
                    index + 1, _commands.Count, ex);
                await Task.Delay(_policy.Retry, stop);   // as on every other move down the chain
                index++;
                continue;
            }

            // No timeout, and none needed: this waits for *that* session's exit code, however long the
            // command runs. Deciding after a few seconds that a command had "worked" was what made a
            // 21-second failure indistinguishable from a tool the user was happily using.
            var exit = await _terminal.WhenSessionEndedAsync(session, stop);
            // From the report, not from a clock around this await. The terminal stamps the session when
            // it spawns the child; timing it here measures the wait instead — the dispatcher's backlog
            // included, and the previous session's teardown too if the clock starts a line too early.
            long lived = (long)exit.Lifetime.TotalMilliseconds;

            // Somebody else owns this terminal now. Nothing is known about how the command would have
            // ended — it was not allowed to end — so there is no verdict to draw, and starting the next
            // command would land it in a session belonging to whoever took over.
            // A second line of defence, not the barrier: the barrier is that whoever replaces a chain
            // disposes it first (TerminalTileViewModel.ReplaceLaunchSession). A kill reports its own
            // exit before the replacement is published, so which of the two signals arrives first is a
            // race — this catches the half of it that is observable, and ownership catches all of it.
            if (exit.Reason == SessionEndReason.Replaced || _terminal.SessionId != session)
            {
                Trace.TraceInformation(
                    "Launch chain for '{0}' stopped: the terminal was taken over by another session.",
                    command);
                return;
            }

            // What the outcome means is the policy's business; this loop only carries it out. The budget
            // is the one thing decided here, because "may I do that again" is not a property of the
            // outcome — spending it is what stops a rule that is right in the small from looping in
            // the large.
            var step = _policy.Decide(exit.ExitCode, lived);
            // Out of budget: carry on down the chain. The profile's fallback is the author's own
            // recovery and deserves its turn — and it is safe to give it one, because the budget
            // belongs to the chain and not to the command, so walking to the fallback and back to the
            // top cannot renew it. Traced, because a tile quietly deciding to stop relaunching the
            // thing the user configured is exactly the kind of silence that took weeks to explain last
            // time.
            if ((step is ChainStep.Relaunch or ChainStep.RestartChain)
                && _policy.CountsAgainstBudget(exit.ExitCode, lived)
                && !budget.TrySpend())
            {
                Trace.TraceWarning(
                    "Launch chain gave up relaunching command {0} of {1} ({2} in {3}ms); moving on.",
                    index + 1, _commands.Count, _policy.MaxRelaunches, _policy.RelaunchWindow);
                step = ChainStep.NextCommand;
            }

            await Task.Delay(step is ChainStep.NextCommand ? _policy.Retry : _policy.Relaunch, stop);
            index = step switch
            {
                ChainStep.Relaunch => index,        // the same command: it had been working until it wasn't
                ChainStep.RestartChain => 0,        // afresh: the profile's first choice may work again now
                ChainStep.NextCommand => index + 1, // onwards, never back — falling back rather than looping
                _ => throw new InvalidOperationException($"Unhandled chain step {step}."),
            };
        }

        // Nothing in the chain worked: fall back to a plain interactive shell, and do not watch it.
        // An interactive shell that exits is the user typing `exit`, which must not be undone — so this
        // is also where the loop ends for good rather than turning into another attempt.
        if (_terminal.IsDisposed) return;
        try
        {
            await ShellStarter.StartAsync(_terminal, _workingDir, _shell.ExecutablePath,
                _shell.InteractiveArgs, environment: _environment, cancellationToken: stop);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or ObjectDisposedException))
        {
            // The last resort failed too — the shell itself will not start. Every earlier failure had
            // somewhere to go; this one has nowhere, so it is an error rather than a warning, and it
            // names what was tried: a profile pointing at a shell that was uninstalled and a working
            // directory that has gone look identical from the tile, which shows whatever the previous
            // command left on screen — or nothing at all, if the first command failed to spawn too.
            Trace.TraceError("The tile has no shell: starting '{0}' {1} in '{2}' failed: {3}",
                _shell.ExecutablePath, string.Join(' ', _shell.InteractiveArgs), _workingDir, ex);
        }
    }

    /// <summary>Stops the chain: no more relaunches, and any wait in flight is abandoned at once.
    /// Idempotent, and safe on a terminal that is already gone.</summary>
    public void Dispose()
    {
        // Cancelled, deliberately not disposed. A wait already in flight reads this token again after
        // resuming, and both `Token` and `CreateLinkedTokenSource` throw once the source has been
        // disposed — an ObjectDisposedException logged as a chain failure, instead of the quiet stop
        // this is. Nothing is registered on it that would need releasing.
        _stopped.Cancel();
    }
}
