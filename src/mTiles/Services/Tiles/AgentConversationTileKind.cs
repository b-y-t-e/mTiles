using System.Text.Json.Nodes;
using mTiles.AgentSessions.Storage;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Agents.Sessions;
using mTiles.ViewModels;
using mTiles.ViewModels.AgentConversation;

namespace mTiles.Services.Tiles;

/// <summary>
/// An AI agent held as a conversation.
/// </summary>
/// <remarks>
/// <para>Chosen the way a terminal agent tile is — one card per configured instance this machine can run —
/// narrowed to the agents that implement <see cref="IConversationalAgent"/>.</para>
/// <para><b>The layout stores which instance and which agent, and nothing about the conversation.</b> The
/// conversation is the tile's id in the conversation store, so a layout file stays a layout file and the
/// history lives where a later web view can read it too.</para>
/// <para>An instance deleted in Settings falls back to another instance of the same agent, and then to the
/// first conversational one — and a tile that lands on a different agent does not start, because the host
/// drops a conversation whose agent does not match rather than handing its token to a CLI that has never
/// seen it (see <see cref="AgentSubstitution"/>).</para>
/// </remarks>
public sealed class AgentConversationTileKind(IConversationStore store, IAgentSessionStarter? sessionStarter = null) : TileKind<AgentConversationTileViewModel>
{
    public override string Id => TileKindIds.AgentConversation;
    public override string DisplayName => "Agent";
    public override string IconId => "agent-chat";
    public override string AccentKey => "TileAccentAgent";

    /// <summary>
    /// Nothing: an Agent tile opens straight into its conversation, where the agent is picked.
    /// </summary>
    /// <remarks>It asked first, the way a terminal tile asks for a shell, and that is the wrong question for a
    /// conversation: a shell has to be decided before anything can run, while a conversation with nothing in it
    /// is bound to nobody yet. The chooser is in the strip beside the model, and it is locked once the
    /// conversation has something in it — the rule t3code keeps, and ours for the same reason: a conversation
    /// belongs to the agent that holds it, and its resume token is not another CLI's to take.</remarks>
    public override IReadOnlyList<TileSetupOption> SetupOptions(TileContext context) => [];

    protected override AgentConversationTileViewModel Create(TileContext context, JsonObject? state)
    {
        var settings = context.Settings.Settings;
        var requestedInstance = state.String(AgentStateKeys.InstanceIdKey) ?? "";
        var requestedAgent = state.String(AgentStateKeys.AgentIdKey) ?? "";

        // What the layout asked for, then the agent it was running, then the one the last Agent tile was
        // pointed at — a new tile has no state at all, and "the one you were using" is what it opens on.
        var instance = settings.AiAgentInstances.FirstOrDefault(i => i.Id == requestedInstance && IsConversational(i))
                       ?? settings.AiAgentInstances.FirstOrDefault(i => i.AgentId == requestedAgent && IsConversational(i))
                       ?? Available(context).FirstOrDefault(i => i.Id == settings.LastAgentInstanceId)
                       ?? Available(context).FirstOrDefault()
                       ?? settings.AiAgentInstances.FirstOrDefault(IsConversational)
                       ?? AiAgentCatalog.SeedInstanceFor(AiAgentCatalog.All[0]);
        var agent = AiAgentCatalog.Find(instance.AgentId) ?? AiAgentCatalog.All[0];

        return new AgentConversationTileViewModel(context.WorkingDirectory, context.Settings, store, instance, agent,
            context.TileId, SubstitutionFor(requestedInstance, requestedAgent, instance, agent),
            OverridesFrom(state), context.RequestSave, sessionStarter: sessionStarter);
    }

    /// <summary>The model, mode and effort chosen in this tile over its instance's.</summary>
    public const string ModelKey = "model";

    /// <inheritdoc cref="ModelKey"/>
    public const string ModeKey = "mode";

    /// <inheritdoc cref="ModelKey"/>
    public const string EffortKey = "effort";

    /// <summary>What the layout says this tile runs differently from its instance.</summary>
    /// <remarks>Read through the canonical ids, so a mode written by a newer build this one has no name for
    /// is dropped — the instance's own applies — rather than failing the tile.</remarks>
    private static SessionOverrides OverridesFrom(JsonObject? state) => new(
        state.String(ModelKey) is { Length: > 0 } model ? model : null,
        SessionSettingOptions.ParseMode(state.String(ModeKey)),
        SessionSettingOptions.ParseEffort(state.String(EffortKey)));

    /// <summary>
    /// A tile resolved onto a different instance than the one it was created with, or null.
    /// </summary>
    /// <remarks>
    /// <para>Any other instance is a substitution, the rule <see cref="TerminalAgentTileKind"/> keeps: another instance
    /// of the same agent is another account or model, and <see cref="Save"/> writing its id would make that
    /// permanent at the next splitter drag, so restoring the instance in Settings would no longer bring it
    /// back. Such a tile still starts — the conversation is the same agent's.</para>
    /// <para>Another agent does not start: the host deletes a conversation whose agent does not match, so the
    /// old agent's history would be gone for good because an instance was removed in Settings.</para>
    /// </remarks>
    private static AgentSubstitution? SubstitutionFor(string requestedInstance, string requestedAgent,
        AiAgentInstance instance, IAiAgent agent)
    {
        var choseSomething = requestedInstance.Length != 0 || requestedAgent.Length != 0;
        if (!choseSomething || requestedInstance == instance.Id) return null;

        var keptAgent = requestedAgent.Length == 0 ? instance.AgentId : requestedAgent;
        if (keptAgent == agent.Id)
            return new AgentSubstitution(requestedInstance, keptAgent,
                $"The agent instance this conversation was held with is gone, so it is running \"{instance.Name}\" " +
                "instead. Restore it in Settings and this tile goes back to it.");

        var requested = AiAgentCatalog.Find(keptAgent)?.DisplayName ?? keptAgent;
        return new AgentSubstitution(requestedInstance, keptAgent,
            $"The {requested} instance this conversation was held with is gone, so it was not opened on " +
            $"{agent.DisplayName} — a different agent. Restore it in Settings and this tile goes back to it.");
    }

    protected override JsonObject? Save(AgentConversationTileViewModel tile)
    {
        var state = new JsonObject
        {
            [AgentStateKeys.InstanceIdKey] = tile.Substitution?.RequestedInstanceId ?? tile.Instance.Id,
            [AgentStateKeys.AgentIdKey] = tile.Substitution?.RequestedAgentId ?? tile.Agent.Id,
        };
        var overrides = tile.Overrides;
        if (overrides.Model is { } model) state[ModelKey] = model;
        if (overrides.Behaviour is { } mode) state[ModeKey] = SessionSettingOptions.ModeId(mode);
        if (overrides.Effort is { } effort) state[EffortKey] = SessionSettingOptions.EffortId(effort);
        return state;
    }

    private static bool IsConversational(AiAgentInstance instance) =>
        AiAgentCatalog.Find(instance.AgentId) is IConversationalAgent;

    private static IEnumerable<AiAgentInstance> Available(TileContext context) =>
        context.Settings.Settings.AiAgentInstances
            .Where(instance => IsConversational(instance) && AiAgentCatalog.IsAvailable(instance, context.Settings.Settings));
}
