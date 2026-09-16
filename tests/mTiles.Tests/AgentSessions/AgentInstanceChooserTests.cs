using mTiles.Models;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// The strip's list of agents, driven on its own: what it offers and refuses, and when a choice reaches the tile.
/// </summary>
public class AgentInstanceChooserTests
{
    [Fact]
    public void Another_agent_is_listed_refused_with_its_reason_while_the_conversation_is_held()
    {
        using var settings = new TempSettings();
        var claude = Instance(settings, "claude");
        var codex = Instance(settings, "codex");
        using var chooser = Chooser(settings, claude, heldAgentId: "claude", picked: _ => { });

        var refused = chooser.Options.Single(o => o.Instance.Id == codex.Id);

        Assert.False(refused.IsPickable);
        Assert.Contains("new conversation", refused.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(claude.Id, chooser.Selected?.Instance.Id);
    }

    [Fact]
    public void A_refused_entry_is_not_handed_to_the_tile_and_the_selection_goes_back()
    {
        using var settings = new TempSettings();
        var claude = Instance(settings, "claude");
        var codex = Instance(settings, "codex");
        AiAgentInstance? handed = null;
        using var chooser = Chooser(settings, claude, heldAgentId: "claude", picked: i => handed = i);

        chooser.Selected = chooser.Options.Single(o => o.Instance.Id == codex.Id);

        Assert.Null(handed);
        Assert.Equal(claude.Id, chooser.Selected?.Instance.Id);
    }

    [Fact]
    public void A_pickable_entry_is_handed_to_the_tile()
    {
        using var settings = new TempSettings();
        var claude = Instance(settings, "claude");
        var codex = Instance(settings, "codex");
        AiAgentInstance? handed = null;
        using var chooser = Chooser(settings, claude, heldAgentId: null, picked: i => handed = i);

        chooser.Selected = chooser.Options.Single(o => o.Instance.Id == codex.Id);

        Assert.Equal(codex.Id, handed?.Id);
    }

    [Fact]
    public void Picking_the_running_entry_back_is_handed_to_the_tile_so_a_queued_pick_is_superseded()
    {
        using var settings = new TempSettings();
        var claude = Instance(settings, "claude");
        var codex = Instance(settings, "codex");
        var handed = new List<string>();
        using var chooser = Chooser(settings, claude, heldAgentId: null, picked: i => handed.Add(i.Id));

        chooser.Selected = chooser.Options.Single(o => o.Instance.Id == codex.Id);
        chooser.Selected = chooser.Options.Single(o => o.Instance.Id == claude.Id);

        Assert.Equal([codex.Id, claude.Id], handed);
    }

    private static AiAgentInstance Instance(TempSettings settings, string agentId) =>
        settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == agentId);

    private static AgentInstanceChooser Chooser(TempSettings settings, AiAgentInstance running, string? heldAgentId,
        Action<AiAgentInstance> picked) =>
        new(settings.Service, () => running, i => i.Id == running.Id, () => heldAgentId, action => action(), picked);
}
