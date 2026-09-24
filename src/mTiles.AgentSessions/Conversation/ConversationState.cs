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

    /// <summary>Who the conversation is running as now, or null before any session said.</summary>
    /// <remarks>What the tile reads to put its own chooser back where the conversation left it: the stored
    /// record names the agent, and only this names the instance and the login inside it.</remarks>
    public SessionAccount? Account { get; init; }

    /// <summary>The model last picked for this conversation on the account running it, or null where nobody
    /// picked one.</summary>
    /// <remarks>What reopening restores, rather than <see cref="Model"/>: that is the CLI's own resolution and
    /// would freeze its default of the day. See <see cref="SessionModelChosen"/>.</remarks>
    public string? ChosenModel { get; init; }

    /// <summary>The newest checkpoint, which is what the next one is compared against.</summary>
    public string? LatestCheckpointId { get; init; }

    /// <summary>The sequence of the last event folded in.</summary>
    public long LastSequence { get; init; }

    /// <summary>A counter for the ids of entries that have none of their own.</summary>
    public long EntryCounter { get; init; }

    /// <summary>Every sub-agent this conversation has launched, oldest first, as each now stands.</summary>
    public ImmutableList<SubAgentRun> SubAgents { get; init; } = [];

    /// <summary>Whether a turn is running. Not whether anything is: see <see cref="IsBusy"/>.</summary>
    [JsonIgnore]
    public bool IsWorking => ActiveTurnId is not null;

    /// <summary>How many sub-agents are working now.</summary>
    [JsonIgnore]
    public int WorkingSubAgentCount => SubAgents.Count(s => s.IsWorking);

    /// <summary>Whether anything is working: a turn, or a sub-agent that outlived the turn that launched it.
    /// </summary>
    /// <remarks>The question the spinner, the tile's activity and a restart all ask — a tile that answered it
    /// with <see cref="IsWorking"/> alone went quiet the moment a background sub-agent was launched, and a
    /// restart then killed it without a word.</remarks>
    [JsonIgnore]
    public bool IsBusy => IsWorking || SubAgents.Any(s => s.IsWorking);

    [JsonIgnore]
    public bool IsWaitingForUser => PendingApprovals.Count > 0 || PendingQuestions.Count > 0;
}

/// <summary>Where a sub-agent is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SubAgentStatus>))]
public enum SubAgentStatus
{
    Working,
    Completed,
    Failed,
    Stopped,
}

/// <summary>One sub-agent, as it now stands.</summary>
/// <param name="Id">The agent's own id for it.</param>
/// <param name="ToolCallId">The call that launched it, where known.</param>
/// <param name="Background">Whether it may work past the turn that launched it.</param>
/// <param name="Progress">What it is doing now, as it last said — or null.</param>
/// <param name="Result">What it came to, once it has ended and said.</param>
public sealed record SubAgentRun(
    string Id,
    string Title,
    string? ToolCallId,
    bool Background,
    SubAgentStatus Status,
    string? Progress,
    string? Result,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt)
{
    /// <summary>The turn it was (last) started in.</summary>
    public string? TurnId { get; init; }

    [JsonIgnore]
    public bool IsWorking => Status == SubAgentStatus.Working;
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
[JsonDerivedType(typeof(HandoverEntry), "handover")]
public abstract record TimelineEntry(string Id)
{
    public string? TurnId { get; init; }
    public DateTimeOffset At { get; init; }

    /// <summary>Who the conversation was running as when this happened, or null where nothing had said yet.
    /// </summary>
    /// <remarks><b>Stamped rather than stored.</b> Nothing writes this into an event: the reducer carries
    /// the account forward from the last <see cref="SessionConfigured"/> and marks every entry it appends
    /// with it, so a conversation recorded before any of this existed reads back with nulls instead of a
    /// migration — and one recorded since says, line by line, which agent and which login produced it.
    /// </remarks>
    public SessionAccount? Account { get; init; }
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

/// <summary>Where the work was handed to another agent or another login, and what it was handed with.</summary>
/// <remarks>Stamped with the account that was <i>leaving</i>, like every entry before it: the handover is the
/// last thing that happened in that stretch, and the seam is drawn above the first entry of the next one.
/// </remarks>
public sealed record HandoverEntry(string Id, SessionAccount? From, SessionAccount To, string Brief)
    : TimelineEntry(Id);

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
    DateTimeOffset? CompletedAt) : WorkItem(Id)
{
    /// <summary>The sub-agent this call launched, as it now stands — or null for a call that launched none.
    /// </summary>
    /// <remarks>On the row because that is where the reader looks: a background <c>Agent</c> call is
    /// finished the moment it is made, and a row saying "done" beside a sub-agent that has ten minutes of
    /// work ahead of it is the silence this exists to end.</remarks>
    public SubAgentRun? SubAgent { get; init; }
}

/// <summary>What the model thought, where the agent shows it.</summary>
public sealed record ReasoningItem(string Id, string Text) : WorkItem(Id);

/// <summary>How an approval was answered.</summary>
public sealed record DecisionItem(string Id, string Title, ApprovalDecision Decision) : WorkItem(Id);
