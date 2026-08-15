using System.Diagnostics;
using Avalonia.Threading;
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
/// <para>The chain is one loop that starts a command and waits for it to end, which
/// <see cref="TerminalControl.WhenSessionEndedAsync"/> makes possible by reporting the exit code of a
/// <em>named</em> session. Subscribing to an event after starting cannot be made correct from here:
/// the command can end in the gap, and a report already delivered is one no subscription will see.</para>
/// </summary>
internal sealed class DirectLaunchSession : IDisposable
{
    private readonly TerminalControl _terminal;
    private readonly string _workingDir;
    private readonly ShellProfile _shell;

    /// <summary>What the chain's commands are run by, which is <see cref="_shell"/> unless that is
    /// <c>cmd.exe</c> — see <see cref="ShellDetector.ResolveForCommands(ShellProfile)"/>. The two are
    /// separate fields because the interactive shell at the end of the chain must stay the one the user
    /// chose; only the commands move.</summary>
    private readonly ShellProfile _commandShell;

    private readonly IReadOnlyList<string> _commands;
    private readonly ChainPolicy _policy;

    /// <summary>Cancelled by <see cref="Dispose"/>. Every wait in the chain takes it, so stopping is
    /// immediate rather than "at the next checkpoint" — a chain waiting on a tool that runs for hours
    /// would otherwise hold the tile's teardown for exactly that long.</summary>
    private readonly CancellationTokenSource _stopped = new();

    private DirectLaunchSession(TerminalControl terminal, string workingDir, ShellProfile shell,
        ShellProfile commandShell, IReadOnlyList<string> commands, ChainPolicy policy)
    {
        policy.Validate();
        _policy = policy;
        _terminal = terminal;
        _workingDir = workingDir;
        _shell = shell;
        _commandShell = commandShell;
        _commands = commands;
    }

    /// <summary>Starts the chain and returns the handle that owns it. Dispose it to stop relaunching.</summary>
    public static DirectLaunchSession Start(TerminalControl terminal, string workingDir, ShellProfile shell,
        LaunchScripts scripts, string tileId, ChainPolicy? policy = null)
    {
        // Here, where the caller can see it. The chain reaches the control's own thread check only
        // inside a task nobody awaits, so a call from the wrong thread would be caught, traced, and
        // presented to the user as a tile that simply never does anything.
        Dispatcher.UIThread.VerifyAccess();

        var commands = BuildCommands(scripts, tileId);
        var commandShell = ShellDetector.ResolveForCommands(shell);
        AnnounceCommandShell(shell, commandShell, commands);

        var session = new DirectLaunchSession(terminal, workingDir, shell, commandShell,
            commands, policy ?? ChainPolicy.Default);
        _ = session.RunGuardedAsync();
        return session;
    }

    /// <summary>
    /// The commands to run, in order. It takes <see cref="LaunchScripts"/> rather than two strings
    /// because "blank is no script" is that type's rule to keep — a second copy of it here is a second
    /// chance for the two to disagree, which is the bug the type was introduced to end.
    /// <para><b>A multi-line script is one command, not several.</b> It goes to the shell whole, as
    /// <c>shell -c "line1\nline2"</c>. Whether the shell then treats the newline as a separator is the
    /// shell's business, and they differ: <c>bash</c>, <c>zsh</c> and <c>pwsh -Command</c> do, so a
    /// <c>cd</c> on one line affects the next; <b><c>cmd /c</c> does not</b> — measured, and it silently
    /// runs the first line only. A multi-line chain script is a POSIX-shell and PowerShell feature, not
    /// a general one.</para>
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

    /// <summary>
    /// Says what the chain is about to run its commands in, whenever that is not what the profile asked
    /// for — and says so louder when nothing could be found to replace <c>cmd</c> with.
    /// </summary>
    /// <remarks>
    /// <para>A <b>warning</b>, not a note, and deliberately so. The profile's shell is a setting the user
    /// made, and for a hand-written <c>cmd</c> profile the substitution is a real regression rather than
    /// a harmless improvement: <c>%VAR%</c> stops expanding, <c>set FOO=bar</c> and the other builtins
    /// are gone, and <c>&amp;&amp;</c> — which <c>cmd</c> understands — is a <em>parser error</em> in
    /// Windows PowerShell 5.1, so a command that used to work now fails before it starts. That is worth
    /// interrupting somebody's log for; the seeded AI profiles, which is what this exists for, are
    /// unaffected.</para>
    /// <para>Traced rather than shown, because it must not block a launch — but at a level that can be
    /// found, since this is where "my profile stopped working after an update" is answered.</para>
    /// </remarks>
    private static void AnnounceCommandShell(ShellProfile shell, ShellProfile commandShell,
        IReadOnlyList<string> commands)
    {
        if (shell.Type != ShellType.Cmd)
            return;

        // Asked of the two shells rather than of their identity: `ResolveForCommands` returning the very
        // same instance is how it happens to say "unchanged" today, and a future one that returned an
        // equal copy would silently turn this warning off.
        if (commandShell.Type != ShellType.Cmd)
        {
            Trace.TraceWarning(
                "This profile's shell is '{0}', which cannot run chain commands correctly, so its {1} "
                + "command(s) will be run by '{2}' instead — %VAR%, && and the cmd builtins will not "
                + "work in them. The tile's interactive shell is unchanged.",
                shell.Name, commands.Count, commandShell.Name);
            return;
        }

        // Left on cmd because there was nothing else installed. Now the old limits apply again, and the
        // multi-line one is the only one detectable by looking at a string: cmd /c runs the first line
        // and discards the rest, measured. The line count, not the lines — a profile script is where
        // people keep tokens.
        Trace.TraceWarning(
            "This profile's shell is '{0}' and no PowerShell or POSIX shell was found to run its "
            + "commands instead, so the known limits apply: `;` is not a separator, quoting differs, and "
            + "of {1} command(s) any multi-line one runs its first line only ({2}).",
            shell.Name, commands.Count,
            string.Join(", ", commands.Select((c, i) => $"#{i + 1}: {c.Split('\n').Length} line(s)")));
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
            var (exe, args) = ShellCommandLine.For(_commandShell, command);
            int session;
            try
            {
                // The id comes from the start itself. Reading SessionId afterwards answers "what is
                // running now", and what this chain needs is "what did I start" — the two differ the
                // moment anything else touches the terminal.
                session = await ShellStarter.StartAsync(_terminal, _workingDir, exe, args, cancellationToken: stop);
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
            await ShellStarter.StartAsync(_terminal, _workingDir, _shell.ExecutablePath, _shell.Args,
                cancellationToken: stop);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or ObjectDisposedException))
        {
            // The last resort failed too — the shell itself will not start. Every earlier failure had
            // somewhere to go; this one has nowhere, so it is an error rather than a warning, and it
            // names what was tried: a profile pointing at a shell that was uninstalled and a working
            // directory that has gone look identical from the tile, which shows whatever the previous
            // command left on screen — or nothing at all, if the first command failed to spawn too.
            Trace.TraceError("The tile has no shell: starting '{0}' {1} in '{2}' failed: {3}",
                _shell.ExecutablePath, string.Join(' ', _shell.Args), _workingDir, ex);
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
