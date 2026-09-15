using System.Text.Json.Serialization;

namespace mTiles.AgentSessions.Events;

/// <summary>
/// One thing an agent session said or did, in the one vocabulary every agent is translated into.
/// </summary>
/// <remarks>
/// <para><b>This is the contract, and nothing about any particular CLI may leak into it.</b> Claude Code
/// speaks stream-json over stdin, codex JSON-RPC, opencode HTTP and server-sent events, Grok ACP, pi its
/// own RPC and agy step updates — and all six arrive here as the same records. What differs is the
/// translation, which is the agent's own class; what is drawn, stored and (later) sent to a browser is
/// this and only this.</para>
/// <para><b>Append-only and replayable.</b> A conversation is the ordered list of its events: the store
/// writes them, <see cref="Conversation.ConversationReducer"/> folds them into what is on screen, and a
/// restart replays the same list into the same picture. So an event describes what happened, never what
/// to draw — grouping, collapsing and "is this still pending" are the reducer's to work out.</para>
/// <para><b>Serialized with a <c>type</c> discriminator</b>, so the JSON a browser receives names each
/// record the way the C# does. <see cref="Sequence"/> and <see cref="At"/> are stamped by the host when
/// the event is appended, not by whoever produced it.</para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionStateChanged), "session.state")]
[JsonDerivedType(typeof(SessionConfigured), "session.configured")]
[JsonDerivedType(typeof(TurnStarted), "turn.started")]
[JsonDerivedType(typeof(TurnCompleted), "turn.completed")]
[JsonDerivedType(typeof(UserMessageAdded), "message.user")]
[JsonDerivedType(typeof(AssistantTextDelta), "message.assistant.delta")]
[JsonDerivedType(typeof(AssistantMessageCompleted), "message.assistant.completed")]
[JsonDerivedType(typeof(ReasoningDelta), "reasoning.delta")]
[JsonDerivedType(typeof(ToolStarted), "tool.started")]
[JsonDerivedType(typeof(ToolUpdated), "tool.updated")]
[JsonDerivedType(typeof(ToolCompleted), "tool.completed")]
[JsonDerivedType(typeof(ApprovalRequested), "approval.requested")]
[JsonDerivedType(typeof(ApprovalResolved), "approval.resolved")]
[JsonDerivedType(typeof(QuestionsAsked), "questions.asked")]
[JsonDerivedType(typeof(QuestionsAnswered), "questions.answered")]
[JsonDerivedType(typeof(PlanUpdated), "plan.updated")]
[JsonDerivedType(typeof(PlanProposed), "plan.proposed")]
[JsonDerivedType(typeof(UsageUpdated), "usage.updated")]
[JsonDerivedType(typeof(CheckpointCaptured), "checkpoint.captured")]
[JsonDerivedType(typeof(CheckpointRestored), "checkpoint.restored")]
[JsonDerivedType(typeof(NoticeRaised), "notice")]
public abstract record AgentEvent
{
    /// <summary>Position in the conversation, from 1, assigned by the store. Zero until appended.</summary>
    public long Sequence { get; init; }

    /// <summary>When the host received it.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>The turn this belongs to, where it belongs to one.</summary>
    public string? TurnId { get; init; }
}

/// <summary>The process behind the session came up, went busy, is waiting on the user, or ended.</summary>
public sealed record SessionStateChanged(AgentSessionState State, string? Detail = null) : AgentEvent;

/// <summary>
/// What the agent says it is running as — and the id that resumes it.
/// </summary>
/// <param name="ResumeToken">The agent's own handle on this conversation, opaque to everything but the
/// agent class that wrote it: a Claude session id, a codex thread id, an ACP session id. The host keeps
/// the latest one beside the conversation, because it is what the next launch is handed.</param>
public sealed record SessionConfigured(string? Model, string? Mode, string? ResumeToken) : AgentEvent;

/// <summary>A turn began — the agent took a message and started working on it.</summary>
public sealed record TurnStarted : AgentEvent;

/// <summary>A turn ended, one way or another.</summary>
public sealed record TurnCompleted(TurnOutcome Outcome, string? Error = null) : AgentEvent;

/// <summary>What the user sent.</summary>
public sealed record UserMessageAdded(string MessageId, string Text, IReadOnlyList<ImageAttachment> Images)
    : AgentEvent;

/// <summary>Part of the assistant's reply, as it streams.</summary>
public sealed record AssistantTextDelta(string MessageId, string Delta) : AgentEvent;

