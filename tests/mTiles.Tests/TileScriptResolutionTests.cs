using mTiles.Models;
using mTiles.Services;
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

    private static ShellProfile Shell => new()
    {
        Name = "fake",
        ExecutablePath = "fake-shell",
        Args = ["-l"],
        Type = ShellType.Bash,
    };

    private static UserShellProfile Profile(string startup, string fallback) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = "test",
        ShellName = "fake",
        StartupScript = startup,
        FallbackScript = fallback,
    };

    // ---- which scripts a tile launches -----------------------------------------

    /// <summary>The reason the tile looks the profile up every time instead of keeping a copy: editing
    /// a profile in Settings has to take effect on the next launch, without recreating the tile.</summary>
    [Fact]
    public void A_tile_launches_the_profiles_scripts_as_they_are_now()
    {
        var settings = NewSettings();
        var profile = Profile("claude --continue", "claude");
        settings.Settings.ShellProfiles.Add(profile);

        var tile = new TerminalTileViewModel("", Shell, settings,
            LaunchScripts.FromProfile("stale", "also stale"), profile.Id);

        profile.StartupScript = "claude --resume";      // the user edits the profile

        var scripts = tile.ResolveCurrentScripts();
        Assert.Equal("claude --resume", scripts.Startup);
        Assert.Equal("claude", scripts.Fallback);
    }

    /// <summary>A profile the user deleted must not take the tile's commands with it — the tile falls
    /// back to what it was created with rather than silently becoming a bare shell.</summary>
    [Fact]
    public void A_tile_whose_profile_was_deleted_keeps_the_scripts_it_was_made_with()
    {
        var settings = NewSettings();
        var tile = new TerminalTileViewModel("", Shell, settings,
            LaunchScripts.FromProfile("claude --continue", "claude"), userProfileId: "gone-for-good");

        var scripts = tile.ResolveCurrentScripts();
        Assert.Equal("claude --continue", scripts.Startup);
        Assert.True(scripts.RunsCommandChain);
    }

    [Fact]
    public void A_tile_created_without_a_profile_runs_its_own_scripts()
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
