using mTiles.AgentSessions;
using mTiles.Models;

namespace mTiles.Services.Agents.Sessions;

/// <summary>
/// How a conversation tile gets from an instance to a running session: the launch, then the session on it.
/// </summary>
/// <remarks>An abstraction so the tile can be driven without a CLI: what it decides — which agent holds the
/// conversation, when a switch is refused — is its own, and a test of that must not spawn whichever agents
/// happen to be installed on the machine running it.</remarks>
public interface IAgentSessionStarter
{
    /// <inheritdoc cref="AgentSessionLauncher.PrepareAsync"/>
    Task<(AgentSessionLaunch? Launch, string? Problem)> PrepareAsync(AppSettings settings, IAiAgent agent,
        AiAgentInstance instance, string workingDirectory, string conversationId, string? resumeToken,
        CancellationToken ct);

    /// <inheritdoc cref="AgentSessionLauncher.Create"/>
    IAgentSession Create(IAiAgent agent, AgentSessionLaunch launch, IAgentEventSink sink);
}

/// <summary>Starts the agent's own CLI, by the rules <see cref="AgentSessionLauncher"/> keeps.</summary>
public sealed class AgentSessionStarter : IAgentSessionStarter
{
    public static AgentSessionStarter Instance { get; } = new();

    private AgentSessionStarter()
    {
    }

    public Task<(AgentSessionLaunch? Launch, string? Problem)> PrepareAsync(AppSettings settings, IAiAgent agent,
        AiAgentInstance instance, string workingDirectory, string conversationId, string? resumeToken,
        CancellationToken ct) =>
        AgentSessionLauncher.PrepareAsync(settings, agent, instance, workingDirectory, conversationId, resumeToken, ct);

    public IAgentSession Create(IAiAgent agent, AgentSessionLaunch launch, IAgentEventSink sink) =>
        AgentSessionLauncher.Create(agent, launch, sink);
}