/// <summary>
/// The assistant's reply, whole. Replaces whatever the deltas built, because some agents stream nothing
/// and some correct what they streamed.
/// </summary>
public sealed record AssistantMessageCompleted(string MessageId, string Text) : AgentEvent;

/// <summary>Part of what the model thought before answering, where the agent shows it.</summary>
public sealed record ReasoningDelta(string MessageId, string Delta) : AgentEvent;

/// <summary>The agent began using a tool.</summary>
/// <param name="ToolCallId">Stable across <see cref="ToolUpdated"/> and <see cref="ToolCompleted"/>.
/// </param>
/// <param name="Name">The tool's own name, as the agent spells it — shown small, never matched on.</param>
/// <param name="Title">One line saying what it is doing, already readable.</param>
public sealed record ToolStarted(
    string ToolCallId,
    ToolKind Kind,
    string Name,
    string Title,
    ToolDetail Detail) : AgentEvent;

/// <summary>More is known about a running tool: its input finished streaming, or it produced output.</summary>
/// <param name="Title">A better title, or null to keep the one already shown.</param>
/// <param name="Detail">The whole detail as it now stands, or null to keep it.</param>
/// <param name="OutputDelta">Output to append, for tools that stream it.</param>
public sealed record ToolUpdated(
    string ToolCallId,
    string? Title = null,
    ToolDetail? Detail = null,
    string? OutputDelta = null) : AgentEvent;

/// <summary>A tool finished.</summary>
/// <param name="Output">Its whole output, where the agent reports one; replaces anything streamed.</param>
public sealed record ToolCompleted(
    string ToolCallId,
    ToolStatus Status,
    string? Output = null,
    ToolDetail? Detail = null) : AgentEvent;

/// <summary>The agent stopped and is asking whether it may do something.</summary>
/// <param name="Options">What may be answered, in the order the agent offers them. Never empty.</param>
public sealed record ApprovalRequested(
    string RequestId,
    ApprovalKind Kind,
    string Title,
    string? Detail,
    string? ToolCallId,
    IReadOnlyList<ApprovalOption> Options) : AgentEvent;

/// <summary>An approval was answered — by the user, by a rule, or by the session ending.</summary>
public sealed record ApprovalResolved(string RequestId, ApprovalDecision Decision) : AgentEvent;

/// <summary>The agent asked the user one or more questions and waits for the answers.</summary>
public sealed record QuestionsAsked(string RequestId, IReadOnlyList<UserQuestion> Questions) : AgentEvent;

/// <summary>The questions were answered, or dismissed (<paramref name="Answers"/> null).</summary>
/// <param name="Answers">Per question id, the chosen labels or the text typed.</param>
public sealed record QuestionsAnswered(
    string RequestId,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Answers) : AgentEvent;

/// <summary>The agent's running to-do list, whole — every update replaces the last.</summary>
public sealed record PlanUpdated(string? Explanation, IReadOnlyList<PlanStep> Steps) : AgentEvent;

/// <summary>A plan the agent wrote for the user to approve before it implements anything.</summary>
public sealed record PlanProposed(string Markdown) : AgentEvent;

/// <summary>How much of the context is in use, and what the session has cost.</summary>
public sealed record UsageUpdated(TokenUsage Usage) : AgentEvent;

/// <summary>
/// The working tree was photographed, and what changed since the photograph it is compared with.
/// </summary>
/// <remarks>Taken twice per turn: before the message is handed to the agent, so edits the user made
/// between turns are not blamed on the agent, and when the turn ends. Only the second carries a
/// <paramref name="BaseCheckpointId"/> and files.</remarks>
/// <param name="CheckpointId">The host's handle on the photograph — enough to ask for the diff again
/// or to put the files back.</param>
/// <param name="BaseCheckpointId">The photograph this one is compared with — the one taken as the turn
/// began — or null for a photograph taken before a turn.</param>
/// <param name="Files">What changed since <paramref name="BaseCheckpointId"/>; empty when nothing did.</param>
public sealed record CheckpointCaptured(
    string CheckpointId,
    string? BaseCheckpointId,
    IReadOnlyList<ChangedFile> Files) : AgentEvent;

/// <summary>The working tree was put back to how a checkpoint found it.</summary>
public sealed record CheckpointRestored(string CheckpointId) : AgentEvent;

/// <summary>Something worth telling the user that is none of the above — a retry, a warning, an error.</summary>
public sealed record NoticeRaised(NoticeLevel Level, string Text) : AgentEvent;
