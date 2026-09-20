using System.Text.Json.Nodes;
using mTiles.Services.Agents;
using mTiles.Services.Shells;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The setup a tile is already running, as the two kinds that can be picked again answer it.
/// </summary>
/// <remarks>
/// A wrong answer here is not a cosmetic one: the card the tile is already on is offered back, and
/// taking it ends the shell and its whole process tree — and for codex and agy the captured session id
/// with it — to rebuild the identical tile. Both rules carry a part the base class cannot state: the
/// terminal's state-less "Default shell" card, and the agent tile standing every instance of its own
/// CLI down in favour of the header's cheaper "Run this tile as…".
/// </remarks>
public sealed class TileSetupReuseTests
{
    private static TileSetupOption DefaultShellCard() =>
        new("Default shell", "console", "TextMuted", State: null);

    private static TileSetupOption ShellCard(string displayName) =>
        new(displayName, "console", "TextMuted",
            new JsonObject { [TerminalTileKind.ShellNameKey] = displayName });

    private static TileSetupOption AgentCard(string instanceId, string agentId) =>
        new(instanceId, "robot", "TileAccentAgent",
            new JsonObject
            {
                [AgentStateKeys.InstanceIdKey] = instanceId,
                [AgentStateKeys.AgentIdKey] = agentId,
            });

    /// <summary>The shell the tile resolved to, and the card that resolves to the same one.</summary>
    [Fact]
    public void A_terminal_is_already_on_its_own_shell_and_on_the_card_that_names_no_shell()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var context = new TileContext(directory.Path, settings.Service);
        var kind = (ITileKind)new TerminalTileKind();
        var tile = (TerminalTileViewModel)kind.Create(context, state: null);

        try
        {
            Assert.True(kind.IsCurrentSetup(context, tile, DefaultShellCard()));
            Assert.True(kind.IsCurrentSetup(context, tile, ShellCard(tile.Shell.DisplayName)));
            Assert.False(kind.IsCurrentSetup(context, tile, ShellCard("A shell this machine has not")));
        }
        finally { tile.Dispose(); }
    }

    /// <summary>A tile on a shell of its own is not on the default card.</summary>
    /// <remarks>Skipped where this machine has only the one shell: the question needs two to have an
    /// answer, and a machine with one offers no setup step at all.</remarks>
    [Fact]
    public void A_terminal_on_a_shell_it_was_given_is_not_on_the_default_card()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var context = new TileContext(directory.Path, settings.Service);
        var fallback = ShellTerminalCatalog.ResolveDefault(settings.Service.Settings, context.Shells);
        if (context.Shells.FirstOrDefault(shell => shell.DisplayName != fallback.DisplayName)
            is not { } another) return;

        var kind = (ITileKind)new TerminalTileKind();
        var tile = (TerminalTileViewModel)kind.Create(
            context, new JsonObject { [TerminalTileKind.ShellNameKey] = another.DisplayName });

        try
        {
            Assert.False(kind.IsCurrentSetup(context, tile, DefaultShellCard()));
            Assert.False(kind.IsCurrentSetup(context, tile, ShellCard(fallback.DisplayName)));
            Assert.True(kind.IsCurrentSetup(context, tile, ShellCard(another.DisplayName)));
        }
        finally { tile.Dispose(); }
    }

    /// <summary>Every instance of the CLI the tile is running counts as the setup it is already on.
    /// </summary>
    [Fact]
    public void An_agent_tile_is_already_on_every_instance_of_its_own_agent()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var context = new TileContext(directory.Path, settings.Service);
        var mine = settings.Service.Settings.AiAgentInstances[0];
        var elsewhere = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId != mine.AgentId);

        var kind = (ITileKind)new TerminalAgentTileKind();
        var tile = (TerminalAgentTileViewModel)kind.Create(context, new JsonObject
        {
            [AgentStateKeys.InstanceIdKey] = mine.Id,
            [AgentStateKeys.AgentIdKey] = mine.AgentId,
        });

        try
        {
            Assert.Equal(mine.AgentId, tile.AgentId);
            Assert.True(kind.IsCurrentSetup(context, tile, AgentCard(mine.Id, mine.AgentId)));
            // A second way of running the same CLI: the header's own instance switch does this without
            // rebuilding the tile, so the setup step does not offer it.
            Assert.True(kind.IsCurrentSetup(context, tile, AgentCard("another-instance", mine.AgentId)));
            Assert.False(kind.IsCurrentSetup(context, tile, AgentCard(elsewhere.Id, elsewhere.AgentId)));
        }
        finally { tile.Dispose(); }
    }
}
