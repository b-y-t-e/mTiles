using mTiles.AgentSessions;
using mTiles.Models;
using mTiles.Services.Providers;

namespace mTiles.Services.Agents.Sessions;

/// <summary>
/// Resolves everything a conversation's launch needs, the way a terminal agent tile and a Goal run
/// resolve theirs, and says in one sentence why it cannot when it cannot.
/// </summary>
/// <remarks>
/// <para><b>The same rules, asked in the same order, as the other two launch paths</b> — availability,
/// the binary, <see cref="AgentModelResolver"/>, the context windows, <see cref="AgentRuntime.For"/>,
/// <c>PrepareToLaunch</c> and <c>EnvFor</c>, and <see cref="AiProcessRunner.Fit"/> for the instance's
/// behaviour and effort. A conversation launched by rules of its own would be the same instance reaching
/// a different account, which is the failure every one of those exists to prevent.</para>
/// <para>Behaviour and effort are fitted as <see cref="AiUsage.Interactive"/>: somebody is watching, and
/// an approval is a button on the screen rather than a denial nobody sees.</para>
/// </remarks>
public static class AgentSessionLauncher
{
    /// <summary>A launch, or the reason there is none.</summary>
    public static async Task<(AgentSessionLaunch? Launch, string? Problem)> PrepareAsync(
        AppSettings settings, IAiAgent agent, AiAgentInstance instance, string workingDirectory,
        string conversationId, string? resumeToken, CancellationToken ct)
    {
        if (agent is not IConversationalAgent)
            return (null, $"{agent.DisplayName} cannot be held as a conversation yet. Use a terminal agent tile.");

        if (AgentAvailability.Problem(instance, settings, agent) is { } unavailable)
            return (null, unavailable);

        if (AiAgentCatalog.Locate(agent) is not { } executable)
            return (null, $"{agent.DisplayName} is not installed on this machine ({agent.BinaryName} was not found).");

        var (model, modelProblem) = await AgentModelResolver.ResolveAsync(settings, agent, instance, ct);
        if (modelProblem is not null) return (null, modelProblem);

        var windows = await ModelContextWindow.ResolveAsync(settings, agent, instance, model ?? "");
        var runtime = AgentRuntime.For(settings, instance, model, agent,
            windows?.AutoCompactWindow, windows?.MaxContextTokens);

        agent.PrepareToLaunch(runtime);
        var environment = agent.EnvFor(runtime);
        var (behaviour, effort) = AiProcessRunner.Fit(agent, AiUsage.Interactive,
            instance.DefaultBehaviour, instance.DefaultEffort, instance);

        return (new AgentSessionLaunch(executable, workingDirectory, runtime, environment, behaviour, effort,
            resumeToken, conversationId), null);
    }

    /// <summary>The agent's session for a prepared launch.</summary>
    public static IAgentSession Create(IAiAgent agent, AgentSessionLaunch launch, IAgentEventSink sink) =>
        ((IConversationalAgent)agent).CreateSession(launch, sink);
}
