using System.Text.Json.Nodes;
using mTiles.AgentSessions;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Storage;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Agents.Sessions;
using mTiles.Services.Providers;
using mTiles.Services.Tiles;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests;

/// <summary>Prepares no launch, so a start stops with a problem before any process exists — nothing in
/// a test may start whichever CLIs the machine running it happens to have.</summary>
internal sealed class NoSessionStarter : IAgentSessionStarter
{
    public const string Problem = "No agent is started in these tests.";

    public static NoSessionStarter Instance { get; } = new();

    public Task<(AgentSessionLaunch? Launch, string? Problem)> PrepareAsync(AppSettings settings, IAiAgent agent,
        AiAgentInstance instance, string workingDirectory, string conversationId, string? resumeToken,
        CancellationToken ct) =>
        Task.FromResult<(AgentSessionLaunch?, string?)>((null, Problem));

    public IAgentSession Create(IAiAgent agent, AgentSessionLaunch launch, IAgentEventSink sink) =>
        throw new InvalidOperationException(Problem);
}

/// <summary>Reaches a live session without running a CLI: counts the starts and hands back a
/// <see cref="FakeAgentSession"/> (or whatever <see cref="NewSession"/> builds).</summary>
internal sealed class ReadyStarter : IAgentSessionStarter
{
    private int _prepared;
    private TaskCompletionSource? _holdNextStart;
    private FakeAgentSession? _session;

    public int Prepared => Volatile.Read(ref _prepared);

    /// <summary>What the preparation answers instead of a launch, for the tile that never starts.</summary>
    public string? Problem { get; init; }

    /// <summary>Builds each session; a plain <see cref="FakeAgentSession"/> by default.</summary>
    public Func<FakeAgentSession> NewSession { get; init; } = () => new FakeAgentSession();

    /// <summary>The session created last.</summary>
    public FakeAgentSession? Session => Volatile.Read(ref _session);

    /// <summary>Holds the next preparation open until completed — the window a restart has between the old
    /// CLI going and the new one being spawned. Taken once.</summary>
    public TaskCompletionSource? HoldNextStart
    {
        get => Volatile.Read(ref _holdNextStart);
        set => Volatile.Write(ref _holdNextStart, value);
    }

    public async Task<(AgentSessionLaunch? Launch, string? Problem)> PrepareAsync(AppSettings settings,
        IAiAgent agent, AiAgentInstance instance, string workingDirectory, string conversationId,
        string? resumeToken, CancellationToken ct)
    {
        Interlocked.Increment(ref _prepared);
        if (Interlocked.Exchange(ref _holdNextStart, null) is { } held) await held.Task;
        if (Problem is { } refused) return (null, refused);

        return (new AgentSessionLaunch(agent.BinaryName, workingDirectory,
            AgentRuntime.For(settings, instance, agent: agent), new Dictionary<string, string?>(),
            AiBehaviour.ToolDefault, AiEffort.ToolDefault, resumeToken, conversationId), null);
    }

    public IAgentSession Create(IAiAgent agent, AgentSessionLaunch launch, IAgentEventSink sink)
    {
        var session = NewSession();
        session.Bind(sink);
        Volatile.Write(ref _session, session);
        return session;
    }
}

/// <summary>A session that runs no process: it says it is ready, records what it is sent and lets a test
/// speak for the agent through <see cref="Say"/>. Every behaviour a test needs different is an init
/// property.</summary>
internal class FakeAgentSession : IAgentSession
{
    private IAgentEventSink? _sink;

    public List<string> Sent { get; } = [];

    /// <summary>Thrown from <see cref="StartAsync"/>, as a CLI that fails to come up.</summary>
    public string? FailStart { get; init; }

    /// <summary>Never announces itself ready.</summary>
    public bool StaysStarting { get; init; }

    /// <summary>Reports an empty set of options at start, as every real session does — the event the host
    /// stamps <c>CanCompact</c> on.</summary>
    public bool ReportsOptions { get; init; }

    /// <summary>Opens a turn and then throws, as codex timing out on <c>turn/start</c>'s reply.</summary>
    public Exception? FailSend { get; init; }

    public SettingsChangeOutcome SettingsOutcome { get; init; } = SettingsChangeOutcome.Applied;

    /// <summary>The outcome per change, where a test needs one setting taken and another not.</summary>
    public Func<SessionSettings, SettingsChangeOutcome>? OutcomeFor { get; init; }

    /// <summary>Emits the <see cref="SessionConfigured"/> an applied change is followed by.</summary>
    public bool AnnouncesSettings { get; init; }

    /// <summary>Reports its process exiting as it is disposed, as a real session does.</summary>
    public bool ReportsStopOnDispose { get; init; }

    public FakeAgentSession Bind(IAgentEventSink sink)
    {
        _sink = sink;
        return this;
    }

    public void Say(AgentEvent e) => _sink!.Emit(e);

    public Task StartAsync(CancellationToken ct)
    {
        if (FailStart is not null) throw new InvalidOperationException(FailStart);
        if (ReportsOptions) _sink?.Emit(new SessionOptionsReported([], [], []));
        if (!StaysStarting) _sink?.Emit(new SessionStateChanged(AgentSessionState.Ready));
        return Task.CompletedTask;
    }

    public Task SendAsync(AgentTurnInput input, CancellationToken ct)
    {
        if (FailSend is not null)
        {
            _sink?.Emit(new TurnStarted { TurnId = "t" });
            throw FailSend;
        }
        Sent.Add(input.Text);
        return Task.CompletedTask;
    }

