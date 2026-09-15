using System.Text.Json.Serialization;

namespace mTiles.AgentSessions.Events;

/// <summary>Where a session's process is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentSessionState>))]
public enum AgentSessionState
{
    Starting,
    Ready,
    Running,
    WaitingForUser,
    Stopped,
    Failed,
}

/// <summary>How a turn ended.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TurnOutcome>))]
public enum TurnOutcome
{
    Completed,
    Interrupted,
    Failed,
}

/// <summary>
/// What a tool call is, so it can be drawn the right way — never how the agent names it.
/// </summary>
/// <remarks>A closed list on purpose: a browser and a desktop view each switch on it, and a kind added
/// here is a kind both of them have to learn. Anything unclassified is <see cref="Other"/> and draws as a
/// title and its output, which is always legible.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ToolKind>))]
public enum ToolKind
{
    Command,
    FileRead,
    FileChange,
    Search,
    WebFetch,
    Mcp,
    SubAgent,
    Other,
}

/// <summary>How a tool call ended.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ToolStatus>))]
public enum ToolStatus
{
    Completed,
    Failed,
    Declined,
}

/// <summary>What an approval is about.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApprovalKind>))]
public enum ApprovalKind
{
    Command,
    FileChange,
    FileRead,
    Other,
}

/// <summary>
/// The four answers to "may I?", and the only four.
/// </summary>
/// <remarks>Every agent's own vocabulary — <c>allow_once</c>, <c>always</c>, <c>behavior: deny</c> —
/// maps onto these in its own class, and an agent that has no word for one of them simply does not offer
/// it in <see cref="ApprovalRequested.Options"/>.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ApprovalDecision>))]
public enum ApprovalDecision
{
    /// <summary>Just this once.</summary>
    Accept,

    /// <summary>This and anything like it, until the session ends.</summary>
    AcceptForSession,

    /// <summary>No, but carry on.</summary>
    Decline,

    /// <summary>No, and stop the turn.</summary>
    Cancel,
}

/// <summary>How loud a notice is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NoticeLevel>))]
public enum NoticeLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>Where a step of the agent's plan is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PlanStepStatus>))]
public enum PlanStepStatus
{
    Pending,
    InProgress,
    Completed,
}

/// <summary>How a file changed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FileChangeKind>))]
public enum FileChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
}

/// <summary>An image sent with a message, as bytes — a browser and a CLI both want it that way.</summary>
public sealed record ImageAttachment(string MimeType, string Base64Data, string? Name = null);

/// <summary>
/// Everything worth showing about one tool call beyond its title. Every field is optional because
/// every agent reports a different subset.
/// </summary>
/// <param name="Command">The shell command, for <see cref="ToolKind.Command"/>.</param>
/// <param name="Paths">The files it touches or reads.</param>
/// <param name="Query">What it searched or fetched.</param>
/// <param name="Diff">A unified diff of what it wrote, when the agent reports one.</param>
/// <param name="Input">The raw input, pretty-printed, as the fallback for everything else.</param>
/// <param name="ExitCode">A command's exit code.</param>
public sealed record ToolDetail(
    string? Command = null,
    IReadOnlyList<string>? Paths = null,
    string? Query = null,
    string? Diff = null,
    string? Input = null,
    int? ExitCode = null)
{
    public static readonly ToolDetail Empty = new();

    /// <summary>This detail with every field the other one has filled in taken from it.</summary>
    public ToolDetail MergedWith(ToolDetail? newer) =>
        newer is null
            ? this
            : new ToolDetail(
                newer.Command ?? Command,
                newer.Paths is { Count: > 0 } ? newer.Paths : Paths,
                newer.Query ?? Query,
                newer.Diff ?? Diff,
                newer.Input ?? Input,
                newer.ExitCode ?? ExitCode);
}

/// <summary>One answer an approval offers.</summary>
/// <param name="Label">What the button says, in the agent's own words where it has them.</param>
public sealed record ApprovalOption(ApprovalDecision Decision, string Label);

/// <summary>One question in a round.</summary>
/// <param name="Id">What the answer is filed under.</param>
/// <param name="Header">A short label, where the agent gives one.</param>
public sealed record UserQuestion(
    string Id,
    string? Header,
    string Text,
    IReadOnlyList<QuestionOption> Options,
    bool MultiSelect,
    bool AllowsCustomAnswer);

/// <summary>One choice a question offers.</summary>
public sealed record QuestionOption(string Label, string? Description = null);

/// <summary>One step of the agent's own to-do list.</summary>
public sealed record PlanStep(string Text, PlanStepStatus Status);

/// <summary>
/// The context in use and what the session has cost. Every figure is nullable, and null is "the agent
/// did not say" — never zero.
/// </summary>
public sealed record TokenUsage(
    long? UsedTokens,
    long? ContextWindow,
    long? InputTokens = null,
    long? OutputTokens = null,
    decimal? CostUsd = null);

/// <summary>One file a turn changed.</summary>
public sealed record ChangedFile(string Path, FileChangeKind Kind, int Additions, int Deletions);
