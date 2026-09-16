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
[JsonDerivedType(typeof(RespondToApproval), "approval.respond")]
[JsonDerivedType(typeof(AnswerQuestions), "questions.answer")]
[JsonDerivedType(typeof(RestoreCheckpoint), "checkpoint.restore")]
[JsonDerivedType(typeof(ChangeSessionSettings), "session.settings")]
public abstract record AgentCommand;

/// <summary>Switch the running session's model, mode or effort. A null field is left as it is.</summary>
public sealed record ChangeSessionSettings(SessionSettings Settings) : AgentCommand;

/// <summary>Send a message.</summary>
public sealed record SendMessage(string Text, IReadOnlyList<ImageAttachment>? Images = null) : AgentCommand;

/// <summary>Stop the running turn.</summary>
public sealed record InterruptTurn : AgentCommand;

/// <summary>Answer a pending approval.</summary>
public sealed record RespondToApproval(string RequestId, ApprovalDecision Decision) : AgentCommand;

/// <summary>Answer or dismiss a pending round of questions.</summary>
public sealed record AnswerQuestions(
    string RequestId,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Answers) : AgentCommand;

/// <summary>Put the working tree back to how a checkpoint found it.</summary>
public sealed record RestoreCheckpoint(string CheckpointId) : AgentCommand;
