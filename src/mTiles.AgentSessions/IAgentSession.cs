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

    /// <summary>Switches the model, mode or effort of the running session, or says it cannot.</summary>
    /// <remarks>On <see cref="SettingsChangeOutcome.Applied"/> the session emits a
    /// <see cref="SessionConfigured"/> saying what it now runs as. <see cref="SettingsChangeOutcome.NeedsRestart"/>
    /// changes nothing: whoever built the session starts a new one with the change, on the same
    /// conversation, because only they hold the launch. The host asks for one setting per call, so an outcome
    /// never has to describe a change that was half taken.</remarks>
    Task<SettingsChangeOutcome> ChangeSettingsAsync(SessionSettings settings, CancellationToken ct);
}

/// <summary>Where a session puts what it hears. Safe to call from any thread.</summary>
public interface IAgentEventSink
{
    void Emit(AgentEvent agentEvent);
}

/// <summary>What the user sends in one message.</summary>
/// <remarks>The images are numbered by the <see cref="ImageMarkers"/> in the text: the first is
/// <c>[Image #1]</c>.</remarks>
public sealed record AgentTurnInput(string Text, IReadOnlyList<ImageAttachment> Images)
{
    public static AgentTurnInput FromText(string text) => new(text, []);

    /// <summary>The message in the order it is said, each piece in a protocol's own shape — see
    /// <see cref="ImageMarkers.Interleave"/>, which also guarantees at least one.</summary>
    public List<T> Blocks<T>(Func<string, T> text, Func<ImageAttachment, T> image) =>
        [.. ImageMarkers.Interleave(Text, Images).Select(part => part.Map(text, image))];
}

/// <summary>A session whose agent can be asked to compact its own context.</summary>
/// <remarks>
/// <para>Separate from <see cref="IAgentSession"/> because it is not true of all of them, and because
/// what it costs to be wrong about is a button that is there and does nothing. Three of the six answer
/// it, each by a route of its own, measured 2026-09-20:</para>
/// <list type="bullet">
/// <item><b>Claude Code</b> — <c>/compact</c> as an ordinary text message on the stream-json stdin. It
/// runs as a turn and comes back as <c>system/status compacting</c>, a <c>compact_boundary</c> whose
/// <c>compact_metadata.trigger</c> is <c>manual</c>, and a <c>result</c>.</item>
/// <item><b>codex</b> — <c>thread/compact/start</c> with <c>{threadId}</c>, which answers <c>{}</c> at
/// once and then runs a whole turn of its own: <c>turn/started</c>, an item of type
/// <c>contextCompaction</c>, <c>turn/completed</c>.</item>
/// <item><b>opencode</b> — <c>POST session/{id}/summarize</c> with <c>{providerID, modelID}</c>, which
/// answers <c>true</c>, takes the session busy and emits <c>session.compacted</c> before going idle.
/// The model is required and is the session's own: a model the server does not know is a 500 and a
/// <c>session.error</c>.</item>
/// </list>
/// <para><b>pi, agy and Grok answer nothing and the button is not drawn for them.</b> Neither pi's nor
/// agy's CLI is on the machine this was measured on, and ACP — which Grok speaks — has no compaction in
/// the protocol at all. A guessed route here is a control that reports having done something to
/// somebody's context window when it has not.</para>
/// <para>Whether the button is offered is not asked of this interface by the view: the host stamps the
/// answer onto <see cref="Events.SessionOptionsReported"/>, so a viewer that is not this window gets it
/// over the wire like every other thing a session can do.</para>
/// </remarks>
public interface ICompactingSession
{
    /// <summary>Asks the agent to summarise the conversation so far and carry on from the summary.</summary>
    /// <remarks>Opens a turn where the agent's own protocol does not announce one, so the tile says
    /// Working for as long as it takes — compaction is a model call and is not quick.</remarks>
    Task CompactAsync(CancellationToken ct);
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
