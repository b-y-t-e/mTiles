using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using mTiles.Models;
using mTiles.Services;
using Terminal.Avalonia;
using Terminal.Pty;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(mTiles.Tests.TestApp))]

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
    /// The chain's real waits are seconds long; these are the same rules at a speed a test can assert
    /// on. The ratio is what matters, and the gap has to stay wide: a crash-loop test ends its command
    /// just after adoption and needs that to land unambiguously below the relaunch bar even when a
    /// loaded machine adds a few hundred milliseconds of scheduling delay. Hence 100 against 1500,
    /// not 150 against 400 — a margin measured in milliseconds is a flake waiting for CI.
    /// </summary>
    private static readonly DirectLaunchSession.Timings Fast =
        new(FallbackTimeout: 100, MinLifetimeForRelaunch: 1500, Retry: 10, Relaunch: 20);

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

    [Fact]
    public void A_command_that_dies_at_once_gives_way_to_the_fallback()
        => OnUiThread(async () =>
        {
            var (control, spawned) = NewTerminal();
            using var launch = DirectLaunchSession.Start(control, "", Shell, "claude --continue", "claude", "tile-1", timings: Fast);

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
            using var launch = DirectLaunchSession.Start(control, "", Shell, "startup", "fallback", "tile-1", timings: Fast);

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
    /// immediate: a chain still in its five-second verdict would otherwise go on to start the fallback
    /// in a terminal that now belongs to someone else — or to nobody.
    /// </summary>
    [Fact]
    public void Disposing_stops_the_chain_before_it_can_fall_back()
        => OnUiThread(async () =>
        {
            var (control, spawned) = NewTerminal();
            var launch = DirectLaunchSession.Start(control, "", Shell, "startup", "fallback", "tile-1", timings: Fast);

            await WaitUntil(() => spawned.Count == 1, "the startup command is spawned");
            launch.Dispose();
            spawned[0].EndProcess(1);   // the command dies — but nobody is entitled to answer that now

            await Task.Delay(Fast.FallbackTimeout + Fast.Retry + 150);   // past where the fallback would start
            await Drain();

            Assert.Single(spawned);
        });

    /// <summary>A tool that ran long enough and then exited is the user quitting it — the tile brings
    /// it back rather than going dead.</summary>
    [Fact]
    public void A_watched_command_that_ran_long_enough_is_relaunched()
        => OnUiThread(async () =>
        {
            var (control, spawned) = NewTerminal();
            using var launch = DirectLaunchSession.Start(control, "", Shell, "claude", null, "tile-1", timings: Fast);

            await WaitUntil(() => spawned.Count == 1, "the command is spawned");
            await Task.Delay(Fast.MinLifetimeForRelaunch + 100);   // outlives both thresholds
            spawned[0].EndProcess(0);                              // the user quits the tool

            await WaitUntil(() => spawned.Count == 2, "the tool is brought back");
            Assert.Equal("fake-shell -c claude", CommandOf(spawned[1]));
        });

    /// <summary>A tool that dies again right after being adopted is crash-looping; relaunching it would
    /// spin forever, so the tile is left dead.</summary>
    [Fact]
    public void A_watched_command_that_dies_straight_away_is_not_relaunched()
        => OnUiThread(async () =>
        {
            var (control, spawned) = NewTerminal();
            using var launch = DirectLaunchSession.Start(control, "", Shell, "claude", null, "tile-1", timings: Fast);

            await WaitUntil(() => spawned.Count == 1, "the command is spawned");
            await Task.Delay(Fast.FallbackTimeout + 50);   // adopted, but well short of the relaunch bar
            spawned[0].EndProcess(1);

            await Task.Delay(Fast.Relaunch + 150);
            await Drain();
            Assert.Single(spawned);
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
            var launch = DirectLaunchSession.Start(control, "", Shell, "claude", null, "tile-1", timings: Fast);

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
