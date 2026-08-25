using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using Terminal.Avalonia;
using Terminal.Pty;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The profile launch chain, driven through a fake PTY instead of a real shell. Everything here failed
/// only at runtime before: a restart leaving two chains fighting over one tile, and a closed tile
/// whose chain relaunched the shell it had just killed.
/// </summary>
public class DirectLaunchSessionTests
{
    private static readonly ShellProfile Shell = new()
    {
        Name = "fake",
        ExecutablePath = "fake-shell",
        Args = ["-l"],
        Type = ShellType.Bash,
    };

    /// <summary>
    /// The chain's real thresholds are seconds to minutes; these are the same rules at a speed a test
    /// can assert on. The gaps have to stay wide: a test that ends its command "at once" needs that to
    /// land unambiguously below the bar even when a loaded machine adds a few hundred milliseconds of
    /// scheduling delay. Hence 600 and 2500, not 100 and 300 — a margin measured in milliseconds is a
    /// flake waiting for CI.
    /// </summary>
    private static readonly ChainPolicy Fast =
        new(MinLifetimeForRelaunch: 600, Established: 2500, Retry: 10, Relaunch: 20);

    /// <summary>Added to every threshold a test means to step over. The lifetime now comes from the
    /// terminal's own monotonic stamp rather than a tick counter, so the grain is no longer 15.6 ms —
    /// but a loaded machine still adds scheduling delay between a test's <c>Delay</c> and the child
    /// actually ending, and a margin measured in single milliseconds is a flake waiting for CI.</summary>
    private const int ClockSlack = 60;

    /// <summary>
    /// For the tests that exhaust the relaunch budget, which need several whole cycles. The band
    /// between the two thresholds has to be wide enough to land inside deliberately: a clean exit
    /// above <c>MinLifetimeForRelaunch</c> but below <c>Established</c> is a relaunch that is
    /// <em>charged</em>, while one above <c>Established</c> is the user at work and is free. With
    /// <see cref="ClockSlack"/> either side, that band cannot be 60 ms wide.
    /// <para>The error is one-directional, which is why 200/1000 is enough: <c>Task.Delay</c> overshoots
    /// and never undershoots, and the dispatcher's own latency between the delay and the child ending
    /// only adds to the measured lifetime. So every "at least this long" bound is safe by construction,
    /// and the single bound overshoot could break — a charged relaunch at 260 ms having to stay under
    /// <c>Established</c> — has 740 ms of headroom.</para>
    /// </summary>
    private static readonly ChainPolicy Budgeted =
        new(MinLifetimeForRelaunch: 200, Established: 1000, Retry: 10, Relaunch: 10,
            MaxRelaunches: 3, RelaunchWindow: 60_000);

    /// <summary>A real GUID: `TileScript.Resolve` refuses anything else, because the id ends up inside
    /// a string handed to `shell -c`.</summary>
    private const string RealTileId = "1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed";

    private readonly List<TerminalControl> _controls = [];

