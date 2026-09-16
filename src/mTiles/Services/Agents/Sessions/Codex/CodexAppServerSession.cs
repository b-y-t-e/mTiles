using System.Text.Json;
using mTiles.AgentSessions;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Protocols;
using mTiles.Models;

namespace mTiles.Services.Agents.Sessions.Codex;

/// <summary>
/// A conversation with codex through <c>codex app-server</c>, its JSON-RPC interface.
/// </summary>
/// <remarks>
/// <para><b>Read against codex-cli 0.153.2's generated schema and t3code's client.</b> JSON-RPC over
/// stdio with <em>no</em> <c>"jsonrpc"</c> field; <c>initialize</c> then the <c>initialized</c>
/// notification; <c>thread/resume</c> for a stored thread id and <c>thread/start</c> otherwise;
/// <c>turn/start</c> per message and <c>turn/interrupt</c> to stop. Approvals are requests codex sends
/// us — <c>item/commandExecution/requestApproval</c> and <c>item/fileChange/requestApproval</c> — answered
/// <c>{"decision":"accept"|"acceptForSession"|"decline"|"cancel"}</c>.</para>
/// <para><b>The session id is codex's thread id</b>, which the app server hands back at once — unlike the
/// terminal tile, which has to find it in a rollout file after the fact.</para>
/// <para>A <c>turn/start</c> sent while a turn runs is queued by codex (t3code), so a second message is
/// counted and becomes the next turn when the running one completes.</para>
/// </remarks>
public sealed class CodexAppServerSession(AgentSessionLaunch launch, CodexAgent agent, IAgentEventSink sink)
    : IAgentSession, IProcessBackedSession
{
    // What the next turn runs as. Codex takes all three on every turn/start, so a change is a new value here
    // and nothing is restarted.
    private string _model = launch.Model;
    private AiBehaviour _behaviour = launch.Behaviour;
    private AiEffort _effort = launch.Effort;
    private readonly PendingReplies<ApprovalDecision> _approvals = new();
    private readonly PendingReplies<IReadOnlyDictionary<string, IReadOnlyList<string>>?> _questions = new();
    private readonly Lock _turnGate = new();
    private JsonRpcPeer? _peer;
    private string? _threadId;
    private string? _turnId;
    private string? _codexTurnId;
    private int _queuedTurns;

    /// <inheritdoc />
    public int? ChildProcessId => _peer?.Process.ProcessId;

    public async Task StartAsync(CancellationToken ct)
    {
        var peer = JsonRpcPeer.Start(launch.StartInfo(["app-server", .. launch.ExtraArgs]), writesVersion: false);
        _peer = peer;
        peer.OnNotification = OnNotification;
        peer.OnRequest = OnRequestAsync;
        _ = WatchExitAsync(peer.Process);

        await peer.RequestAsync("initialize", new
        {
            clientInfo = new { name = "mtiles", title = "mTiles", version = "1" },
            capabilities = new { experimentalApi = true },
        }, ct, TimeSpan.FromSeconds(60));
        await peer.NotifyAsync("initialized", null, ct);

        var (approval, sandbox) = CodexAgent.AppServerPermissions(launch.Behaviour);
        JsonElement started;
        try
        {
            started = launch.ResumeToken is { Length: > 0 } threadId
                ? await peer.RequestAsync("thread/resume",
                    ThreadParameters(threadId, approval, sandbox), ct, TimeSpan.FromSeconds(90))
                : await StartThreadAsync(approval, sandbox, ct);
        }
        catch (JsonRpcException ex) when (launch.ResumeToken is not null)
        {
            sink.Emit(new NoticeRaised(NoticeLevel.Warning,
                $"The previous conversation could not be resumed ({ex.Message}); a new one was started."));
            started = await StartThreadAsync(approval, sandbox, ct);
        }

        _threadId = started.Prop("thread").Str("id");
        await ReportOptionsAsync(peer, ct);
        sink.Emit(new SessionConfigured(started.Str("model"), SessionSettingOptions.ModeId(_behaviour), _threadId,
            SessionSettingOptions.EffortId(_effort)));
        sink.Emit(new SessionStateChanged(AgentSessionState.Ready));
    }

    /// <summary>
    /// codex's models from <c>model/list</c> (codex-cli 0.153.2's schema: <c>data[]</c> of <c>model</c>,
    /// <c>displayName</c>, <c>description</c>, <c>hidden</c>), beside the modes and efforts this agent supports.
    /// </summary>
    /// <remarks>A list that cannot be read is an empty one: the model field still takes a name typed by hand.</remarks>
    private async Task ReportOptionsAsync(JsonRpcPeer peer, CancellationToken ct)
    {
        List<SessionOption> models = [];
        try
        {
            var listed = await peer.RequestAsync("model/list", new { }, ct, TimeSpan.FromSeconds(20));
            models.AddRange(listed.Items("data")
                .Where(m => m.Prop("hidden") is not { ValueKind: JsonValueKind.True })
                .Select(m => m.Str("model") ?? m.Str("id"))
                .OfType<string>()
                .Distinct()
                .Select(id => new SessionOption(id, id)));
        }
        catch (Exception ex) when (ex is JsonRpcException or TimeoutException)
        {
        }

        var instance = launch.Runtime.Instance;
        sink.Emit(new SessionOptionsReported(models, SessionSettingOptions.Modes(agent, instance),
            SessionSettingOptions.Efforts(agent, instance)));
    }

    /// <summary>Taken by the next <c>turn/start</c>, which carries the model, the effort and the permissions.</summary>
    public Task<SettingsChangeOutcome> ChangeSettingsAsync(SessionSettings settings, CancellationToken ct)
    {
        lock (_turnGate)
        {
            if (settings.Model is { Length: > 0 } model) _model = model;
            if (SessionSettingOptions.ParseMode(settings.Mode) is { } mode) _behaviour = mode;
            if (SessionSettingOptions.ParseEffort(settings.Effort) is { } effort) _effort = effort;
        }

        sink.Emit(new SessionConfigured(settings.Model, settings.Mode, null, settings.Effort));
        return Task.FromResult(SettingsChangeOutcome.Applied);
    }

    public async Task SendAsync(AgentTurnInput input, CancellationToken ct)
    {
        if (_peer is null || _threadId is null) return;

        bool startsTurn;
        lock (_turnGate)
        {
            startsTurn = _turnId is null;
            if (startsTurn) BeginTurn();
            else _queuedTurns++;
        }

        var items = new List<object> { new { type = "text", text = input.Text } };
        items.AddRange(input.Images.Select(image =>
            (object)new { type = "image", url = $"data:{image.MimeType};base64,{image.Base64Data}" }));

        string model;
        AiBehaviour behaviour;
        AiEffort currentEffort;
        lock (_turnGate) (model, behaviour, currentEffort) = (_model, _behaviour, _effort);

        var (approval, sandbox) = CodexAgent.AppServerPermissions(behaviour);
        var parameters = new Dictionary<string, object?>
        {
            ["threadId"] = _threadId,
            ["input"] = items,
        };
        if (model.Length > 0) parameters["model"] = model;
        if (AiEfforts.Name(AiEfforts.RoundToNearest(currentEffort, CodexAgent.AppServerEfforts)) is { } effort)
            parameters["effort"] = effort;
        if (approval is not null) parameters["approvalPolicy"] = approval;
        if (sandbox is not null) parameters["sandboxPolicy"] = new { type = SandboxPolicyType(sandbox) };

        try
        {
            var result = await _peer.RequestAsync("turn/start", parameters, ct, TimeSpan.FromSeconds(60));
            lock (_turnGate) _codexTurnId ??= result.Prop("turn").Str("id");
        }
        catch (Exception ex) when (ex is JsonRpcException or TimeoutException && startsTurn)
        {
            // Closed here as well as in the host: a turn only the host closed stays open in this session, and
            // the next message would be counted as queued behind a turn nothing is running.
            EndTurn(TurnOutcome.Failed, ex.Message);
        }
        catch (Exception ex) when (ex is JsonRpcException or TimeoutException)
        {
            RefuseQueuedMessage(ex.Message);
        }
    }

    /// <summary>
    /// A message refused while another turn runs takes back its place in the queue and says so; the running
    /// turn is still codex's, and ending it would cancel its approvals and hand its completion to a new turn.
    /// </summary>
    private void RefuseQueuedMessage(string error)
    {
        lock (_turnGate)
            if (_queuedTurns > 0) _queuedTurns--;
        sink.Emit(new NoticeRaised(NoticeLevel.Error, $"codex refused the message: {error}"));
    }

    public async Task InterruptAsync(CancellationToken ct)
    {
        _approvals.AbandonAll(ApprovalDecision.Cancel);
        _questions.AbandonAll(null);
        string? codexTurn;
        lock (_turnGate)
        {
            codexTurn = _codexTurnId;
            _queuedTurns = 0;
        }

        if (_peer is null || _threadId is null || codexTurn is null) return;
        try
        {
            await _peer.RequestAsync("turn/interrupt", new { threadId = _threadId, turnId = codexTurn }, ct,
                TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) when (ex is JsonRpcException or TimeoutException)
        {
            sink.Emit(new NoticeRaised(NoticeLevel.Warning, $"codex did not stop the turn: {ex.Message}"));
        }
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
        if (_peer is not null) await _peer.DisposeAsync();
    }

    private Task<JsonElement> StartThreadAsync(string? approval, string? sandbox, CancellationToken ct) =>
        _peer!.RequestAsync("thread/start", ThreadParameters(null, approval, sandbox), ct, TimeSpan.FromSeconds(90));

    private Dictionary<string, object?> ThreadParameters(string? threadId, string? approval, string? sandbox)
    {
        var parameters = new Dictionary<string, object?> { ["cwd"] = launch.WorkingDirectory };
        if (threadId is not null) parameters["threadId"] = threadId;
        if (launch.Model.Length > 0) parameters["model"] = launch.Model;
        if (approval is not null) parameters["approvalPolicy"] = approval;
        if (sandbox is not null) parameters["sandbox"] = sandbox;
        return parameters;
    }

    private static string SandboxPolicyType(string sandbox) => sandbox switch
    {
        "read-only" => "readOnly",
        "danger-full-access" => "dangerFullAccess",
        _ => "workspaceWrite",
    };

    private void OnNotification(string method, JsonElement parameters)
    {
        // Notifications of a sub-agent's own thread are the inside of one collab tool call: its turns must
        // not end ours, and its thread must not become the id this conversation resumes.
        if (IsAnotherThread(parameters)) return;

        switch (method)
        {
            case "turn/started":
                lock (_turnGate) _codexTurnId = parameters.Prop("turn").Str("id") ?? _codexTurnId;
                return;
            case "turn/completed":
                var (outcome, error) = CodexAppServerMapper.OutcomeOf(parameters);
                EndTurn(outcome, error);
                return;
        }

        string? turnId;
        lock (_turnGate) turnId = _turnId;
        foreach (var e in CodexAppServerMapper.Map(method, parameters, turnId)) sink.Emit(e);
    }

    /// <summary>
    /// Whether a notification names a thread other than this conversation's — in <c>threadId</c>, or in
    /// <c>thread.id</c> for <c>thread/started</c>. Before our own thread is known nothing is filtered.
    /// </summary>
    private bool IsAnotherThread(JsonElement parameters) =>
        _threadId is not null
        && (parameters.Str("threadId") ?? parameters.Prop("thread").Str("id")) is { } thread
        && thread != _threadId;

    private Task<object?> OnRequestAsync(string method, JsonElement parameters) => method switch
    {
        "item/commandExecution/requestApproval" => ApproveAsync(parameters, ApprovalKind.Command,
            parameters.Str("command") is { } command ? $"Run {command}" : "Run a command",
            string.Join("\n", new[] { parameters.Str("reason"), parameters.Str("command") }.OfType<string>())),
        "item/fileChange/requestApproval" => ApproveAsync(parameters, ApprovalKind.FileChange,
            "Apply file changes", parameters.Str("reason") ?? parameters.Str("grantRoot")),
        "item/tool/requestUserInput" => AskAsync(parameters),
        "mcpServer/elicitation/request" => Task.FromResult<object?>(new { action = "decline" }),
        _ => throw new JsonRpcException(JsonRpcPeer.MethodNotFound, $"Method not found: {method}"),
    };

    private async Task<object?> ApproveAsync(JsonElement parameters, ApprovalKind kind, string title, string? detail)
    {
        var requestId = $"approval-{Guid.NewGuid():N}";
        string? turn;
        lock (_turnGate) turn = _turnId;

        // Waiting before it is announced: a viewer may answer from inside the announcement itself,
        // and an answer that finds nothing waiting is lost and the turn hangs.
        var pending = _approvals.WaitAsync(requestId, CancellationToken.None);
        sink.Emit(new ApprovalRequested(requestId, kind, title, string.IsNullOrWhiteSpace(detail) ? null : detail,
            parameters.Str("itemId"),
            [
                new ApprovalOption(ApprovalDecision.Accept, "Allow"),
                new ApprovalOption(ApprovalDecision.AcceptForSession, "Allow for this session"),
                new ApprovalOption(ApprovalDecision.Decline, "Deny"),
                new ApprovalOption(ApprovalDecision.Cancel, "Deny and stop"),
            ]) { TurnId = turn });
        var decision = await pending;
        sink.Emit(new ApprovalResolved(requestId, decision) { TurnId = turn });

        return new
        {
            decision = decision switch
            {
                ApprovalDecision.Accept => "accept",
                ApprovalDecision.AcceptForSession => "acceptForSession",
                ApprovalDecision.Decline => "decline",
                _ => "cancel",
            },
        };
    }

    private async Task<object?> AskAsync(JsonElement parameters)
    {
        var requestId = $"questions-{Guid.NewGuid():N}";
        var questions = parameters.Items("questions").Select(q => new UserQuestion(
            q.Str("id") ?? Guid.NewGuid().ToString("N"),
            q.Str("header"),
            q.Str("question") ?? "",
            [.. q.Items("options").Select(o => new QuestionOption(o.Str("label") ?? "", o.Str("description")))],
            MultiSelect: false,
            AllowsCustomAnswer: q.Prop("isOther") is { ValueKind: JsonValueKind.True } || !q.Items("options").Any()))
            .ToList();

        string? turn;
        lock (_turnGate) turn = _turnId;
        // Waiting before it is announced: a viewer may answer from inside the announcement itself,
        // and an answer that finds nothing waiting is lost and the turn hangs.
        var pending = _questions.WaitAsync(requestId, CancellationToken.None);
        sink.Emit(new QuestionsAsked(requestId, questions) { TurnId = turn });
        var answers = await pending;
        sink.Emit(new QuestionsAnswered(requestId, answers) { TurnId = turn });

        return new
        {
            answers = (answers ?? new Dictionary<string, IReadOnlyList<string>>())
                .ToDictionary(pair => pair.Key, pair => new { answers = pair.Value }),
        };
    }

    private void BeginTurn()
    {
        _turnId = $"turn-{Guid.NewGuid():N}";
        _codexTurnId = null;
        sink.Emit(new TurnStarted { TurnId = _turnId });
    }

    private void EndTurn(TurnOutcome outcome, string? error)
    {
        lock (_turnGate)
        {
            if (_turnId is null) return;
            _approvals.AbandonAll(ApprovalDecision.Cancel);
            _questions.AbandonAll(null);
            sink.Emit(new TurnCompleted(outcome, error) { TurnId = _turnId });
            _turnId = null;
            _codexTurnId = null;

            if (_queuedTurns > 0 && outcome != TurnOutcome.Interrupted)
            {
                _queuedTurns--;
                BeginTurn();
            }
            else
            {
                _queuedTurns = 0;
            }
        }
    }

    private async Task WatchExitAsync(AgentProcess process)
    {
        var code = await process.Exited;
        _approvals.AbandonAll(ApprovalDecision.Cancel);
        _questions.AbandonAll(null);
        if (process.StoppedByUs) EndTurn(TurnOutcome.Interrupted, null);
        else EndTurn(TurnOutcome.Failed, "codex ended during the turn.");

        var stderr = process.StderrText;
        sink.Emit(code is 0 or null || process.StoppedByUs
            ? new SessionStateChanged(AgentSessionState.Stopped)
            : new SessionStateChanged(AgentSessionState.Failed,
                $"codex exited with code {code}." + (stderr.Length > 0 ? "\n" + AgentProcess.Tail(stderr) : "")));
    }
}
