using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Tiles;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The AI tiles somebody already has become agent tiles, and nothing else does.
/// </summary>
/// <remarks>
/// Without this an existing installation gets no agent tile at all: every AI tile anybody has today is a
/// terminal whose <c>userProfileId</c> names one of the four seeded profiles, and the profiles are what
/// this stage removes — so those leaves would come back as bare shells with no conversation to resume.
/// The negative half matters just as much: a profile the user wrote themselves is not an agent, and
/// starting one on an agent's flags is a repository under something nobody asked for.
/// </remarks>
public class AgentTileMigrationTests
{
    private static AppSettings SettingsWith(UserShellProfile profile)
    {
        var settings = new AppSettings();
        settings.ShellProfiles.Add(profile);
        foreach (var instance in AiAgentCatalog.SeedInstances())
            settings.AiAgentInstances.Add(instance);
        return settings;
    }

    private static UserShellProfile ClaudeProfile() => new()
    {
        Id = "profile-claude",
        Name = "Claude Code",
        ShellName = "PowerShell",
        StartupScript = "claude --session-id ${tileId}",
        RequiredAiToolBinaryName = "claude",
    };

    private static TileNode TerminalLeaf(string profileId, string shell = "PowerShell") => new()
    {
        IsLeaf = true,
        Kind = TileKindIds.Terminal,
        TileId = "tile-1",
        Settings = new JsonObject
        {
            [TerminalTileKind.UserProfileIdKey] = profileId,
            [TerminalTileKind.ShellNameKey] = shell,
        },
    };

    /// <summary>A leaf that was a seeded AI profile becomes an agent tile on the matching agent.</summary>
    [Fact]
    public void A_tile_running_a_seeded_ai_profile_becomes_an_agent_tile()
    {
        var settings = SettingsWith(ClaudeProfile());
        var leaf = TerminalLeaf("profile-claude");

        Assert.True(AgentTileMigration.Apply(leaf, settings));

        Assert.Equal(TileKindIds.Agent, leaf.Kind);
        Assert.Equal("claude", leaf.Settings?[AgentTileKind.AgentIdKey]?.GetValue<string>());
        Assert.Equal(
            settings.AiAgentInstances.First(i => i.AgentId == "claude").Id,
            leaf.Settings?[AgentTileKind.InstanceIdKey]?.GetValue<string>());

        // The profile is gone from the file, or the next reader has to keep explaining it away.
        Assert.Null(leaf.Settings?[TerminalTileKind.UserProfileIdKey]);

        // And the shell name stays: an older build reads an agent leaf as a terminal, and one without a
        // shell name opens on whatever that machine's default happens to be.
        Assert.Equal("PowerShell", leaf.Settings?[AgentTileKind.ShellNameKey]?.GetValue<string>());
        Assert.Equal(TileContentType.Terminal, leaf.ContentType);
    }

    /// <summary>
    /// A profile the user wrote is left alone.
    /// </summary>
    /// <remarks>Matched on the required binary rather than on the name, because the name is theirs to
    /// change and a profile that starts a build script is not an agent however it is called.</remarks>
    [Theory]
    [InlineData("")]
    [InlineData("some-other-tool")]
    public void A_profile_that_names_no_known_agent_stays_a_terminal(string binary)
    {
        var settings = SettingsWith(new UserShellProfile
        {
            Id = "profile-mine",
            Name = "Claude Code",
            ShellName = "PowerShell",
            RequiredAiToolBinaryName = binary,
        });

        var leaf = TerminalLeaf("profile-mine");

        Assert.False(AgentTileMigration.Apply(leaf, settings));
        Assert.Equal(TileKindIds.Terminal, leaf.Kind);
        Assert.Equal("profile-mine", leaf.Settings?[TerminalTileKind.UserProfileIdKey]?.GetValue<string>());
    }

    /// <summary>A plain shell tile, a note and a split's far branch are all left as they are.</summary>
    [Fact]
    public void Only_the_leaves_that_were_agents_change()
    {
        var settings = SettingsWith(ClaudeProfile());

        var tree = new TileNode
        {
            IsLeaf = false,
            First = TerminalLeaf("profile-claude"),
            Second = new TileNode
            {
                IsLeaf = false,
                First = new TileNode { IsLeaf = true, Kind = TileKindIds.Terminal, TileId = "shell" },
                Second = new TileNode { IsLeaf = true, Kind = TileKindIds.Note, TileId = "note" },
            },
        };

        Assert.True(AgentTileMigration.Apply(tree, settings));

        Assert.Equal(TileKindIds.Agent, tree.First!.Kind);
        Assert.Equal(TileKindIds.Terminal, tree.Second!.First!.Kind);
        Assert.Equal(TileKindIds.Note, tree.Second.Second!.Kind);
    }

    /// <summary>Nothing to migrate is not a change, so nothing is rewritten and no copy is taken.</summary>
    [Fact]
    public void A_layout_with_no_ai_profiles_is_left_untouched()
    {
        var settings = SettingsWith(ClaudeProfile());
        var leaf = new TileNode { IsLeaf = true, Kind = TileKindIds.Terminal, TileId = "shell" };

        Assert.False(AgentTileMigration.Apply(leaf, settings));
        Assert.False(AgentTileMigration.Apply(null, settings));
    }
}
