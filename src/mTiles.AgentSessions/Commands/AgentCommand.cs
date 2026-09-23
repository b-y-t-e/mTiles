using System.Text.Json.Serialization;
using mTiles.AgentSessions.Events;

namespace mTiles.AgentSessions.Commands;

/// <summary>
/// Something a viewer asks a conversation to do, as data.
/// </summary>
/// <remarks>The desktop view could call <see cref="IAgentSession"/> directly; these exist so that a
/// browser can send exactly the same requests as JSON and have them handled by the same
/// <see cref="Hosting.AgentConversationHost.ExecuteAsync"/>, rather than by a second copy of the rules
/// about what may be asked when.</remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SendMessage), "message.send")]
[JsonDerivedType(typeof(InterruptTurn), "turn.interrupt")]
[JsonDerivedType(typeof(CompactContext), "context.compact")]
[JsonDerivedType(typeof(RespondToApproval), "approval.respond")]
[JsonDerivedType(typeof(AnswerQuestions), "questions.answer")]
[JsonDerivedType(typeof(RestoreCheckpoint), "checkpoint.restore")]
[JsonDerivedType(typeof(ChangeSessionSettings), "session.settings")]
public abstract record AgentCommand;

/// <summary>Switch the running session's model, mode or effort. A null field is left as it is.</summary>
public sealed record ChangeSessionSettings(SessionSettings Settings) : AgentCommand;

/// <summary>Send a message.</summary>
/// <param name="Recorded">Whether the transcript gets a message from the user for it.</param>
/// <remarks><b>False has exactly one caller and needs a reason every time it gains another.</b> The handover
/// brief is text the agent must read and is not something the user said: written into the transcript as
/// theirs, a page of Markdown they never typed would stand above the first answer of the new agent and read
/// as their own words. It is not lost either — <see cref="Events.HandoverRecorded"/> carries the same text
/// and the timeline draws it folded, which is where an account of what the agent was told belongs.</remarks>
public sealed record SendMessage(string Text, IReadOnlyList<ImageAttachment>? Images = null, bool Recorded = true)
    : AgentCommand;

/// <summary>Stop the running turn.</summary>
public sealed record InterruptTurn : AgentCommand;

/// <summary>Ask the agent to summarise the conversation so far and carry on from the summary.</summary>
/// <remarks>Refused out loud where the running session is not an <see cref="ICompactingSession"/>, rather
/// than being ignored: three of the six agents can do this and the other three cannot, so a viewer that
/// asked anyway has to be told which it is looking at.</remarks>
public sealed record CompactContext : AgentCommand;

/// <summary>Answer a pending approval.</summary>
public sealed record RespondToApproval(string RequestId, ApprovalDecision Decision) : AgentCommand;

/// <summary>Answer or dismiss a pending round of questions.</summary>
public sealed record AnswerQuestions(
    string RequestId,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Answers) : AgentCommand;

/// <summary>Put the working tree back to how a checkpoint found it.</summary>
public sealed record RestoreCheckpoint(string CheckpointId) : AgentCommand;
