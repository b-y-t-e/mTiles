using System.Diagnostics;
using System.Text.Json;
using mTiles.AgentSessions.Events;

namespace mTiles.AgentSessions.Protocols.Acp;

/// <summary>
/// A conversation with an agent that speaks ACP over stdio.
/// </summary>
/// <remarks>
/// <para><b>The protocol, not an agent.</b> What differs between vendors — the command line, which
/// login to use, and the extension methods a vendor adds — is asked of the subclass; the handshake, the
/// prompt, permissions and the update stream are ACP's and are handled here once.</para>
/// <para>Sequence, as t3code runs it: <c>initialize</c> → <c>authenticate</c> (when the agent lists a
/// method and the subclass picks one) → <c>session/load</c> for a stored id where the agent advertises
/// <c>loadSession</c>, else <c>session/new</c>. Updates marked <c>_meta.isReplay</c>, and everything
/// streamed while a load is in flight, are dropped: the history is already on screen from our own store,
/// and replayed it would be drawn twice.</para>
/// <para>The client declares no file-system and no terminal capability, so the agent does its own reads,
/// writes and commands — the same choice t3code makes for Grok.</para>
/// </remarks>
public abstract class AcpAgentSession : IAgentSession, IProcessBackedSession
{
    private readonly ProcessStartInfo _process;
    private readonly string _workingDirectory;
    private readonly string? _resumeSessionId;
    private readonly AcpUpdateMapper _mapper = new();
    private readonly PendingReplies<ApprovalDecision> _approvals = new();
    private readonly PendingReplies<IReadOnlyDictionary<string, IReadOnlyList<string>>?> _questions = new();
    private JsonRpcPeer? _peer;
    private volatile bool _loading;
    private string? _turnId;
    private CancellationTokenSource? _turnCancellation;

    protected AcpAgentSession(ProcessStartInfo process, string workingDirectory, string? resumeSessionId,
        IAgentEventSink sink)
    {
        _process = process;
        _workingDirectory = workingDirectory;
        _resumeSessionId = resumeSessionId;
        Sink = sink;
    }

    protected IAgentEventSink Sink { get; }

    /// <summary>The ACP session id, once the agent has given one.</summary>
    protected string? SessionId { get; private set; }

    protected JsonRpcPeer Peer => _peer ?? throw new InvalidOperationException("The session has not started.");

    /// <summary>The turn in progress, or null.</summary>
    protected string? CurrentTurnId => _turnId;

    /// <inheritdoc />
    public int? ChildProcessId => _peer?.Process.ProcessId;

    public async Task StartAsync(CancellationToken ct)
    {
        _peer = JsonRpcPeer.Start(_process, writesVersion: true);
        _peer.OnNotification = OnNotification;
        _peer.OnRequest = OnRequestAsync;
        _peer.OnStrayLine = OnStrayLine;
        _ = WatchExitAsync(_peer.Process);

        var initialized = await _peer.RequestAsync("initialize", new
        {
            protocolVersion = 1,
            clientCapabilities = new { fs = new { readTextFile = false, writeTextFile = false }, terminal = false },
            clientInfo = new { name = "mtiles", version = ClientVersion },
        }, ct, TimeSpan.FromSeconds(60));

        if (AuthenticationMethod(initialized) is { } method)
            await _peer.RequestAsync("authenticate", new { methodId = method }, ct, TimeSpan.FromSeconds(60));

        var canLoad = initialized.Prop("agentCapabilities").Bool("loadSession") == true;
        JsonElement session;
        if (_resumeSessionId is { Length: > 0 } resume && canLoad)
        {
            _loading = true;
            try
            {
                session = await _peer.RequestAsync("session/load",
                    new { sessionId = resume, cwd = _workingDirectory, mcpServers = Array.Empty<object>() },
                    ct, TimeSpan.FromSeconds(90));
                SessionId = resume;
            }
            catch (JsonRpcException ex)
            {
                Sink.Emit(new NoticeRaised(NoticeLevel.Warning,
                    $"The previous conversation could not be resumed ({ex.Message}); a new one was started."));
                session = await NewSessionAsync(ct);
            }
            finally
            {
                _loading = false;
            }
        }
        else
        {
            session = await NewSessionAsync(ct);
        }

        await AfterSessionStartedAsync(session, ct);
        Sink.Emit(new SessionOptionsReported(
            [
                .. session.Prop("models").Items("availableModels")
                    .Where(m => m.Str("modelId") is not null)
                    .Select(m => new SessionOption(m.Str("modelId")!, m.Str("name") ?? m.Str("modelId")!, m.Str("description"))),
            ],
            ModeOptions,
            EffortOptions));
        Sink.Emit(new SessionConfigured(
            CurrentModel ?? session.Prop("models").Str("currentModelId"),
            CurrentMode,
            SessionId,
            CurrentEffort));
        Sink.Emit(new SessionStateChanged(AgentSessionState.Ready));
    }

