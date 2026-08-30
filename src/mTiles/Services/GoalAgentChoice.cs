using mTiles.Models;
using mTiles.Services.Agents;

namespace mTiles.Services;

/// <summary>
/// One agent a Goal tile can be run by: the configured instance, the CLI behind it, and where that CLI
/// was found.
/// </summary>
/// <remarks>
/// <para>The three travel together because a run needs all three and any one of them alone is useless: an
/// instance says which model and which provider, the agent says which flags spell "read-only" and how to
/// read what comes back, and the path is what is actually started. Resolved once and passed down, so that
/// a phase cannot be judged against one agent's flags and run with another's.</para>
/// <para><see cref="Label"/> rather than a formatting rule at the call site: the same words name the agent
/// in the strip, in the review's own line and in the message a failure prints, and two of the three used
/// to be built separately.</para>
/// </remarks>
/// <param name="Instance">The configured instance, which is what the tile stores an id of.</param>
/// <param name="Agent">The CLI it runs.</param>
/// <param name="ExecutablePath">Where that CLI was found on this machine.</param>
public sealed record GoalAgentChoice(AiAgentInstance Instance, IAiAgent Agent, string ExecutablePath)
{
    /// <summary>What this instance is called on screen.</summary>
    /// <remarks>Its own name, falling back to the agent's: an instance the user has renamed is the one
    /// they will look for, and a nameless one still has to say which CLI it is.</remarks>
    public string Label => Instance.Name.Length > 0 ? Instance.Name : Agent.DisplayName;

    /// <summary>The instance's id, which is what a goal file records.</summary>
    public string InstanceId => Instance.Id;
}

/// <summary>
/// Which agents a Goal tile may offer, and which one a stored id means.
/// </summary>
/// <remarks>
/// <para>Separate from the tile because it is the one part of choosing an agent that is a rule rather
/// than a screen: what "available" means, and what happens to a goal whose agent has since been deleted
/// or uninstalled. Both were previously spread across three methods of the tile that each answered them
/// slightly differently — one of which silently swapped the tool a run was already using.</para>
/// <para>Availability is <c>AiAgentCatalog.IsAvailable(instance, settings)</c> and nothing else, so the
/// Goal tile and the agent tile cannot come to different conclusions about the same row.</para>
/// </remarks>
public static class GoalAgents
{
    /// <summary>
    /// What "the agents on this machine" means. Replaced by a test, so the Goal tile's phase machine can
    /// be driven without a CLI installed.
    /// </summary>
    /// <remarks>The same seam <c>WorktreeReader.Factory</c> and <c>GoalBaseline.Factory</c> give the
    /// same tests, and for the same reason: otherwise the whole loop passes by doing nothing wherever no
    /// agent happens to be installed, which is most build agents.</remarks>
    internal static Func<AppSettings, IReadOnlyList<GoalAgentChoice>>? Factory { get; set; }

    /// <summary>Every configured instance this machine can actually run.</summary>
    /// <remarks>Scans <c>PATH</c> per agent, so it is not free — see <c>AiAgentCatalog.Locate</c>, which
    /// holds its answer for half a minute.</remarks>
    public static IReadOnlyList<GoalAgentChoice> Available(AppSettings settings) =>
        Factory is { } stand ? stand(settings) : Detected(settings);

    private static IReadOnlyList<GoalAgentChoice> Detected(AppSettings settings) =>
    [
        .. settings.AiAgentInstances
            .Where(instance => AiAgentCatalog.IsAvailable(instance, settings))
            .Select(Resolve)
            .OfType<GoalAgentChoice>()
    ];

    /// <summary>The choice a configured instance makes, or null when its agent is not installed.</summary>
    private static GoalAgentChoice? Resolve(AiAgentInstance instance) =>
        AiAgentCatalog.Find(instance.AgentId) is { } agent
        && AiAgentCatalog.Locate(agent) is { } path
            ? new GoalAgentChoice(instance, agent, path)
            : null;

    /// <summary>
    /// The choice a stored instance id names, or null when nothing here is it.
    /// </summary>
    /// <remarks><b>Never substitutes.</b> A goal that names an agent which is gone gets no agent, and the
    /// tile says so: silently running somebody's goal on a different model than the one it was planned
    /// with is the failure this used to have, and it left no trace anywhere the user could see.</remarks>
    public static GoalAgentChoice? WithId(IReadOnlyList<GoalAgentChoice> available, string instanceId) =>
        instanceId.Length == 0
            ? null
            : available.FirstOrDefault(choice => choice.InstanceId == instanceId);

    /// <summary>
    /// The instance an old goal file's tool name refers to, for a file written before agents existed.
    /// </summary>
    /// <remarks>
    /// <para>Matched against the agent's display name and its binary, which is what the AI tools table
    /// called its rows — "Claude Code", "codex", "opencode". A name nothing matches answers null and the
    /// tile falls back to its first available agent, which is what the old code did anyway.</para>
    /// <para><b>Read for ever, not migrated once.</b> A goal file travels with a branch, so one written on
    /// another machine or on an older branch will still carry a tool name years from now — this is not a
    /// one-release bridge like the layout's.</para>
    /// </remarks>
    public static GoalAgentChoice? MatchingToolName(IReadOnlyList<GoalAgentChoice> available,
        string toolName) =>
        toolName.Length == 0
            ? null
            : available.FirstOrDefault(choice =>
                choice.Agent.DisplayName.Equals(toolName, StringComparison.OrdinalIgnoreCase)
                || choice.Agent.BinaryName.Equals(toolName, StringComparison.OrdinalIgnoreCase)
                || choice.Label.Equals(toolName, StringComparison.OrdinalIgnoreCase));
}
