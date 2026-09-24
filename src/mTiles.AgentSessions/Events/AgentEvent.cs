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
[JsonDerivedType(typeof(SessionOptionsReported), "session.options")]
[JsonDerivedType(typeof(SessionModelChosen), "session.model-chosen")]
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
[JsonDerivedType(typeof(HandoverRecorded), "handover")]
[JsonDerivedType(typeof(SubAgentStarted), "subagent.started")]
[JsonDerivedType(typeof(SubAgentProgressed), "subagent.progressed")]
[JsonDerivedType(typeof(SubAgentEnded), "subagent.ended")]
public abstract record AgentEvent
{
    /// <summary>Position in the conversation, from 1, assigned by the store. Zero until appended.</summary>
    public long Sequence { get; init; }

    /// <summary>When the host received it.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>The turn this belongs to, where it belongs to one.</summary>
    public string? TurnId { get; init; }

    /// <summary>
    /// Whether this event is worth keeping: false for one that only describes the session running now, which
    /// a viewer is told and the store is not.
    /// </summary>
    /// <remarks>A transient event is numbered, folded into the state and handed to every viewer exactly as any
    /// other; it is simply not appended. Replaying a conversation without it costs nothing, because the next
    /// session reports it again at start, and keeping it costs a great deal: <see cref="SessionOptionsReported"/>
    /// carries a whole model catalogue — hundreds of entries on an opencode installation — so a tile started
    /// thirty times would write megabytes of it into one conversation and parse them back on every open.</remarks>
    [JsonIgnore]
    public virtual bool IsTransient => false;
}

/// <summary>The process behind the session came up, went busy, is waiting on the user, or ended.</summary>
public sealed record SessionStateChanged(AgentSessionState State, string? Detail = null) : AgentEvent;

/// <summary>
/// What the agent is running as — and the id that resumes it. A null field keeps what was last said.
/// </summary>
/// <param name="Model">The model, spelled the way the agent takes it.</param>
/// <param name="Mode">The permission mode, as one of the ids <see cref="SessionOptionsReported.Modes"/>
/// offers — never the agent's own word for it, so a viewer compares like with like.</param>
/// <param name="ResumeToken">The agent's own handle on this conversation, opaque to everything but the
/// agent class that wrote it: a Claude session id, a codex thread id, an ACP session id. The host keeps
/// the latest one beside the conversation, because it is what the next launch is handed.</param>
/// <param name="Effort">The reasoning effort, as one of the ids <see cref="SessionOptionsReported.Efforts"/>
/// offers.</param>
public sealed record SessionConfigured(string? Model, string? Mode, string? ResumeToken, string? Effort = null)
    : AgentEvent
{
    /// <summary>Who the agent is running as, stamped by the host rather than reported by the session.</summary>
    /// <remarks><b>The session cannot answer this and the host can.</b> A session reports what its own CLI
    /// told it — a model, a mode, an id to resume by — and knows nothing of the row in Settings it was
    /// launched from, which is precisely the thing that decides where the resume token lives. Null on every
    /// event written before this existed, which is why every reader treats it as <i>not said</i> rather than
    /// as a change.</remarks>
    public SessionAccount? Account { get; init; }
}

/// <summary>A model somebody picked for this conversation and the session took, written by the host.</summary>
/// <remarks><b>Not the same thing as <see cref="SessionConfigured.Model"/>.</b> That is what the CLI says it
/// runs, which is usually its own resolution of the instance's answer — an empty field or an alias come back as
/// a full id — so restoring it on reopening would pin the CLI's default of that day over every later change in
/// Settings. This names only a choice.</remarks>
/// <param name="Model">The model, spelled the way the agent takes it.</param>
public sealed record SessionModelChosen(string Model) : AgentEvent;

