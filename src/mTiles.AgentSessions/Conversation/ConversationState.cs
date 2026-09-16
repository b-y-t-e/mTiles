using System.Collections.Immutable;
using System.Text.Json.Serialization;
using mTiles.AgentSessions.Events;

namespace mTiles.AgentSessions.Conversation;

/// <summary>
/// A conversation as it is drawn: what <see cref="ConversationReducer"/> makes of its events so far.
/// </summary>
/// <remarks>
/// <para>Immutable, so a view can hold the last one it drew and compare, and so the same state can be
/// handed to a serializer for a browser without a lock.</para>
/// <para><b>Nothing in here is stored.</b> The events are; this is always recomputed from them, which is
/// what keeps the desktop and a later web view from disagreeing about what a conversation looks like.
/// </para>
/// </remarks>
public sealed record ConversationState
{
    public static readonly ConversationState Empty = new();

    /// <summary>Everything in the conversation, oldest first.</summary>
    public ImmutableList<TimelineEntry> Timeline { get; init; } = [];

    /// <summary>Approvals the agent is waiting on, oldest first.</summary>
    public ImmutableList<ApprovalRequested> PendingApprovals { get; init; } = [];

    /// <summary>Rounds of questions the agent is waiting on, oldest first.</summary>
    public ImmutableList<QuestionsAsked> PendingQuestions { get; init; } = [];

    /// <summary>The agent's own to-do list as it last reported it, or null when it has none.</summary>
    public PlanUpdated? Plan { get; init; }

    /// <summary>The latest usage figures, merged — a report that leaves a figure out keeps the last one.
    /// </summary>
    public TokenUsage? Usage { get; init; }

    public AgentSessionState SessionState { get; init; } = AgentSessionState.Stopped;

    /// <summary>The turn in progress, or null when the agent is idle.</summary>
    public string? ActiveTurnId { get; init; }

    public string? Model { get; init; }

    /// <summary>The permission mode, as an id from <see cref="Options"/>.</summary>
    public string? Mode { get; init; }

    /// <summary>The reasoning effort, as an id from <see cref="Options"/>.</summary>
    public string? Effort { get; init; }

    /// <summary>What the session can be switched to, as it last reported — null before it has.</summary>
    public SessionOptionsReported? Options { get; init; }

    /// <summary>The agent's own handle on this conversation, as it last reported it.</summary>
    public string? ResumeToken { get; init; }

    /// <summary>The newest checkpoint, which is what the next one is compared against.</summary>
    public string? LatestCheckpointId { get; init; }

    /// <summary>The sequence of the last event folded in.</summary>
    public long LastSequence { get; init; }

    /// <summary>A counter for the ids of entries that have none of their own.</summary>
    public long EntryCounter { get; init; }

    [JsonIgnore]
    public bool IsWorking => ActiveTurnId is not null;

    [JsonIgnore]
    public bool IsWaitingForUser => PendingApprovals.Count > 0 || PendingQuestions.Count > 0;
}

/// <summary>Who wrote a message.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MessageRole>))]
public enum MessageRole
{
    User,
    Assistant,
}

/// <summary>Where a tool call is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ToolCallState>))]
public enum ToolCallState
{
    Running,
    Completed,
    Failed,
    Declined,

    /// <summary>The turn or the session ended while it was still running.</summary>
    Abandoned,
}

/// <summary>One thing in a conversation's timeline.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MessageEntry), "message")]
[JsonDerivedType(typeof(WorkGroupEntry), "work")]
[JsonDerivedType(typeof(ProposedPlanEntry), "plan")]
[JsonDerivedType(typeof(QuestionsEntry), "questions")]
[JsonDerivedType(typeof(CheckpointEntry), "checkpoint")]
[JsonDerivedType(typeof(NoticeEntry), "notice")]
public abstract record TimelineEntry(string Id)
{
    public string? TurnId { get; init; }
    public DateTimeOffset At { get; init; }
}

/// <summary>A message from the user or the assistant.</summary>
public sealed record MessageEntry(
    string Id,
    MessageRole Role,
    string Text,
    bool IsStreaming,
    IReadOnlyList<ImageAttachment> Images) : TimelineEntry(Id);

/// <summary>
/// Everything the agent did between two messages, together — the unit a view collapses.
/// </summary>
public sealed record WorkGroupEntry(string Id, ImmutableList<WorkItem> Items) : TimelineEntry(Id);

/// <summary>A plan the agent wrote for approval.</summary>
public sealed record ProposedPlanEntry(string Id, string Markdown) : TimelineEntry(Id);

/// <summary>A round of questions that has been answered (or dismissed, where <see cref="Answers"/> is
/// null), kept where it was asked.</summary>
public sealed record QuestionsEntry(
    string Id,
    IReadOnlyList<UserQuestion> Questions,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Answers) : TimelineEntry(Id);

/// <summary>What a turn changed on disk.</summary>
/// <param name="Id">The checkpoint taken as the turn ended.</param>
/// <param name="BaseCheckpointId">The one taken as it began — what "undo this turn" restores.</param>
/// <param name="Restored">Whether the files have since been put back to the start of this turn.</param>
public sealed record CheckpointEntry(
    string Id,
    string BaseCheckpointId,
    IReadOnlyList<ChangedFile> Files,
    bool Restored) : TimelineEntry(Id);

/// <summary>Something said to the user by the session rather than by the agent.</summary>
public sealed record NoticeEntry(string Id, NoticeLevel Level, string Text) : TimelineEntry(Id);

/// <summary>One thing inside a <see cref="WorkGroupEntry"/>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ToolCallItem), "tool")]
[JsonDerivedType(typeof(ReasoningItem), "reasoning")]
[JsonDerivedType(typeof(DecisionItem), "decision")]
public abstract record WorkItem(string Id);

/// <summary>One tool call, as it now stands.</summary>
public sealed record ToolCallItem(
    string Id,
    ToolKind Kind,
    string Name,
    string Title,
    ToolDetail Detail,
    string Output,
    ToolCallState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt) : WorkItem(Id);

/// <summary>What the model thought, where the agent shows it.</summary>
public sealed record ReasoningItem(string Id, string Text) : WorkItem(Id);

/// <summary>How an approval was answered.</summary>
public sealed record DecisionItem(string Id, string Title, ApprovalDecision Decision) : WorkItem(Id);
