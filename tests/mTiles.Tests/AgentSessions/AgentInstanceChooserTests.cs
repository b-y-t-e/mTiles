using mTiles.Models;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// The strip's list of agents, driven on its own: what it offers and refuses, and when a choice reaches the tile.
/// </summary>
public class AgentInstanceChooserTests
{
    /// <remarks>It used to be refused here, with a sentence saying to start a new conversation. The refusal
    /// was right that no CLI can resume another's session and wrong that nothing could move: the transcript
    /// is ours and the tree is on disk, so the row says what picking it does and the tile asks before it
    /// does it.</remarks>
    [Fact]
    public void Another_agent_is_offered_as_a_handover_while_the_conversation_is_held()
    {
        using var settings = new TempSettings();
        var claude = Instance(settings, "claude");
        var codex = Instance(settings, "codex");
        using var chooser = Chooser(settings, claude, heldAgentId: "claude", picked: _ => { });

        var offered = chooser.Options.Single(o => o.Instance.Id == codex.Id);

        Assert.True(offered.IsPickable);
        Assert.Null(offered.Reason);
        Assert.Contains("hands the work over", offered.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(claude.Id, chooser.Selected?.Instance.Id);
    }

    /// <remarks>What is still refused is an instance this machine cannot run at all — and the reason under
    /// it is the only thing that says why, so the entry is offered dimmed rather than left out.</remarks>
    [Fact]
    public void An_instance_that_cannot_run_here_is_refused_and_never_handed_to_the_tile()
    {
        using var settings = new TempSettings();
        var claude = Instance(settings, "claude");
        var broken = Instance(settings, "codex");
        broken.ApiAccountId = "a-provider-that-is-not-configured";
        AiAgentInstance? handed = null;
        using var chooser = Chooser(settings, claude, heldAgentId: "claude", picked: i => handed = i);

        var refused = chooser.Options.Single(o => o.Instance.Id == broken.Id);
        Assert.False(refused.IsPickable);
        Assert.NotNull(refused.Reason);

        chooser.Selected = refused;

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
