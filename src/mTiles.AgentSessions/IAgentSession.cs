using mTiles.AgentSessions.Events;

namespace mTiles.AgentSessions;

/// <summary>
/// One live conversation with one agent, spoken to in the shared vocabulary.
/// </summary>
/// <remarks>
/// <para>Implemented once per agent, next to that agent's class — this is where Claude Code's control
/// requests, codex's JSON-RPC and opencode's HTTP are hidden. Everything it hears goes out through the
/// <see cref="IAgentEventSink"/> it was built with; everything it is asked to do comes in through these
/// methods.</para>
/// <para><b>No method throws for a reason the user should see.</b> A request the agent refused, a process
/// that died, an approval that arrived too late: each is a <see cref="NoticeRaised"/> or a
/// <see cref="SessionStateChanged"/> on the sink, because the only caller is a view that has to say
/// something either way. An exception out of here is a bug.</para>
/// </remarks>
public interface IAgentSession : IAsyncDisposable
{
    /// <summary>Starts the agent's process and brings the conversation up, resumed where it has a token.
    /// Completes when the session can take a message.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Sends a message. While a turn is running, whether this steers it or queues behind it is
    /// the agent's own behaviour.</summary>
    Task SendAsync(AgentTurnInput input, CancellationToken ct);

    /// <summary>Stops the running turn, if there is one.</summary>
    Task InterruptAsync(CancellationToken ct);

    /// <summary>Answers an approval the session raised.</summary>
    Task RespondToApprovalAsync(string requestId, ApprovalDecision decision, CancellationToken ct);

    /// <summary>Answers — or with null, dismisses — a round of questions the session raised.</summary>
    Task AnswerQuestionsAsync(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers,
        CancellationToken ct);
}

/// <summary>Where a session puts what it hears. Safe to call from any thread.</summary>
public interface IAgentEventSink
{
    void Emit(AgentEvent agentEvent);
}

/// <summary>What the user sends in one message.</summary>
public sealed record AgentTurnInput(string Text, IReadOnlyList<ImageAttachment> Images)
{
    public static AgentTurnInput FromText(string text) => new(text, []);
}

/// <summary>A session whose agent runs as a child process of ours.</summary>
/// <remarks>
/// Separate from <see cref="IAgentSession"/> because it is not true of all of them — a session speaking
/// to something already running has no process to name — and because nothing that drives a conversation
/// needs the answer. The one caller is the tile, which reports it as the root of what it started so the
/// workspace's memory reading covers the agent it is running.
/// <para><c>null</c> until the process is up and again once it has ended: a stale id is a number that
/// now belongs to somebody else's process.</para>
/// </remarks>
public interface IProcessBackedSession
{
    int? ChildProcessId { get; }
}
