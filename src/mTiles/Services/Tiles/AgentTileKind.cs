using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Shells;
using mTiles.ViewModels;

namespace mTiles.Services.Tiles;

/// <summary>
/// An AI agent in a tile.
/// </summary>
/// <remarks>
/// <para>What a shell profile whose script happened to start an agent used to be, minus the script: the
/// commands are the agent's own (<c>IAiAgent.Interactive</c>) and the thing the user picks is an
/// <see cref="AiAgentInstance"/> — a configured way of running one.</para>
/// <para>The layout stores the instance's id <b>and</b> the agent's, because they answer different
/// questions after the user has been in Settings: the instance is what a tile was configured as, the
/// agent is what it was running. An instance that has been deleted still leaves a tile that starts, on
/// the agent's own seeded configuration, rather than an empty tile where a conversation used to be.
/// </para>
/// </remarks>
public sealed class AgentTileKind : TileKind<AgentTileViewModel>
{
    /// <summary>The configured way of running an agent this tile was created from.</summary>
    public const string InstanceIdKey = "agentInstanceId";

    /// <summary>And which agent that was, as a fallback for when the instance has been deleted.</summary>
    public const string AgentIdKey = "agentId";

    /// <summary>
    /// The shell the agent's commands run in, written for the rollback and nothing else.
    /// </summary>
    /// <remarks>An older build reads this leaf as a terminal (<c>TileKindIds.ToLegacy</c>), and a
    /// terminal without a shell name opens on whatever that machine's default is. This build never
    /// reads it: the shell an agent tile uses is the default one, decided at every launch.</remarks>
    public const string ShellNameKey = "shellName";

    /// <summary>The conversation to resume, for an agent that names its own — see
    /// <see cref="SessionStrategy.CapturedAfterStart"/>. Absent for the other two strategies, where the
    /// tile's own id is the session id and writing it down twice would let the two disagree.</summary>
    public const string SessionIdKey = "sessionId";

    public override string Id => TileKindIds.Agent;
    public override string DisplayName => "Agent";
    public override string IconId => "robot";
    public override string AccentKey => "TileAccentAgent";

    /// <summary>
    /// One card per agent this machine can actually run.
    /// </summary>
    /// <remarks>Unavailable instances are left out rather than shown disabled, which is what
    /// <c>AiAgentCatalog.IsAvailable</c> is for: a chooser is a list of things that will work when
    /// clicked. An empty list means no agent is installed, and the tile is then created on the first
    /// instance there is — which starts, fails to find its binary, and says so in the terminal, where
    /// the user can read the reason.</remarks>
    public override IReadOnlyList<TileSetupOption> SetupOptions(TileContext context)
    {
        var available = Available(context).ToList();
        if (available.Count <= 1) return [];

        return
        [
            .. available.Select(instance => new TileSetupOption(
                instance.Name, IconId, AccentKey,
                new JsonObject { [InstanceIdKey] = instance.Id, [AgentIdKey] = instance.AgentId }))
        ];
    }

    protected override AgentTileViewModel Create(TileContext context, JsonObject? state)
    {
        var settings = context.Settings.Settings;
        var (instance, agent, substitution) = Resolve(context, state);

        return new AgentTileViewModel(context.WorkingDirectory,
            ShellTerminalCatalog.ResolveDefault(settings), context.Settings, agent, instance.Id,
            SessionIdFor(agent, state), context.TileId, context.RequestSave, substitution);
    }

    /// <summary>
    /// Which configured way of running an agent this tile opens on, and whether that is the one it was
    /// created with.
    /// </summary>
    /// <remarks>
    /// <para>The chain of fallbacks is what keeps a tile from opening empty when its instance has been
    /// deleted in Settings — but its last two links can land on a <em>different agent</em>, and a Codex
    /// tile that quietly comes back as Claude is a different program working in somebody's repository.
    /// So a fallback that had something to fall back <em>from</em> answers an
    /// <see cref="AgentSubstitution"/> as well, which the tile says once and <see cref="Save"/> writes
    /// the original choice from.</para>
    /// <para>A tile created without a chooser names nothing, and nothing is not a substitution: there
    /// was no choice to depart from.</para>
    /// </remarks>
    private static (AiAgentInstance Instance, IAiAgent Agent, AgentSubstitution? Substitution) Resolve(
        TileContext context, JsonObject? state)
    {
        var settings = context.Settings.Settings;
        var requestedInstanceId = state.String(InstanceIdKey) ?? "";
        var requestedAgentId = state.String(AgentIdKey) ?? "";

        if (settings.AiAgentInstances.FirstOrDefault(i => i.Id == requestedInstanceId) is { } configured)
            return WithAgent(configured, requestedInstanceId, requestedAgentId, false);

        // The instance is gone, or there never was one. The agent it was running is the closest thing to
        // what the user had; the first available one is what a tile created without a chooser gets,
        // because there was only one to offer.
        var instance = settings.AiAgentInstances.FirstOrDefault(i => i.AgentId == requestedAgentId)
                       ?? Available(context).FirstOrDefault()
                       ?? settings.AiAgentInstances.FirstOrDefault()
                       ?? AiAgentCatalog.SeedInstanceFor(AiAgentCatalog.All[0]);

        var chose = requestedInstanceId.Length != 0 || requestedAgentId.Length != 0;
        return WithAgent(instance, requestedInstanceId, requestedAgentId, chose);
    }