/// <summary>
/// The agent, the configured instance and the login one stretch of a conversation ran as.
/// </summary>
/// <remarks>
/// <para><b>Why the sign-in is part of the identity and not a detail of the instance.</b> The resume token
/// is the CLI's own, and where the CLI keeps it is the account's directory — so the same agent on a second
/// subscription resumes nothing, and a transcript that carries on across that seam is a transcript the
/// model has never seen. What says two stretches are one session is this triple, not the agent alone.</para>
/// <para>The name travels with the ids because it is what a row is called on screen, and an instance the
/// user has since deleted or renamed must still be namable in a conversation that ran on it.</para>
/// </remarks>
/// <param name="AgentId">The CLI, as <c>AiAgentCatalog</c> keys it.</param>
/// <param name="InstanceId">The configured instance, or null where nothing configured it.</param>
/// <param name="InstanceName">What that instance was called when it ran.</param>
/// <param name="SignInId">The CLI's own login, or null for its default account.</param>
public sealed record SessionAccount(
    string AgentId,
    string? InstanceId = null,
    string? InstanceName = null,
    string? SignInId = null)
{
    /// <summary>Whether two stretches of a conversation ran as the same agent, instance and login.</summary>
    /// <remarks>By id and never by name: a renamed instance is the same account, and two rows seeded with
    /// one provider's display name are two identically spelled accounts.</remarks>
    public bool IsSameAs(SessionAccount? other) =>
        other is not null && other.AgentId == AgentId && other.InstanceId == InstanceId
        && other.SignInId == SignInId;

    /// <summary>Whether two stretches ran on the same login of the same agent, whatever instance carried it.
    /// </summary>
    /// <remarks>The narrower question the seam and the switch confirmation both ask: the resume token lives in
    /// the login's directory, so another instance on the same login (another key, another model) resumes
    /// perfectly well and is no break in what the model remembers.</remarks>
    public bool SharesLoginWith(SessionAccount? other) =>
        other is not null && other.AgentId == AgentId && other.SignInId == SignInId;
}

/// <summary>
/// What this session can be switched to while it runs: models, permission modes and efforts.
/// </summary>
/// <remarks>Reported by the session, because only the agent knows its catalogue — Claude Code lists its
/// models in the answer to <c>initialize</c>, codex answers <c>model/list</c>, opencode names every
/// provider's models, and an agent that lists nothing reports an empty list, where a viewer still accepts
/// a model typed by hand. Every report replaces the last.</remarks>
public sealed record SessionOptionsReported(
    IReadOnlyList<SessionOption> Models,
    IReadOnlyList<SessionOption> Modes,
    IReadOnlyList<SessionOption> Efforts) : AgentEvent
{
    /// <summary>Whether this session can be asked to compact its own context.</summary>
    /// <remarks><b>Stamped by the host, not reported by the session</b> — the same division
    /// <see cref="SessionConfigured.Account"/> makes, and for a plainer reason: the answer is whether the
    /// object the host is holding implements <see cref="ICompactingSession"/>, so a session saying it
    /// separately is a second copy of one fact that can disagree with the method that is actually called.
    /// False on a session that cannot, which is what keeps the control off the three agents nobody has
    /// measured a route for.</remarks>
    public bool CanCompact { get; init; }

    /// <summary>Never stored: it describes the session running now, and the next one says it again.</summary>
    public override bool IsTransient => true;
}

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
    IReadOnlyList<ApprovalOption> Options) : AgentEvent
{
    /// <summary>The sub-agent asking, where it is one rather than the agent itself.</summary>
    /// <remarks>What keeps the request on screen after the turn that launched a background sub-agent has
    /// ended: that turn's end closes what <i>it</i> left open, and a sub-agent still working is waiting on
    /// this answer. See <see cref="SubAgentStarted"/>.</remarks>
    public string? SubAgentId { get; init; }
}

/// <summary>An approval was answered — by the user, by a rule, or by the session ending.</summary>
public sealed record ApprovalResolved(string RequestId, ApprovalDecision Decision) : AgentEvent;

/// <summary>The agent asked the user one or more questions and waits for the answers.</summary>
public sealed record QuestionsAsked(string RequestId, IReadOnlyList<UserQuestion> Questions) : AgentEvent
{
    /// <summary>The sub-agent asking, where it is one — see <see cref="ApprovalRequested.SubAgentId"/>.</summary>
    public string? SubAgentId { get; init; }
}

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

