using mTiles.Models;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests;

/// <summary>Which instances the Agent tile's chooser offers: none whose CLI is missing, except the tile's own.</summary>
public sealed class AgentInstanceChooserOfferTests
{
    private static AiAgentInstance Instance(string agentId) => new() { AgentId = agentId, Name = agentId };

    [Fact]
    public void An_agent_that_is_not_installed_is_left_out()
    {
        var current = Instance("claude");
        var missing = Instance("codex");

        var offered = AgentInstanceChooser.Offered([current, missing], current, agent => agent.Id == "claude");

        Assert.Equal([current], offered);
    }

    [Fact]
    public void The_tile_s_own_instance_stays_even_when_its_agent_is_not_installed()
    {
        var current = Instance("claude");
        var installed = Instance("codex");

        var offered = AgentInstanceChooser.Offered([current, installed], current, agent => agent.Id == "codex");

        Assert.Equal([current, installed], offered);
    }

    [Fact]
    public void The_tile_s_own_instance_comes_first_when_settings_no_longer_list_it()
    {
        var current = Instance("claude");
        var installed = Instance("codex");

        var offered = AgentInstanceChooser.Offered([installed], current, _ => true);

        Assert.Equal([current, installed], offered);
    }
}