    /// <summary>
    /// Switches the model through <c>session/set_model</c>; a mode or an effort needs a restart, because
    /// what they are is the vendor's command line or extension, which a subclass answers for.
    /// </summary>
    public virtual async Task<SettingsChangeOutcome> ChangeSettingsAsync(SessionSettings settings, CancellationToken ct)
    {
        if (settings.Mode is not null || settings.Effort is not null) return SettingsChangeOutcome.NeedsRestart;
        if (settings.Model is not { Length: > 0 } model) return SettingsChangeOutcome.Applied;
        if (SessionId is null) return SettingsChangeOutcome.NeedsRestart;

        try
        {
            await Peer.RequestAsync("session/set_model", new { sessionId = SessionId, modelId = model }, ct,
                TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is JsonRpcException or TimeoutException)
        {
            Sink.Emit(new NoticeRaised(NoticeLevel.Warning, $"The agent did not switch to {model}: {ex.Message}"));
            return SettingsChangeOutcome.Rejected;
        }

        Sink.Emit(new SessionConfigured(model, null, null) { TurnId = _turnId });
        return SettingsChangeOutcome.Applied;
    }

    /// <summary>The permission modes this session offers, as ids a viewer sends back. None by default.</summary>
    protected virtual IReadOnlyList<SessionOption> ModeOptions => [];

    /// <summary>The efforts this session offers. None by default.</summary>
    protected virtual IReadOnlyList<SessionOption> EffortOptions => [];

    /// <summary>The model asked for at launch, where the subclass knows it better than the agent's answer.</summary>
    protected virtual string? CurrentModel => null;

    /// <summary>The mode this session runs in, as one of <see cref="ModeOptions"/>.</summary>
    protected virtual string? CurrentMode => null;

    /// <summary>The effort this session runs at, as one of <see cref="EffortOptions"/>.</summary>
    protected virtual string? CurrentEffort => null;

    public async Task SendAsync(AgentTurnInput input, CancellationToken ct)
    {
        if (SessionId is null) return;

        // ACP has no steering: a prompt sent while one runs replaces it, which is t3code's order too.
        // The replaced turn is closed here rather than by its prompt's answer: that answer arrives after the
        // new turn has taken its place, and would then find nothing of its own to close.
        if (_turnId is { } replaced)
        {
            await InterruptAsync(ct);
            CompleteTurn(replaced, TurnOutcome.Interrupted);
        }

        var turnId = $"turn-{Guid.NewGuid():N}";
        _turnId = turnId;
        _mapper.CloseSegment();
        var cancellation = new CancellationTokenSource();
        _turnCancellation = cancellation;
        Sink.Emit(new TurnStarted { TurnId = turnId });

        var prompt = new List<object> { new { type = "text", text = input.Text } };
        prompt.AddRange(input.Images.Select(image => (object)new
        {
            type = "image", data = image.Base64Data, mimeType = image.MimeType,
        }));

        _ = RunPromptAsync(turnId, PromptParameters(SessionId, prompt), cancellation.Token);
    }

    public async Task InterruptAsync(CancellationToken ct)
    {
        if (_peer is null || SessionId is null || _turnId is null) return;
        _approvals.AbandonAll(ApprovalDecision.Cancel);
        _questions.AbandonAll(null);
        await _peer.NotifyAsync("session/cancel", new { sessionId = SessionId }, ct);
    }

    public Task RespondToApprovalAsync(string requestId, ApprovalDecision decision, CancellationToken ct)
    {
        _approvals.Resolve(requestId, decision);
        return Task.CompletedTask;
    }

    public Task AnswerQuestionsAsync(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers,
        CancellationToken ct)
    {
        _questions.Resolve(requestId, answers);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _approvals.AbandonAll(ApprovalDecision.Cancel);
        _questions.AbandonAll(null);
        _turnCancellation?.Cancel();
        if (_peer is not null) await _peer.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>What this client calls itself in <c>initialize</c>.</summary>
    protected virtual string ClientVersion => "1";

    /// <summary>The login to authenticate with, chosen from what <c>initialize</c> offered, or null to
    /// skip authentication.</summary>
    protected virtual string? AuthenticationMethod(JsonElement initializeResult) => null;

    /// <summary>A chance to configure the new or loaded session — a model, a mode.</summary>
    protected virtual Task AfterSessionStartedAsync(JsonElement session, CancellationToken ct) => Task.CompletedTask;

    /// <summary>The parameters of <c>session/prompt</c>; a vendor adds its <c>_meta</c> here.</summary>
    protected virtual object PromptParameters(string sessionId, IReadOnlyList<object> prompt) =>
        new { sessionId, prompt };

    /// <summary>A request that is not ACP's own. Answer, or throw a <see cref="JsonRpcException"/> with
    /// <see cref="JsonRpcPeer.MethodNotFound"/>.</summary>
    protected virtual Task<object?> OnExtensionRequestAsync(string method, JsonElement parameters) =>
        throw new JsonRpcException(JsonRpcPeer.MethodNotFound, $"Method not found: {method}");

    /// <summary>A notification that is not ACP's own.</summary>
    protected virtual void OnExtensionNotification(string method, JsonElement parameters)
    {
    }

    /// <summary>A line on stdout that was not JSON — some agents print a sign-in link there.</summary>
    protected virtual void OnStrayLine(string line) =>
        Trace.TraceInformation($"[AgentSessions] {GetType().Name}: {line}");

    /// <summary>Finishes the current turn, once: for a vendor whose completion can arrive by another
    /// route than the prompt's own answer.</summary>
    protected void CompleteTurn(string turnId, TurnOutcome outcome, string? error = null)
    {
        if (Interlocked.CompareExchange(ref _turnId, null, turnId) != turnId) return;
        _approvals.AbandonAll(ApprovalDecision.Cancel);
        _questions.AbandonAll(null);
        _mapper.CloseSegment();
        Sink.Emit(new TurnCompleted(outcome, error) { TurnId = turnId });
    }

    /// <summary>Asks the user a round of questions and waits for the answers — null when dismissed or
    /// when the turn ended first.</summary>
    protected async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> AskAsync(
        IReadOnlyList<UserQuestion> questions)
    {
        var requestId = $"questions-{Guid.NewGuid():N}";
        // Waiting before it is announced: a viewer may answer from inside the announcement itself,
        // and an answer that finds nothing waiting is lost and the turn hangs.
        var pending = _questions.WaitAsync(requestId, CancellationToken.None);
        Sink.Emit(new QuestionsAsked(requestId, questions) { TurnId = _turnId });
        var answers = await pending;
        Sink.Emit(new QuestionsAnswered(requestId, answers) { TurnId = _turnId });
        return answers;
    }

    private async Task<JsonElement> NewSessionAsync(CancellationToken ct)
    {
        var session = await Peer.RequestAsync("session/new",
            new { cwd = _workingDirectory, mcpServers = Array.Empty<object>() }, ct, TimeSpan.FromSeconds(90));
        SessionId = session.Str("sessionId");
        return session;
    }

    private async Task RunPromptAsync(string turnId, object parameters, CancellationToken ct)
    {
        try
        {
            var result = await Peer.RequestAsync("session/prompt", parameters, ct);
            if (result.Prop("usage") is { } usage)
                Sink.Emit(new UsageUpdated(new TokenUsage(null, null,
                    ((JsonElement?)usage).Long("inputTokens"), ((JsonElement?)usage).Long("outputTokens")))
                    { TurnId = turnId });

            CompleteTurn(turnId, result.Str("stopReason") switch
            {
                "cancelled" => TurnOutcome.Interrupted,
                "refusal" => TurnOutcome.Failed,
                _ => TurnOutcome.Completed,
            }, result.Str("stopReason") == "refusal" ? "The agent refused to continue." : null);
        }
        catch (JsonRpcException ex)
        {
            CompleteTurn(turnId, TurnOutcome.Failed, ex.Message);
        }
        catch (OperationCanceledException)
        {
            CompleteTurn(turnId, TurnOutcome.Interrupted);
        }
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        if (method != "session/update")
        {
            OnExtensionNotification(method, parameters);
            return;
        }

        if (_loading || parameters.Prop("_meta").Bool("isReplay") == true) return;
        if (SessionId is not null && parameters.Str("sessionId") is { } id && id != SessionId) return;
        if (parameters.Prop("update") is not { } update) return;

        foreach (var e in _mapper.Map(update, _turnId)) Sink.Emit(e);
    }

    private Task<object?> OnRequestAsync(string method, JsonElement parameters) => method switch
    {
        "session/request_permission" => RequestPermissionAsync(parameters),
        _ => OnExtensionRequestAsync(method, parameters),
    };

    private async Task<object?> RequestPermissionAsync(JsonElement parameters)
    {
        var call = parameters.Prop("toolCall");
        var offered = parameters.Items("options")
            .Select(o => (Id: o.Str("optionId"), Kind: o.Str("kind"), Name: o.Str("name")))
            .Where(o => o.Id is not null)
            .ToList();

        var options = new List<ApprovalOption>();
        var byDecision = new Dictionary<ApprovalDecision, string>();
        foreach (var (id, kind, name) in offered)
        {
            ApprovalDecision? decision = kind switch
            {
                "allow_once" => ApprovalDecision.Accept,
                "allow_always" => ApprovalDecision.AcceptForSession,
                "reject_once" => ApprovalDecision.Decline,
                "reject_always" when !byDecision.ContainsKey(ApprovalDecision.Decline) => ApprovalDecision.Decline,
                _ => null,
            };
            if (decision is not { } d || byDecision.ContainsKey(d)) continue;
            byDecision[d] = id!;
            options.Add(new ApprovalOption(d, name ?? d.ToString()));
        }

        options.Add(new ApprovalOption(ApprovalDecision.Cancel, "Stop"));

        var requestId = $"approval-{Guid.NewGuid():N}";
        var kindOfCall = call is { } c ? AcpUpdateMapper.KindOf(c.Str("kind")) : ToolKind.Other;
        var detail = call is { } toolCall ? AcpUpdateMapper.DetailOf(toolCall) : ToolDetail.Empty;
        // Waiting before it is announced: a viewer may answer from inside the announcement itself,
        // and an answer that finds nothing waiting is lost and the turn hangs.
        var pending = _approvals.WaitAsync(requestId, CancellationToken.None);
        Sink.Emit(new ApprovalRequested(requestId,
            kindOfCall switch
            {
                ToolKind.Command => ApprovalKind.Command,
                ToolKind.FileChange => ApprovalKind.FileChange,
                ToolKind.FileRead => ApprovalKind.FileRead,
                _ => ApprovalKind.Other,
            },
            call.Str("title") ?? "The agent asks for permission",
            detail.Command ?? detail.Diff ?? (detail.Paths is { Count: > 0 } paths ? string.Join("\n", paths) : detail.Input),
            call.Str("toolCallId"),
            options) { TurnId = _turnId });
        var answer = await pending;
        Sink.Emit(new ApprovalResolved(requestId, answer) { TurnId = _turnId });

        return answer != ApprovalDecision.Cancel && byDecision.TryGetValue(answer, out var optionId)
            ? new { outcome = new { outcome = "selected", optionId } }
            : new { outcome = new { outcome = "cancelled" } };
    }

    private async Task WatchExitAsync(AgentProcess process)
    {
        var code = await process.Exited;
        _approvals.AbandonAll(ApprovalDecision.Cancel);
        _questions.AbandonAll(null);
        if (_turnId is { } turn)
            CompleteTurn(turn, process.StoppedByUs ? TurnOutcome.Interrupted : TurnOutcome.Failed,
                process.StoppedByUs ? null : "The agent's process ended.");

        var stderr = process.StderrText.Trim();
        Sink.Emit(code is 0 or null || process.StoppedByUs
            ? new SessionStateChanged(AgentSessionState.Stopped)
            : new SessionStateChanged(AgentSessionState.Failed,
                $"The agent exited with code {code}." + (stderr.Length > 0 ? $"\n{AgentProcess.Tail(stderr)}" : "")));
    }
}