/// <summary>
/// The work was handed to another agent, or to another login, together with the brief it was handed with.
/// </summary>
/// <remarks>
/// <para><b>The seam is recorded rather than hidden.</b> A conversation drawn as one unbroken column across
/// a change of agent claims a continuity that does not exist: the transcript is ours and survives, the
/// outgoing CLI's memory of it does not. This is the event that says where the cut is, and it carries the
/// brief so that what the next agent was actually told can be read back — by the user now, and by whoever
/// is working out later why the second agent believed what it did.</para>
/// <para><b>It is also the one thing that invalidates the resume token.</b> The token is the issuing CLI's
/// and means nothing to the agent arriving; handed on, codex would be given a Claude session id, which
/// opens an interactive picker no launch chain can answer, and agy would warn, silently start a fresh
/// conversation and exit 0. So the reducer clears the token here, and the host clears it on the record.
/// </para>
/// </remarks>
/// <param name="From">The account the work was done as, or null where nothing had said.</param>
/// <param name="To">The account it was handed to.</param>
/// <param name="Brief">What the next agent was told, verbatim — <see cref="Conversation.ConversationHandover"/>
/// folds it out of what this application recorded, and anything the outgoing agent added is in it too.</param>
public sealed record HandoverRecorded(SessionAccount? From, SessionAccount To, string Brief) : AgentEvent;

/// <summary>
/// A sub-agent the agent launched began working — or, where the agent keeps it, began working again.
/// </summary>
/// <remarks>
/// <para><b>Why a sub-agent is not only a tool call.</b> A sub-agent run in the foreground is: its tool call
/// is open for as long as it works, the turn is open with it, and the spinner says so. One run in the
/// background is not — Claude Code answers the <c>Agent</c> call with "launched" at once and ends the turn
/// while the sub-agent goes on for minutes, and a codex sub-agent is a thread of its own that outlives the
/// turn that spawned it. Measured against Claude Code 2.1.281 (2026-09-24): the tile went quiet, Stop turned
/// back into Send, and the agent then woke on its own when the sub-agent finished, with nothing on screen
/// saying any of it. This is what says it — the tile is busy while any sub-agent is working, whether or not
/// a turn is open.</para>
/// <para><b>What a sub-agent does is not drawn in the conversation</b>, the rule t3code keeps too: its
/// messages and tool calls are the inside of one piece of the agent's work, and interleaved with the
/// agent's own they would read as the agent's. What surfaces is one line of progress
/// (<see cref="SubAgentProgressed"/>) and the result it ended with.</para>
/// </remarks>
/// <param name="SubAgentId">The agent's own id for it — a Claude Code task id, a codex thread id. Stable
/// across its start, its progress and its end.</param>
/// <param name="Title">What it was launched to do, readable.</param>
/// <param name="ToolCallId">The tool call that launched it, where known, so the row of that call can say
/// how the sub-agent is getting on after the call itself has finished.</param>
/// <param name="Background">Whether it works past the turn that launched it. A foreground one cannot, so
/// the end of that turn ends it; a background one ends only when it says so, or with the session.</param>
public sealed record SubAgentStarted(string SubAgentId, string Title, string? ToolCallId = null, bool Background = false)
    : AgentEvent;

/// <summary>What a working sub-agent is doing now — one line, replacing the last.</summary>
/// <remarks>Never stored: it is the running session's news, the sub-agent's end carries what it came to, and a
/// sub-agent busy for ten minutes would otherwise write a row into the conversation for every tool it ran.
/// </remarks>
public sealed record SubAgentProgressed(string SubAgentId, string Progress) : AgentEvent
{
    public override bool IsTransient => true;
}

/// <summary>A sub-agent stopped working: it finished, failed, or was stopped.</summary>
/// <param name="Result">What it said it came to, where the agent reports it.</param>
public sealed record SubAgentEnded(string SubAgentId, SubAgentOutcome Outcome, string? Result = null) : AgentEvent;
