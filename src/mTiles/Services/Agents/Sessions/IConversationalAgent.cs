using System.Diagnostics;
using mTiles.AgentSessions;
using mTiles.Models;
using mTiles.Services.Providers;

namespace mTiles.Services.Agents.Sessions;

/// <summary>
/// An agent that can be held as a structured conversation — messages, tool calls, approvals and plans
/// drawn by this application — rather than as a terminal running its TUI.
/// </summary>
/// <remarks>
/// <para><b>A separate interface, not more members on <see cref="IAiAgent"/></b>: that one is already the
/// whole of what a terminal launch, a headless Goal run, sign-ins and usage need to know, and a
/// conversation is a fourth way of running the CLI with nothing in common with the other three beyond
/// the process environment. An agent implements this when it has a structured protocol worth speaking.
/// </para>
/// <para><b>The session is the agent's, and so is every word of its protocol.</b> What comes out of it
/// is <c>mTiles.AgentSessions.Events</c> and nothing else, which is what lets one view — and later a
/// browser — draw every agent the same way.</para>
/// </remarks>
public interface IConversationalAgent
{
    /// <summary>Builds a session that is not started yet.</summary>
    IAgentSession CreateSession(AgentSessionLaunch launch, IAgentEventSink sink);
}

/// <summary>
/// Everything resolved for one conversation's launch: the binary, the account, the environment, and the
/// conversation to resume.
/// </summary>
/// <param name="ExecutablePath">Where the CLI is on this machine.</param>
/// <param name="Runtime">The instance, its provider or sign-in, and the model resolved for this launch.
/// </param>
/// <param name="Environment">The instance's environment — a null value unsets the variable.</param>
/// <param name="Behaviour">The instance's permission mode, already fitted to what the agent supports.
/// </param>
/// <param name="Effort">The instance's effort, already fitted.</param>
/// <param name="ResumeToken">What the agent last called this conversation, or null for a new one.</param>
/// <param name="ConversationId">The tile's own id — also the session id for an agent that is told one.
/// </param>
public sealed record AgentSessionLaunch(
    string ExecutablePath,
    string WorkingDirectory,
    AgentRuntime Runtime,
    IReadOnlyDictionary<string, string?> Environment,
    AiBehaviour Behaviour,
    AiEffort Effort,
    string? ResumeToken,
    string ConversationId)
{
    /// <summary>The model to ask for, or empty for the agent's own choice.</summary>
    public string Model => Runtime.RequestedModel;

    /// <summary>The instance's own extra arguments, verbatim.</summary>
    public IReadOnlyList<string> ExtraArgs => Runtime.Instance.ExtraArgs;

    /// <summary>A start of the CLI with these arguments, in the working directory, under the instance's
    /// environment.</summary>
    public ProcessStartInfo StartInfo(IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo(ExecutablePath) { WorkingDirectory = WorkingDirectory };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        foreach (var (name, value) in Environment)
        {
            if (value is null) psi.Environment.Remove(name);
            else psi.Environment[name] = value;
        }

        return psi;
    }
}
