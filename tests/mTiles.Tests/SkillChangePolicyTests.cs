using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a tile does when the databases it may reach change under a running agent.
/// </summary>
/// <remarks>A table test, the convention <c>ChainPolicy</c> and <c>ActivityPolicy</c> keep: the whole of
/// this is which of three answers a situation deserves, and the answer that costs most if it is wrong —
/// restarting — is the one reached by the fewest situations.</remarks>
public class SkillChangePolicyTests
{
    private static SkillChangeResponse For(bool agentFollowsIt = false, bool isRunning = true,
        bool isBusy = false, bool hasUnsentWork = false) =>
        SkillChangePolicy.For(agentFollowsIt, isRunning, isBusy, hasUnsentWork);

    /// <summary>Claude Code watches its skills directory, so saying anything would be asking for
    /// something already done.</summary>
    [Fact]
    public void An_agent_that_follows_the_change_is_left_alone() =>
        Assert.Equal(SkillChangeResponse.Nothing,
            For(agentFollowsIt: true, isBusy: true, hasUnsentWork: true));

    /// <summary>Nothing has read anything yet, so the skill is simply there when it does.</summary>
    [Fact]
    public void An_agent_that_has_not_started_needs_nothing() =>
        Assert.Equal(SkillChangeResponse.Nothing, For(isRunning: false, isBusy: true));

    /// <summary>Idle with nothing unsent: restarted, whatever the conversation holds. Every agent now says
    /// when a cold resume did not bring the history back (ResumeCheck, and Grok's own load error), so the
    /// worst case is a loss the transcript announces rather than one discovered from a nonsense answer.
    /// </summary>
    [Fact]
    public void An_idle_tile_is_restarted_without_asking() =>
        Assert.Equal(SkillChangeResponse.Restart, For());

    /// <summary>Typed and not sent is work too, and a restart would take the composer's draft with the
    /// process.</summary>
    [Fact]
    public void An_unsent_message_counts_as_something_to_lose() =>
        Assert.Equal(SkillChangeResponse.Tell, For(hasUnsentWork: true));

    /// <summary>Busy outranks everything: a turn in flight, or a question half answered.</summary>
    [Fact]
    public void A_busy_agent_is_never_restarted() =>
        Assert.Equal(SkillChangeResponse.Tell, For(isBusy: true));

    /// <summary>A terminal agent holds a scrollback and possibly a half-typed prompt, so it answers the
    /// last two questions yes and the notice is all it can ever get.</summary>
    [Fact]
    public void A_terminal_agent_is_told_and_never_restarted() =>
        Assert.Equal(SkillChangeResponse.Tell, For(isBusy: true, hasUnsentWork: true));

    /// <summary>Even with everything to lose, the two rules above busy still answer first — which is what
    /// keeps a Claude terminal tile, and one whose session never started, from being told to restart for
    /// a change it does not need.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void The_first_two_rules_outrank_everything_a_terminal_agent_holds(bool agentFollowsIt, bool isRunning) =>
        Assert.Equal(SkillChangeResponse.Nothing,
            For(agentFollowsIt: agentFollowsIt, isRunning: isRunning, isBusy: true, hasUnsentWork: true));

    /// <summary>The sentence names the cause. A restart asked for out of nowhere is one nobody performs.</summary>
    [Fact]
    public void The_notice_says_why()
    {
        Assert.Contains("Database access", SkillChangePolicy.Notice, StringComparison.Ordinal);
        Assert.Contains("Restart the agent", SkillChangePolicy.Notice, StringComparison.Ordinal);
    }

    /// <summary>No agent reaches the policy's first rule, pinned so that one doing so is a measurement
    /// rather than an assumption — Claude Code did on its documentation and was observed not to follow.</summary>
    [Fact]
    public void No_agent_reaches_the_first_rule() =>
        Assert.DoesNotContain(AiAgentCatalog.All, agent => agent.WatchesSkillsDirectory(AgentSurface.Terminal));

    /// <summary>And no agent reaches it in an Agent tile, which is the tile the first rule used to silence
    /// on the commonest configuration there is.</summary>
    /// <remarks>Claude Code's watcher is documented for its terminal interface; the only thing said about
    /// a skill change reaching a headless or SDK session is <c>/reload-skills</c>, which nothing here
    /// sends. Answered as one property for both surfaces, an Agent tile on Claude Code got neither a
    /// restart nor a notice.</remarks>
    [Fact]
    public void No_agent_follows_a_skill_change_in_the_session_an_Agent_tile_drives() =>
        Assert.DoesNotContain(AiAgentCatalog.All, agent => agent.WatchesSkillsDirectory(AgentSurface.Structured));

    /// <summary>
    /// A restart something is waiting on is drawn differently, and says why.
    /// </summary>
    /// <remarks>The header shows the state <see cref="SkillChangePolicy"/> puts the tile into, and it
    /// was the one thing about that state nothing on the tile said: a notice bar the user had dismissed
    /// left no trace at all, so the request was made once and then forgotten by both sides. Colour
    /// alone would be a mark nobody can read, so the two are asserted together — the reason is the
    /// urgency, and the tooltip is the reason above the shortcut.</remarks>
    [Fact]
    public void An_action_nothing_is_waiting_on_is_drawn_as_it_always_was()
    {
        var leaf = LeafWith(new TileAction(TileActionIds.Restart, "Restart agent", "restart"));

        Assert.False(leaf.RestartIsUrgent);
        Assert.Equal("Restart agent (Ctrl+Shift+R)", leaf.RestartLabel);
    }

    [Fact]
    public void An_action_something_is_waiting_on_is_lit_and_names_the_cause()
    {
        var leaf = LeafWith(new TileAction(TileActionIds.Restart, "Restart agent", "restart",
            Urgency: SkillChangePolicy.Notice));

        Assert.True(leaf.RestartIsUrgent);
        Assert.Contains(SkillChangePolicy.Notice, leaf.RestartLabel, StringComparison.Ordinal);
        // The shortcut is what the tooltip is for the rest of the time, so it is kept rather than
        // replaced: the reason goes above it.
        Assert.Contains("Ctrl+Shift+R", leaf.RestartLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tile_with_nothing_to_restart_says_nothing_about_it()
    {
        var leaf = LeafWith();

        Assert.False(leaf.RestartIsUrgent);
        Assert.Equal("", leaf.RestartLabel);
    }

    private static LeafTileNodeViewModel LeafWith(params TileAction[] actions)
    {
        var content = actions.Length > 0 ? new ActionsOnly(actions) : null;
        return new LeafTileNodeViewModel("test", content, "", new TileActivationScope());
    }

    /// <summary>Content that offers a list of actions and does nothing else.</summary>
    /// <remarks>The header asks the content what it can do rather than what kind it is, which is what
    /// lets a test hand it a list without building a terminal.</remarks>
    private sealed class ActionsOnly(IReadOnlyList<TileAction> actions) : ObservableObject, ITileActions
    {
        public string KindId => "test";
        public IReadOnlyList<TileAction> Actions { get; } = actions;
        public Task<TileActionResult> InvokeAsync(string id) => Task.FromResult(TileActionResult.Ok);
        public void Dispose() { }
    }
}