    public Task<SettingsChangeOutcome> ChangeSettingsAsync(SessionSettings settings, CancellationToken ct)
    {
        var outcome = OutcomeFor?.Invoke(settings) ?? SettingsOutcome;
        if (AnnouncesSettings && outcome == SettingsChangeOutcome.Applied)
            _sink?.Emit(new SessionConfigured(settings.Model, settings.Mode, null, settings.Effort));
        return Task.FromResult(outcome);
    }

    public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;

    public Task RespondToApprovalAsync(string requestId, ApprovalDecision decision, CancellationToken ct) =>
        Task.CompletedTask;

    public Task AnswerQuestionsAsync(string requestId,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? answers, CancellationToken ct) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        if (ReportsStopOnDispose) _sink?.Emit(new SessionStateChanged(AgentSessionState.Stopped));
        return ValueTask.CompletedTask;
    }
}

/// <summary>A session with a route for compaction — Claude Code, codex and opencode.</summary>
internal sealed class CompactingFakeSession : FakeAgentSession, ICompactingSession
{
    private int _compactions;

    public int Compactions => Volatile.Read(ref _compactions);

    public Task CompactAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _compactions);
        return Task.CompletedTask;
    }
}

/// <summary>A conversation store that passes everything to a real one, with hooks for the tests that
/// need a write refused or a read counted.</summary>
internal sealed class TestStore(IConversationStore inner) : IConversationStore
{
    private int _reads;
    private volatile bool _refuseNextAppend;

    /// <summary>A store of its own, in a file no other test shares.</summary>
    public TestStore() : this(TestTiles.ConversationStore())
    {
    }

    /// <summary>How many times a conversation's events were read — how often it was opened.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>Every append throws while this is set.</summary>
    public volatile bool RefusingAppends;

    /// <summary>The next append throws, once.</summary>
    public bool RefuseNextAppend
    {
        get => _refuseNextAppend;
        set => _refuseNextAppend = value;
    }

    /// <summary>Completes when an append has been refused.</summary>
    public TaskCompletionSource Refused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConversationRecord? Find(string conversationId) => inner.Find(conversationId);
    public IReadOnlyList<ConversationSummary> List(string workingDirectory) => inner.List(workingDirectory);
    public void Save(ConversationRecord record) => inner.Save(record);

    public IReadOnlyList<AgentEvent> ReadEvents(string conversationId)
    {
        Interlocked.Increment(ref _reads);
        return inner.ReadEvents(conversationId);
    }

    public long LastSequence(string conversationId) => inner.LastSequence(conversationId);
    public void Delete(string conversationId) => inner.Delete(conversationId);

    public void Append(string conversationId, IReadOnlyList<AgentEvent> events)
    {
        if (RefusingAppends || _refuseNextAppend)
        {
            _refuseNextAppend = false;
            Refused.TrySetResult();
            throw new IOException("database is locked");
        }

        inner.Append(conversationId, events);
    }
}

/// <summary>Agent tiles for a test, built the way the application builds them but with no CLI behind
/// them unless the test hands one in.</summary>
internal static class ConversationTiles
{
    /// <summary>A tile built directly, on Claude Code's seeded instance unless one is given.</summary>
    public static AgentConversationTileViewModel New(TempSettings settings, AiAgentInstance? instance = null,
        IConversationStore? store = null, IAgentSessionStarter? starter = null, string? tileId = null,
        Action? requestSave = null)
    {
        var agent = AiAgentCatalog.Find(instance?.AgentId ?? "claude")!;
        instance ??= AiAgentCatalog.SeedInstanceFor(agent);
        // One id for the life of the tile: a fresh one per read names a different conversation every time.
        var id = tileId ?? Guid.NewGuid().ToString();
        return new AgentConversationTileViewModel(Path.GetTempPath(), settings.Service,
            store ?? TestTiles.ConversationStore(), instance, agent, () => id, requestSave: requestSave,
            post: action => action(), sessionStarter: starter ?? NoSessionStarter.Instance);
    }

    /// <summary>A tile built by its kind from saved state, the route a layout and a chooser take.</summary>
    public static AgentConversationTileViewModel FromKind(TempSettings settings, JsonObject state,
        IConversationStore? store = null, IAgentSessionStarter? starter = null, string? tileId = null) =>
        FromKind(new TileContext(Path.GetTempPath(), settings.Service), state, store, starter, tileId);

    /// <summary>The same, in a context of the test's own — a workspace directory, its agent files.</summary>
    public static AgentConversationTileViewModel FromKind(TileContext context, JsonObject state,
        IConversationStore? store = null, IAgentSessionStarter? starter = null, string? tileId = null)
    {
        var id = tileId ?? Guid.NewGuid().ToString();
        return (AgentConversationTileViewModel)((ITileKind)new AgentConversationTileKind(
                store ?? TestTiles.ConversationStore(), starter ?? NoSessionStarter.Instance))
            .Create(context with { TileId = () => id }, state);
    }

    /// <summary>Starts the tile and waits until it has stopped at its launch problem.</summary>
    public static async Task StartUntilRefused(AgentConversationTileViewModel tile)
    {
        tile.EnsureStarted();
        await WaitUntil(() => tile.LaunchProblem is not null, "the tile to stop at its launch problem");
    }

    /// <summary>Starts the tile and waits until it has a live session.</summary>
    public static async Task StartUntilRunning(AgentConversationTileViewModel tile)
    {
        tile.EnsureStarted();
        await WaitUntil(() => !tile.IsStarting, "the tile to finish starting");
        Assert.Null(tile.LaunchProblem);
    }

    /// <summary>Polls until <paramref name="condition"/> holds; fails with <paramref name="what"/> after
    /// the timeout. The deadline is a safety net, never what a passing test waits for.</summary>
    public static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }
}