    private void OnUiThread(Func<Task> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DirectLaunchSessionTests).Assembly);
        session.Dispatch(async () =>
        {
            try { await body(); }
            finally
            {
                foreach (var control in _controls)
                    control.Dispose();
                _controls.Clear();
            }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"timed out waiting until {what}");
            await Task.Delay(1);
        }
    }

    private static Task Drain()
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => drained.SetResult(), DispatcherPriority.Background);
        return drained.Task;
    }

    /// <summary>A terminal whose every spawn is recorded, so a test can assert what the chain decided
    /// to run and in which order.</summary>
    private (TerminalControl Control, List<FakePty> Spawned) NewTerminal()
    {
        var spawned = new List<FakePty>();
        var control = new TerminalControl
        {
            PtyFactory = options =>
            {
                var pty = new FakePty(options);
                spawned.Add(pty);
                return pty;
            },
        };
        _controls.Add(control);
        return (control, spawned);
    }

    /// <summary>The command line the chain asked for, as the shell would have seen it.</summary>
    private static string CommandOf(FakePty pty) => string.Join(' ', [pty.Options.Command, .. pty.Options.Arguments]);

    /// <summary>
    /// First launch and "restart shell" go through <see cref="TileLauncher.Launch"/> precisely so they
    /// cannot drift, and the thing they must both do first is take the tile away from whatever was
    /// running it. They were two copies once, and the difference was a tile left with two chains after
    /// a restart, each relaunching into the other's session.
    /// </summary>
    [Fact]
    public void Launching_a_tile_stops_whatever_was_running_it_and_starts_its_chain()
        => OnUiThread(async () =>
        {
            using var settings = new TempSettings();
            var (control, spawned) = NewTerminal();
            var tile = new TerminalTileViewModel("", Shell, settings.Service,
                LaunchScripts.FromProfile("claude", "fallback")) { TileId = "tile-1" };
            var previous = new TileScriptResolutionTests.CountingLaunch();
            tile.ReplaceLaunchSession(previous);

            try
            {
                TileLauncher.Launch(control, tile);

                Assert.Equal(1, previous.Disposals);        // handed over before anything else
                await WaitUntil(() => spawned.Count == 1, "the profile's chain starts");
                Assert.Equal("fake-shell -c claude", CommandOf(spawned[0]));
            }
            finally
            {
                tile.Dispose();
            }
        });

    /// <summary>
    /// A profile whose shell is <c>cmd</c> has its commands run by something else — and this asserts it
    /// where it counts, on what was actually spawned. The rule itself is unit-tested in
    /// <c>ScriptContractTests</c>; what could still be wrong here is the wiring, and it silently was:
    /// resolving a replacement shell and then handing the old one to <c>ShellCommandLine</c> compiles,
    /// passes every test of the rule, and leaves the behaviour exactly as broken as before.
    /// </summary>
    [Fact]
    public void A_cmd_profiles_commands_are_not_spawned_through_cmd()
        => OnUiThread(async () =>
        {
            using var settings = new TempSettings();
            var (control, spawned) = NewTerminal();
            var cmd = new ShellProfile
            {
                Name = "CMD", ExecutablePath = @"C:\Windows\System32\cmd.exe", Type = ShellType.Cmd,
            };
            var tile = new TerminalTileViewModel("", cmd, settings.Service,
                LaunchScripts.FromProfile("opencode import \"x\" ; opencode", "fallback"))
            { TileId = RealTileId };

            try
            {
                TileLauncher.Launch(control, tile);

                await WaitUntil(() => spawned.Count == 1, "the profile's chain starts");
                var launched = CommandOf(spawned[0]);
                // Whatever this machine has — PowerShell here, a POSIX shell on a Linux agent — the one
                // thing it must not be is the shell measured to mishandle every command it is given.
                Assert.DoesNotContain("cmd.exe", launched, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(" /c ", launched);
                // And the command itself travelled intact: the `;` that cmd would have swallowed.
                Assert.Contains("opencode import \"x\" ; opencode", launched);
            }
            finally
            {
                tile.Dispose();
            }
        });

    /// <summary>A tile whose profile names no fallback takes the other path entirely: one interactive
    /// shell, with the startup script typed into it, and no chain to relaunch anything.</summary>
    [Fact]
    public void Launching_a_tile_without_a_fallback_starts_a_plain_interactive_shell()
        => OnUiThread(async () =>
        {
            using var settings = new TempSettings();
            var (control, spawned) = NewTerminal();
            var tile = new TerminalTileViewModel("", Shell, settings.Service,
                LaunchScripts.FromProfile("echo hi", null)) { TileId = "tile-1" };

            try
            {
                TileLauncher.Launch(control, tile);

                await WaitUntil(() => spawned.Count == 1, "the shell starts");
                Assert.Equal("fake-shell -l", CommandOf(spawned[0]));   // interactive, not -c
            }
            finally
            {
                tile.Dispose();
            }
        });

    /// <summary>
    /// The classic path end to end: a profile with no fallback starts an interactive shell and types
    /// its startup script into it. Every piece of this was unit-tested and none of the joins were —
    /// the script is not an argument, it is typed into the session once the child speaks, so a script
    /// that never arrives looks exactly like one that did until somebody reads the bytes.
    /// </summary>
    [Fact]
    public void The_startup_script_is_typed_into_the_shell_once_it_speaks()
        => OnUiThread(async () =>
        {
            using var settings = new TempSettings();
            var (control, spawned) = NewTerminal();
            var tile = new TerminalTileViewModel("", Shell, settings.Service,
                LaunchScripts.FromProfile("cd src\nclaude --session ${tileId}", null)) { TileId = RealTileId };

            try
            {
                TileLauncher.Launch(control, tile);
                await WaitUntil(() => spawned.Count == 1, "the shell starts");

                // Nothing is typed until the child has spoken: a shell that has not opened its stdin
                // drops whatever arrives first, so the control waits for the first byte of output.
                await Drain();
                Assert.Equal("", spawned[0].Written);

                spawned[0].Emit("$ ");
                await WaitUntil(() => spawned[0].Written.Contains("claude"), "the script is typed in");

                // One command per line, each submitted, and ${tileId} resolved to this tile's own id.
                Assert.Equal($"cd src\rclaude --session {RealTileId}\r", spawned[0].Written);
            }
            finally
            {
                tile.Dispose();
            }
        });

    /// <summary>Incoherent thresholds are refused where a caller can see it, not somewhere inside a
    /// task nobody awaits — which is where every other failure in this class would surface.</summary>
    [Fact]
    public void Starting_a_chain_with_contradictory_thresholds_fails_at_the_call()
        => OnUiThread(() =>
        {
            var (control, spawned) = NewTerminal();
            var broken = new ChainPolicy(MinLifetimeForRelaunch: 10_000, Established: 5_000,
                Retry: 1, Relaunch: 1);

            Assert.Throws<ArgumentOutOfRangeException>(() => DirectLaunchSession.Start(
                control, "", Shell, LaunchScripts.FromProfile("claude", "fallback"), "tile-1", policy: broken));
            Assert.Empty(spawned);      // and nothing was started on the way to finding out
            return Task.CompletedTask;
        });

    /// <summary>
    /// The tile closing while the chain waits on its command. The session is replaced out from under
    /// the wait, and the chain has to read that as "somebody else owns this terminal now" rather than
    /// as a command that failed — starting the fallback into a disposed control is the difference
    /// between a tile closing and an orphaned shell with no window.
    /// </summary>
    [Fact]
    public void A_session_replaced_under_the_chain_stops_it_rather_than_falling_back()
        => OnUiThread(async () =>
        {
            var (control, spawned) = NewTerminal();
            using var launch = DirectLaunchSession.Start(control, "", Shell, LaunchScripts.FromProfile("claude", "fallback"), "tile-1", policy: Fast);

            await WaitUntil(() => spawned.Count == 1, "the command is spawned");
            control.Dispose();                      // the tile goes, without handing the chain over

            await Task.Delay(Fast.Retry + 150);
            await Drain();
            Assert.Single(spawned);                 // no fallback into a terminal that is gone
        });

    /// <summary>
    /// A tile whose id never got stamped, with a profile that uses <c>${tileId}</c>. The scripts cannot
    /// be run — expanding the token to nothing makes a different command — but the tile must still end
    /// up usable. This one runs on the dispatcher from an <c>async void</c> attach handler, so the
    /// alternative to falling back is not a failed launch; it is the application going down.
    /// </summary>
    [Fact]
    public void A_tile_with_no_id_drops_its_scripts_and_still_gets_a_shell()
        => OnUiThread(async () =>
        {
            using var settings = new TempSettings();
            var (control, spawned) = NewTerminal();
            var tile = new TerminalTileViewModel("", Shell, settings.Service,
                LaunchScripts.FromProfile("claude -r ${tileId}", "claude ${tileId}"));   // TileId never set

            try
            {
                TileLauncher.Launch(control, tile);                  // must not throw

                await WaitUntil(() => spawned.Count == 1, "the tile gets a shell anyway");
                Assert.Equal("fake-shell -l", CommandOf(spawned[0]));
                Assert.True(control.IsRunning);
            }
            finally
            {
                tile.Dispose();
            }
        });

    /// <summary>The same tile without a fallback, which takes the other launch path. That one builds
    /// its script inside a task nobody awaits, so before the check moved up in front of both paths it
    /// logged and left the tile with nothing at all — the asymmetry, not the throw, was the defect.</summary>
    [Fact]
    public void A_tile_with_no_id_still_gets_a_shell_on_the_interactive_path_too()
        => OnUiThread(async () =>
        {
            using var settings = new TempSettings();
            var (control, spawned) = NewTerminal();
            var tile = new TerminalTileViewModel("", Shell, settings.Service,
                LaunchScripts.FromProfile("claude --session ${tileId}", null));      // TileId never set

            try
            {
                TileLauncher.Launch(control, tile);

                await WaitUntil(() => spawned.Count == 1, "the tile gets a shell anyway");
                Assert.Equal("fake-shell -l", CommandOf(spawned[0]));

                // And the script it could not resolve is not typed in half-expanded either.
                spawned[0].Emit("$ ");
                await Drain();
                Assert.Equal("", spawned[0].Written);
            }
            finally
            {
                tile.Dispose();
            }
        });

    /// <summary>The one wiring proof that <see cref="ChainStep.NextCommand"/> spawns the profile's
    /// fallback rather than a bare shell — the regression that quietly walked a tile past the very
    /// command its author wrote for the case. Which exit codes and lifetimes arrive at that step is a
    /// separate question, and <c>ChainDecisionTests</c> answers it row by row without a terminal.</summary>
    [Fact]
    public void A_command_that_dies_at_once_gives_way_to_the_fallback()
        => OnUiThread(async () =>
        {
            var (control, spawned) = NewTerminal();
            using var launch = DirectLaunchSession.Start(control, "", Shell, LaunchScripts.FromProfile("claude --continue", "claude"), "tile-1", policy: Fast);

            await WaitUntil(() => spawned.Count == 1, "the startup command is spawned");
            Assert.Equal("fake-shell -c claude --continue", CommandOf(spawned[0]));

            spawned[0].EndProcess(1);   // no session to continue — the command gives up immediately

            await WaitUntil(() => spawned.Count == 2, "the fallback takes over");
            Assert.Equal("fake-shell -c claude", CommandOf(spawned[1]));
        });

    [Fact]
    public void A_chain_where_nothing_survives_ends_at_a_plain_interactive_shell()
        => OnUiThread(async () =>
        {
            var (control, spawned) = NewTerminal();
            using var launch = DirectLaunchSession.Start(control, "", Shell, LaunchScripts.FromProfile("startup", "fallback"), "tile-1", policy: Fast);

            await WaitUntil(() => spawned.Count == 1, "the startup command is spawned");
            spawned[0].EndProcess(1);
            await WaitUntil(() => spawned.Count == 2, "the fallback is spawned");
            spawned[1].EndProcess(1);

            await WaitUntil(() => spawned.Count == 3, "the interactive shell is started");
            // Interactively this time: the profile's own args, and no -c command.
            Assert.Equal("fake-shell -l", CommandOf(spawned[2]));
            Assert.True(control.IsRunning);
        });

    /// <summary>
    /// Disposing is how a restart and a closing tile take the terminal away from a chain. It has to be
    /// immediate: a chain waiting on its command would otherwise go on to start the fallback in a
    /// terminal that now belongs to someone else — or to nobody.
    /// </summary>
    [Fact]
    public void Disposing_stops_the_chain_before_it_can_fall_back()
        => OnUiThread(async () =>
        {
            var (control, spawned) = NewTerminal();
            var launch = DirectLaunchSession.Start(control, "", Shell, LaunchScripts.FromProfile("startup", "fallback"), "tile-1", policy: Fast);

            await WaitUntil(() => spawned.Count == 1, "the startup command is spawned");
            launch.Dispose();
            spawned[0].EndProcess(1);   // the command dies — but nobody is entitled to answer that now

            await Task.Delay(Fast.Retry + 150);   // past where the fallback would start
            await Drain();

            Assert.Single(spawned);
        });

    /// <summary>
    /// A spawn that fails outright — a tool that is not installed, a working directory that has gone —
    /// is what the next link in the chain is for. Letting it out of the chain abandons the tile
    /// instead, which is the opposite of what a fallback exists to do.
    /// </summary>
    [Fact]
    public void A_command_that_cannot_even_be_spawned_gives_way_to_the_fallback()
        => OnUiThread(async () =>
        {
            var spawned = new List<FakePty>();
            var control = new TerminalControl
            {
                PtyFactory = options =>
                {
                    // The first command's binary does not exist; the fallback's does.
                    if (options.Arguments.Contains("missing-tool"))
                        throw new FileNotFoundException("no such executable");
                    var pty = new FakePty(options);
                    spawned.Add(pty);
                    return pty;
                },
            };
            _controls.Add(control);

            using var launch = DirectLaunchSession.Start(control, "", Shell, LaunchScripts.FromProfile("missing-tool", "claude"), "tile-1", policy: Fast);

            await WaitUntil(() => spawned.Count == 1, "the fallback runs after the failed spawn");
            Assert.Equal("fake-shell -c claude", CommandOf(spawned[0]));
        });

    /// <summary>A tool that ran long enough and then exited is the user quitting it — the tile brings
    /// it back rather than going dead.</summary>
    [Fact]
    public void A_watched_command_that_ran_long_enough_is_relaunched()
        => OnUiThread(async () =>
        {
            var (control, spawned) = NewTerminal();
            using var launch = DirectLaunchSession.Start(control, "", Shell, LaunchScripts.FromProfile("claude", null), "tile-1", policy: Fast);

            await WaitUntil(() => spawned.Count == 1, "the command is spawned");
            await Task.Delay(Fast.MinLifetimeForRelaunch + 100);   // outlives both thresholds
            spawned[0].EndProcess(0);                              // the user quits the tool

            await WaitUntil(() => spawned.Count == 2, "the tool is brought back");
            Assert.Equal("fake-shell -c claude", CommandOf(spawned[1]));
        });

    /// <summary>A tool the user quits again right away is one that will not stay up; bringing it back
    /// would spin forever. The chain stops trying — but leaves a shell behind, because a tile the user
    /// is left staring at, dead, is worse than one holding a shell they did not ask for.
    /// <para>The failing variant of this — the last command exiting <em>non-zero</em> just as quickly —
    /// is not a second test here. Both codes reach the same <see cref="ChainStep.NextCommand"/> in
    /// <c>ChainPolicy.Decide</c>, and the chain dispatches on the step alone, so what the exit code
    /// means is <c>ChainDecisionTests</c>' table to state and this file's job only to wire.</para></summary>
    [Fact]
    public void A_command_the_user_quits_straight_away_is_not_relaunched_but_leaves_a_shell()
        => OnUiThread(async () =>
        {
            var (control, spawned) = NewTerminal();
            using var launch = DirectLaunchSession.Start(control, "", Shell, LaunchScripts.FromProfile("claude", null), "tile-1", policy: Fast);

            await WaitUntil(() => spawned.Count == 1, "the command is spawned");
            await Task.Delay(50);   // well short of the relaunch bar
            spawned[0].EndProcess(0);

            await WaitUntil(() => spawned.Count == 2, "the tile is left with a shell");
            Assert.Equal("fake-shell -l", CommandOf(spawned[1]));   // not the command again
            Assert.True(control.IsRunning);

            await Task.Delay(Fast.Relaunch + 100);
            await Drain();
            Assert.Equal(2, spawned.Count);                         // and that is the end of it
        });

    /// <summary>
    /// The other half of the exit-code rule, and the reason it cannot stand on the code alone. A tool
    /// the user had been working in for a long time and that then crashes is not a command that "did
    /// not work" — demoting the tile down the chain, permanently, on a single crash would take the
    /// user's tool away and never give it back. Long enough, and a failure is a crash to recover from.
    /// </summary>
    [Fact]
    public void A_command_that_crashes_after_working_for_a_long_time_is_brought_back()
        => OnUiThread(async () =>
        {
            var (control, spawned) = NewTerminal();
            using var launch = DirectLaunchSession.Start(control, "", Shell, LaunchScripts.FromProfile("claude", "fallback"), "tile-1", policy: Fast);

            await WaitUntil(() => spawned.Count == 1, "the command is spawned");
            await Task.Delay(Fast.Established + 100);   // a session the user actually worked in
            spawned[0].EndProcess(1);                   // and which then crashed

            await WaitUntil(() => spawned.Count == 2, "the tool comes back");
            Assert.Equal("fake-shell -c claude", CommandOf(spawned[1]));   // not the fallback
        });

    /// <summary>
    /// Bringing a crashed tool back cannot be unconditional. A command that runs just long enough to
    /// qualify and then dies, every single time, is still a loop — a slow one, which is worse than a
    /// fast one because nobody watches long enough to see it happening. After a few tries the chain
    /// stops believing in it and moves on.
    /// </summary>
    [Fact]
    public void A_command_that_keeps_crashing_is_given_up_on_rather_than_relaunched_for_ever()
        => OnUiThread(async () =>
        {
            // Its own thresholds: this test needs several full crash-and-return cycles, and at the
            // shared ones that is ten seconds of sleeping.
            var (control, spawned) = NewTerminal();
            using var launch = DirectLaunchSession.Start(control, "", Shell, LaunchScripts.FromProfile("claude", "fallback"), "tile-1", policy: Budgeted);

            // Each turn: run long enough to count as having worked, then crash.
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                await WaitUntil(() => spawned.Count >= attempt, $"attempt {attempt} is spawned");
                if (CommandOf(spawned[attempt - 1]) != "fake-shell -c claude")
                    break;
                await Task.Delay(Budgeted.Established + ClockSlack);
                spawned[attempt - 1].EndProcess(1);
            }

            await WaitUntil(() => spawned.Exists(p => CommandOf(p) == "fake-shell -c fallback"),
                "the chain gives up and moves on");

            // Four runs of the command: the first, and three that brought it back.
            Assert.Equal(4, spawned.FindAll(p => CommandOf(p) == "fake-shell -c claude").Count);
        });

    /// <summary>
    /// The bound has to hold for the chain, not for each command in it. With a budget per command, a
    /// profile whose fallback exits cleanly walked from the crashing command to the fallback, took the
    /// clean exit as "start the profile afresh", and came back to the top — renewing the budget on
    /// every lap. A rule that was bounded at each step and unbounded as a whole, reachable by any
    /// profile with a fallback, which is every profile that runs a chain at all.
    /// </summary>
    [Fact]
    public void Walking_to_the_fallback_and_back_does_not_renew_the_relaunch_budget()
        => OnUiThread(async () =>
        {
            var (control, spawned) = NewTerminal();
            using var launch = DirectLaunchSession.Start(control, "", Shell, LaunchScripts.FromProfile("claude", "fallback"), "tile-1", policy: Budgeted);

            // End every session the way that used to keep the lap going: the fallback cleanly, so it
            // asks to start the profile over. Inside the band — over the relaunch bar, under
            // `Established` — because a clean exit above `Established` is the user at work and is
            // deliberately free, which would make this loop by design rather than by defect.
            for (int i = 0; i < 12; i++)
            {
                await WaitUntil(() => spawned.Count >= i + 1, $"session {i + 1} is spawned");
                if (CommandOf(spawned[i]) == "fake-shell -l")
                    break;                                  // the chain has reached its terminus
                await Task.Delay(Budgeted.MinLifetimeForRelaunch + ClockSlack);
                spawned[i].EndProcess(CommandOf(spawned[i]).EndsWith("fallback") ? 0 : 1);
            }

            await WaitUntil(() => spawned.Exists(p => CommandOf(p) == "fake-shell -l"),
                "the chain comes to rest at a shell");

            // Exactly, not "at most": three charged laps of two sessions, a fourth lap whose relaunch
            // is refused, then the shell. An upper bound would pass just as happily if the chain
            // stopped early for some entirely different reason.
            Assert.Equal(9, spawned.Count);
        });

    /// <summary>Every command in the profile pointing at something that is not installed — the state a
    /// machine is in after the tool was uninstalled, or before it was ever installed. The tile still has
    /// to end up with a shell in it.</summary>
    [Fact]
    public void A_profile_whose_every_command_is_missing_still_ends_at_a_shell()
        => OnUiThread(async () =>
        {
            var spawned = new List<FakePty>();
            var control = new TerminalControl
            {
                PtyFactory = options =>
                {
                    if (options.Arguments.Contains("-c"))    // any wrapped command, i.e. both of them
                        throw new FileNotFoundException("no such executable");
                    var pty = new FakePty(options);
                    spawned.Add(pty);
                    return pty;
                },
            };
            _controls.Add(control);

            using var launch = DirectLaunchSession.Start(control, "", Shell, LaunchScripts.FromProfile("gone", "also-gone"), "tile-1", policy: Fast);

            await WaitUntil(() => spawned.Count == 1, "the interactive shell is started");
            Assert.Equal("fake-shell -l", CommandOf(spawned[0]));
            Assert.True(control.IsRunning);
        });

    /// <summary>
    /// The bug this chain was rewritten for. A restart kills the watched session, which the chain is
    /// otherwise entitled to answer by relaunching — straight into the tile the restart has just taken
    /// over, leaving two chains on one terminal. Handing the chain over first is what prevents it; the
    /// <c>SessionId</c> on the exit report is the second barrier, for an exit that is not even its own.
    /// </summary>
    [Fact]
    public void A_chain_handed_over_before_a_restart_does_not_relaunch_into_it()
        => OnUiThread(async () =>
        {
            var (control, spawned) = NewTerminal();
            var launch = DirectLaunchSession.Start(control, "", Shell, LaunchScripts.FromProfile("claude", null), "tile-1", policy: Fast);

            await WaitUntil(() => spawned.Count == 1, "the command is spawned");
            await Task.Delay(Fast.MinLifetimeForRelaunch + 100);   // adopted, and old enough to qualify

            // Exactly what TileLauncher does on "restart shell": hand the chain over, then replace the
            // session. Without the first step the kill below reads as "my tool exited".
            launch.Dispose();
            await control.RestartAsync(new PtyOptions { Command = "fake-shell", Arguments = ["-l"] });

            await Task.Delay(Fast.Relaunch + 150);
            await Drain();

            Assert.Equal(2, spawned.Count);              // the restart's session, and nothing else
            Assert.Equal("fake-shell -l", CommandOf(spawned[1]));
            Assert.True(control.IsRunning);
        });
}

public class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
