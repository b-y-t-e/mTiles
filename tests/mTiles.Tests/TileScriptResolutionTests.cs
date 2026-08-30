using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Shells;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a tile decides to run, and what it does with the launch that runs it. Both are rules about
/// ownership rather than about terminals, so they are reachable without one — given a settings file
/// that is not the user's.
/// </summary>
public sealed class TileScriptResolutionTests : IDisposable
{
    private readonly TempSettings _settings = new();

    private SettingsService NewSettings() => _settings.Service;

    public void Dispose() => _settings.Dispose();

    private static ShellInstallation Shell => new(new BashTerminal(), "fake-shell");

    /// <summary>A shell tile answers with what it was built with, and nothing looks anything up.
    /// </summary>
    /// <remarks>The profile lookup that used to be here went with the profiles: an AI CLI in a shell is
    /// an agent tile now, and it is <c>AgentTileViewModel</c> that overrides this to ask its instance.
    /// </remarks>
    [Fact]
    public void A_shell_tile_runs_the_scripts_it_was_made_with()
    {
        var settings = NewSettings();
        var tile = new TerminalTileViewModel("", Shell, settings,
            LaunchScripts.FromProfile(null, "claude"));

        Assert.Equal("claude", tile.ResolveCurrentScripts().Fallback);
    }

    [Fact]
    public void A_tile_with_no_scripts_at_all_runs_nothing()
    {
        var settings = NewSettings();
        var tile = new TerminalTileViewModel("", Shell, settings);

        Assert.False(tile.ResolveCurrentScripts().RunsCommandChain);
    }

    /// <summary>
    /// An install command runs at the first launch and never again — Restart shell starts a shell.
    /// </summary>
    /// <remarks>The user agreed to it once, in a dialog that showed the command. A button promising a
    /// fresh shell must not run somebody's package manager a second time.</remarks>
    [Fact]
    public void A_one_time_startup_script_is_run_once_and_not_on_a_restart()
    {
        var settings = NewSettings();
        var tile = new TerminalTileViewModel("", Shell, settings,
            oneTimeStartup: "npm install -g @anthropic-ai/claude-code");

        Assert.Equal("npm install -g @anthropic-ai/claude-code", tile.ResolveCurrentScripts().Startup);
        Assert.Null(tile.ResolveCurrentScripts().Startup);
    }

    // ---- who owns the launch ---------------------------------------------------

    /// <summary>
    /// Hand over first, stop the old one second. The other order leaves the tile pointing at a chain it
    /// has already tried to end, while the new one — already started — is unreachable and can never be
    /// stopped at all: a chain relaunching into a terminal nobody can take it away from.
    /// </summary>
    [Fact]
    public void A_launch_that_fails_to_stop_does_not_cost_the_tile_the_new_one()
    {
        var settings = NewSettings();
        var tile = new TerminalTileViewModel("", Shell, settings);
        var awkward = new ThrowingLaunch();
        var replacement = new CountingLaunch();

        tile.ReplaceLaunchSession(awkward);

        // No exception reaches the caller: every one of them is in the middle of something that must
        // finish — a relaunch that would otherwise abandon the tile without starting anything at all.
        tile.ReplaceLaunchSession(replacement);

        // And the tile is holding the new chain, so disposing the tile still stops it. With the old
        // ordering it kept pointing at the one that refused to stop, and disposed that again instead.
        tile.Dispose();
        Assert.Equal(1, replacement.Disposals);
    }

    private sealed class ThrowingLaunch : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("this chain refuses to stop");
    }

    internal sealed class CountingLaunch : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
}