    /// <summary>
    /// The agent an instance names, or the one standing in for it — and either way, whether the tile is
    /// running what it was configured as.
    /// </summary>
    /// <remarks>
    /// <para>An instance can name an agent <em>this build does not have</em>: <c>settings.json</c> is
    /// read tolerantly and never pruned, so a file written by a newer version and read after a Velopack
    /// rollback keeps its rows intact. Found by id, such an instance used to resolve straight onto the
    /// first agent in the catalog — the tile ran a different program with no notice, and <see cref="Save"/>
    /// then wrote the stand-in's id over the only record of the original, so the next splitter drag made
    /// it permanent. That is exactly the failure <see cref="AgentSubstitution"/> exists for, so this is a
    /// substitution like any other: said once, and the requested ids kept.</para>
    /// </remarks>
    private static (AiAgentInstance Instance, IAiAgent Agent, AgentSubstitution? Substitution) WithAgent(
        AiAgentInstance instance, string requestedInstanceId, string requestedAgentId,
        bool instanceSubstituted)
    {
        var substitution = instanceSubstituted
            ? new AgentSubstitution(requestedInstanceId, requestedAgentId,
                NoticeFor(requestedAgentId, instance))
            : null;

        if (AiAgentCatalog.Find(instance.AgentId) is { } agent) return (instance, agent, substitution);

        // The instance itself is the request when nothing was substituted on the way here, so its own
        // ids are what the layout keeps — a build that has the agent again opens the tile as it was.
        var standIn = AiAgentCatalog.All[0];
        return (instance, standIn,
            new AgentSubstitution(
                instanceSubstituted ? requestedInstanceId : instance.Id,
                instanceSubstituted ? requestedAgentId : instance.AgentId,
                UnknownAgentNotice(instance, standIn)));
    }

    /// <summary>The sentence for an instance whose agent this build does not know.</summary>
    /// <remarks>It does not offer Settings, because there is nothing to restore there: the instance is
    /// intact and it is this build that is missing the agent.</remarks>
    private static string UnknownAgentNotice(AiAgentInstance instance, IAiAgent standIn) =>
        $"The agent instance \"{instance.Name}\" names \"{instance.AgentId}\", which this version of "
        + $"mTiles does not have, so the tile is running {standIn.DisplayName} — a different agent. "
        + "Update mTiles and this tile goes back to it.";

    /// <summary>The one sentence a substituted tile carries.</summary>
    /// <remarks>Two of them, because the two cases are not the same size: another configuration of the
    /// agent the user chose is a changed account or model, while another agent is another program. Both
    /// end on the same offer, since the original choice is still in the layout and restoring the instance
    /// in Settings is all it takes to have it honoured again.</remarks>
    private static string NoticeFor(string requestedAgentId, AiAgentInstance instance)
    {
        const string restore = " Restore it in Settings and this tile goes back to it.";
        var running = $"The agent instance this tile was created with is gone, so it is running "
                      + $"\"{instance.Name}\"";

        if (instance.AgentId == requestedAgentId) return running + " instead." + restore;

        var requested = AiAgentCatalog.Find(requestedAgentId)?.DisplayName
                        ?? "the agent it was created with";
        return running + $" instead of {requested} — a different agent." + restore;
    }

    protected override JsonObject? Save(AgentTileViewModel tile)
    {
        // The choice, not what it had to be resolved to: a layout is saved for any reason at all — a
        // splitter dragged — so writing the substitute's ids would make a fallback permanent within
        // seconds of the tile opening, and re-adding the instance in Settings would no longer bring it
        // back. See AgentSubstitution.
        var state = new JsonObject
        {
            [InstanceIdKey] = tile.Substitution?.RequestedInstanceId ?? tile.InstanceId,
            [AgentIdKey] = tile.Substitution?.RequestedAgentId ?? tile.AgentId,
            [ShellNameKey] = tile.Shell.DisplayName,
        };
        // And not the conversation either, while the tile is substituted: the id belongs to the agent
        // standing in, while the ids above name the one the layout still asks for — the disagreement
        // SessionIdFor drops on the next load, and handed on it would be an id an agent has never seen.
        if (tile.Substitution is null && tile.NamesItsOwnSession
            && tile.SessionId is { Length: > 0 } session)
            state[SessionIdKey] = session;
        return state;
    }

    /// <summary>The stored conversation, but only where it can still belong to this tile.</summary>
    /// <remarks>An id is the answer of <em>one</em> agent, so it is dropped as soon as the tile is
    /// resolving onto another one — which happens when the instance's agent is changed in Settings, or
    /// when the instance is deleted and the tile falls back to whatever is available. Handing it on
    /// would be the one thing the captured strategy forbids: <c>codex resume &lt;unknown&gt;</c> stops
    /// on an interactive picker, and <c>agy --conversation &lt;unknown&gt;</c> quietly starts a
    /// different conversation and exits 0.</remarks>
    private static string? SessionIdFor(IAiAgent agent, JsonObject? state)
    {
        if (agent.SessionStrategy != SessionStrategy.CapturedAfterStart) return null;
        var storedAgent = state.String(AgentIdKey);
        // A layout this build wrote always names the agent beside the id; nothing else is evidence of a
        // disagreement, so an absent name is read as "the one being resolved" rather than as a mismatch.
        return string.IsNullOrEmpty(storedAgent) || storedAgent == agent.Id
            ? state.String(SessionIdKey)
            : null;
    }

    /// <summary>The instances whose agent this machine has, and whose provider it can still reach.
    /// </summary>
    private static IEnumerable<AiAgentInstance> Available(TileContext context) =>
        context.Settings.Settings.AiAgentInstances
            .Where(instance => AiAgentCatalog.IsAvailable(instance, context.Settings.Settings));
}
