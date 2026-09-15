using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using mTiles.AgentSessions;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Protocols;
using mTiles.Models;

namespace mTiles.Services.Agents.Sessions.OpenCode;

/// <summary>
/// A conversation with opencode through <c>opencode serve</c>: HTTP for what we ask, server-sent events
/// for what it says.
/// </summary>
/// <remarks>
/// <para><b>Measured 2026-09-15 against opencode 1.18.18.</b> The server is started on a free loopback
/// port and announces itself on stdout as <c>opencode server listening on http://127.0.0.1:&lt;port&gt;</c>.
/// Every request carries the workspace in <c>x-opencode-directory</c>; <c>POST /session</c> creates the
/// conversation (with its permission rules), <c>POST /session/{id}/prompt_async</c> sends a message and
/// answers 204 at once, and <c>GET /event</c> streams everything that follows.</para>
/// <para><b>The server gets a password of its own</b> (<c>OPENCODE_SERVER_PASSWORD</c>, sent as HTTP
/// basic auth with the user <c>opencode</c>). Bound to loopback it is still reachable by every other
/// process on the machine, and what it offers is a shell in the user's repository.</para>
/// <para>Permission rules are t3code's, by mode: bypass allows everything; an editing mode allows edits
/// and asks for the rest; asking and planning ask. The tool's own default configuration applies where
/// the instance passes no behaviour.</para>
/// </remarks>
public sealed partial class OpenCodeServerSession(AgentSessionLaunch launch, OpenCodeAgent agent, IAgentEventSink sink)
    : IAgentSession, IProcessBackedSession
{
    private readonly PendingReplies<ApprovalDecision> _approvals = new();
    private readonly PendingReplies<IReadOnlyDictionary<string, IReadOnlyList<string>>?> _questions = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _turnGate = new();
    private AgentProcess? _process;
    private HttpClient? _http;
    private OpenCodeEventMapper? _mapper;
    private string? _sessionId;
    private string? _turnId;
    private bool _abortRequested;
    private bool _turnWasBusy;

    /// <inheritdoc />
    public int? ChildProcessId => _process?.ProcessId;

    public async Task StartAsync(CancellationToken ct)
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var port = FreePort();
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

        var start = launch.StartInfo(["serve", "--hostname=127.0.0.1", $"--port={port}", .. launch.ExtraArgs]);
        start.Environment["OPENCODE_SERVER_PASSWORD"] = password;
        _process = AgentProcess.Start(start, line =>
        {
            if (ListeningLine().Match(line) is { Success: true } match && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri))
                listening.TrySetResult(uri);
        });

        var ready = await Task.WhenAny(listening.Task, _process.Exited, Task.Delay(TimeSpan.FromSeconds(45), ct));
        if (ready != listening.Task)
            throw new InvalidOperationException("opencode's server did not start." +
                                                (_process.StderrText.Length > 0 ? "\n" + AgentProcess.Tail(_process.StderrText) : ""));

        _http = new HttpClient { BaseAddress = listening.Task.Result, Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{password}")));
        _http.DefaultRequestHeaders.Add("x-opencode-directory", Uri.EscapeDataString(launch.WorkingDirectory));
        _ = WatchExitAsync(_process);

        _sessionId = await OpenSessionAsync(ct);
        _mapper = new OpenCodeEventMapper(_sessionId);
        _ = ListenAsync(_lifetime.Token);

        sink.Emit(new SessionConfigured(launch.Model.Length > 0 ? launch.Model : null, null, _sessionId));
        sink.Emit(new SessionStateChanged(AgentSessionState.Ready));
    }

    public async Task SendAsync(AgentTurnInput input, CancellationToken ct)
    {
        if (_http is null || _sessionId is null) return;

        bool startsTurn;
        lock (_turnGate)
        {
            startsTurn = _turnId is null;
            if (startsTurn)
            {
                _turnId = $"turn-{Guid.NewGuid():N}";
                _abortRequested = false;
                sink.Emit(new TurnStarted { TurnId = _turnId });
            }
        }

        var parts = new List<object> { new { type = "text", text = input.Text } };
        parts.AddRange(input.Images.Select(image => (object)new
        {
            type = "file", mime = image.MimeType, filename = image.Name ?? "image",
            url = $"data:{image.MimeType};base64,{image.Base64Data}",
        }));

        var body = new Dictionary<string, object?> { ["parts"] = parts };
        if (ModelReference() is { } model) body["model"] = model;
        if (launch.Behaviour == AiBehaviour.Plan) body["agent"] = "plan";

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync($"session/{_sessionId}/prompt_async", body, ct);
        }
        catch (HttpRequestException ex) when (startsTurn)
        {
            // Closed here as well as in the host: a turn only the host closed stays open in this session, and
            // the next message would never be shown as working.
            EndTurn(TurnOutcome.Failed, $"opencode did not take the message: {ex.Message}");
            return;
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
            EndTurn(TurnOutcome.Failed, $"opencode refused the message ({(int)response.StatusCode}): " +
                                        await response.Content.ReadAsStringAsync(ct));
    }

    public async Task InterruptAsync(CancellationToken ct)
    {
        _approvals.AbandonAll(ApprovalDecision.Cancel);
        _questions.AbandonAll(null);
        if (_http is null || _sessionId is null) return;
        lock (_turnGate) _abortRequested = true;
        using var _ = await _http.PostAsync($"session/{_sessionId}/abort", null, ct);
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
        await _lifetime.CancelAsync();
        _http?.Dispose();
        if (_process is not null) await _process.DisposeAsync();
        // The lifetime is deliberately not disposed: the exit watcher runs on the thread pool and may
        // cancel it after this returns, and a source with no timer and no wait handle holds nothing.
    }

    /// <summary>What a reply to the server meets once the session is closing, and is not worth reporting.</summary>
    private static bool SessionIsGone(Exception ex) =>
        ex is HttpRequestException or OperationCanceledException or ObjectDisposedException;

    [GeneratedRegex(@"listening on\s+(https?://\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ListeningLine();

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary><c>{providerID, modelID}</c> from the model spelled opencode's way, or null for its own.</summary>
    private object? ModelReference()
    {
        var qualified = agent.QualifiedModel(launch.Runtime);
        var slash = qualified.IndexOf('/');
        return slash <= 0 ? null : new { providerID = qualified[..slash], modelID = qualified[(slash + 1)..] };
    }

    private async Task<string> OpenSessionAsync(CancellationToken ct)
    {
        var rules = PermissionRules(launch.Behaviour);
        if (launch.ResumeToken is { Length: > 0 } token)
        {
            using var existing = await _http!.GetAsync($"session/{token}", ct);
            if (existing.IsSuccessStatusCode)
            {
                if (rules is not null)
                    using (await _http.PatchAsJsonAsync($"session/{token}", new { permission = rules }, ct)) { }
                return token;
            }

            sink.Emit(new NoticeRaised(NoticeLevel.Warning,
                "The previous conversation could not be found in opencode; a new one was started."));
        }

        var body = new Dictionary<string, object?> { ["title"] = "mTiles" };
        if (rules is not null) body["permission"] = rules;
        using var created = await _http!.PostAsJsonAsync("session", body, ct);
        created.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return document.RootElement.Str("id") ?? throw new InvalidOperationException("opencode did not name the new session.");
    }

    /// <summary>The session's permission rules for a mode, or null to leave opencode's own.</summary>
    internal static object[]? PermissionRules(AiBehaviour behaviour)
    {
        static object Rule(string permission, string pattern, string action) => new { permission, pattern, action };

        if (behaviour == AiBehaviour.BypassPermissions)
            return [Rule("*", "*", "allow"), Rule("external_directory", "*", "allow")];
        if (behaviour == AiBehaviour.ToolDefault) return null;

        var edit = behaviour is AiBehaviour.Auto or AiBehaviour.AcceptEdits ? "allow" : "ask";
        return
        [
            Rule("*", "*", "ask"),
            Rule("read", "*", "allow"),
            Rule("read", "*.env", "ask"),
            Rule("read", "*.env.*", "ask"),
            Rule("read", "*.env.example", "allow"),
            Rule("glob", "*", "allow"),
            Rule("grep", "*", "allow"),
            Rule("list", "*", "allow"),
            Rule("lsp", "*", "allow"),
            Rule("skill", "*", "allow"),
            Rule("todowrite", "*", "allow"),
            Rule("question", "*", "allow"),
            Rule("edit", "*", edit),
        ];
    }

    private const int MaxStreamFailures = 5;

    /// <remarks>Never throws: it runs unobserved, so every way out of it — cancellation included — ends
    /// here, and giving up on the stream ends the turn out loud rather than leaving it working for good.
    /// </remarks>
    private async Task ListenAsync(CancellationToken ct)
    {
        var failures = 0;
        Exception? lastFailure = null;
        while (!ct.IsCancellationRequested && failures < MaxStreamFailures)
        {
            try
            {
                await ReadEventStreamAsync(() => failures = 0, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                lastFailure = ex;
                if (!await DelayQuietlyAsync(TimeSpan.FromMilliseconds(500 * failures), ct)) return;
            }
        }

        if (ct.IsCancellationRequested) return;
        const string lost = "The connection to opencode's event stream was lost.";
        sink.Emit(new NoticeRaised(NoticeLevel.Error, $"{lost} {lastFailure?.Message}".TrimEnd()));
        EndTurn(TurnOutcome.Failed, lost);
        // Without a reader nothing will ever close the next turn, so the session stops taking messages.
        sink.Emit(new SessionStateChanged(AgentSessionState.Failed, lost));
    }

    /// <summary>Reads one connection to <c>GET /event</c> until the server closes it.</summary>
    private async Task ReadEventStreamAsync(Action connected, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "event");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        connected();

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
            if (line.StartsWith("data:", StringComparison.Ordinal))
                OnEvent(line[5..].Trim());
    }

    /// <returns>False when the wait was cancelled.</returns>
    private static async Task<bool> DelayQuietlyAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void OnEvent(string data)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(data);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        var type = root.Str("type") ?? "";
        var properties = root.Prop("properties") ?? default;
        if (_mapper is null || !_mapper.Concerns(properties)) return;

        switch (type)
        {
            case "permission.asked":
                _ = ApproveAsync(properties);
                return;
            case "question.asked":
                _ = AskAsync(properties);
                return;
            // Idle ends a turn only once the turn has been busy: the status can report the session idle
            // in the moment between the message being accepted and the work starting.
            case "session.status" when properties.Prop("status").Str("type") == "busy":
                lock (_turnGate) _turnWasBusy = _turnId is not null;
                break;
            case "session.idle" or "session.status" when type == "session.idle"
                                                      || properties.Prop("status").Str("type") == "idle":
                bool busy;
                lock (_turnGate) busy = _turnWasBusy;
                if (busy) EndTurn(TurnOutcome.Completed, null);
                return;
            case "session.error":
                var error = properties.Prop("error");
                if (error.Str("name") == "MessageAbortedError") EndTurn(TurnOutcome.Interrupted, null);
                else EndTurn(TurnOutcome.Failed, error.Prop("data").Str("message") ?? error.Str("name") ?? "opencode failed.");
                return;
        }

        string? turnId;
        lock (_turnGate) turnId = _turnId;
        foreach (var e in _mapper.Map(type, properties, turnId)) sink.Emit(e);
    }

    private async Task ApproveAsync(JsonElement request)
    {
        if (request.Str("id") is not { } requestId) return;
        var permission = request.Str("permission") ?? "tool";
        var patterns = string.Join(", ", request.Items("patterns").Select(p => p.GetString()));
        var metadata = request.Prop("metadata");
        var detail = metadata.Str("diff") ?? metadata.Str("command") ?? (patterns.Length > 0 ? patterns : null);

        string? turn;
        lock (_turnGate) turn = _turnId;
        // Waiting before it is announced: a viewer may answer from inside the announcement itself,
        // and an answer that finds nothing waiting is lost and the turn hangs.
        var pending = _approvals.WaitAsync(requestId, CancellationToken.None);
        sink.Emit(new ApprovalRequested(requestId, OpenCodeTools.ApprovalKindOf(permission),
            patterns.Length > 0 ? $"{permission} {patterns}" : permission, detail,
            request.Prop("tool").Str("callID"),
            [
                new ApprovalOption(ApprovalDecision.Accept, "Allow"),
                new ApprovalOption(ApprovalDecision.AcceptForSession, "Always allow"),
                new ApprovalOption(ApprovalDecision.Decline, "Reject"),
            ]) { TurnId = turn });
        var decision = await pending;
        sink.Emit(new ApprovalResolved(requestId, decision) { TurnId = turn });

        var reply = decision switch
        {
            ApprovalDecision.Accept => "once",
            ApprovalDecision.AcceptForSession => "always",
            _ => "reject",
        };

        try
        {
            using var response = await _http!.PostAsJsonAsync($"permission/{requestId}/reply", new { reply },
                _lifetime.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                using (await _http.PostAsJsonAsync($"session/{_sessionId}/permissions/{requestId}",
                           new { response = reply }, _lifetime.Token)) { }
            if (decision == ApprovalDecision.Cancel) await InterruptAsync(CancellationToken.None);
        }
        catch (Exception ex) when (SessionIsGone(ex))
        {
        }
    }

    private async Task AskAsync(JsonElement request)
    {
        if (request.Str("id") is not { } requestId) return;
        var questions = request.Items("questions").Select((q, index) => new UserQuestion(
            $"q{index}",
            q.Str("header"),
            q.Str("question") ?? "",
            [.. q.Items("options").Select(o => new QuestionOption(o.Str("label") ?? "", o.Str("description")))],
            q.Bool("multiple") == true,
            q.Bool("custom") != false)).ToList();

        string? turn;
        lock (_turnGate) turn = _turnId;
        // Waiting before it is announced: a viewer may answer from inside the announcement itself,
        // and an answer that finds nothing waiting is lost and the turn hangs.
        var pending = _questions.WaitAsync(requestId, CancellationToken.None);
        sink.Emit(new QuestionsAsked(requestId, questions) { TurnId = turn });
        var answers = await pending;
        sink.Emit(new QuestionsAnswered(requestId, answers) { TurnId = turn });

        try
        {
            if (answers is null)
            {
                using (await _http!.PostAsync($"question/{requestId}/reject", null, _lifetime.Token)) { }
                return;
            }

            var ordered = questions.Select(q => answers.TryGetValue(q.Id, out var chosen) ? chosen : []).ToList();
            using (await _http!.PostAsJsonAsync($"question/{requestId}/reply", new { answers = ordered }, _lifetime.Token)) { }
        }
        catch (Exception ex) when (SessionIsGone(ex))
        {
        }
    }

    private void EndTurn(TurnOutcome outcome, string? error)
    {
        lock (_turnGate)
        {
            if (_turnId is null) return;
            if (outcome == TurnOutcome.Completed && _abortRequested) outcome = TurnOutcome.Interrupted;
            _approvals.AbandonAll(ApprovalDecision.Cancel);
            _questions.AbandonAll(null);
            sink.Emit(new TurnCompleted(outcome, error) { TurnId = _turnId });
            _turnId = null;
            _turnWasBusy = false;
        }
    }

    private async Task WatchExitAsync(AgentProcess process)
    {
        var code = await process.Exited;
        await _lifetime.CancelAsync();
        if (process.StoppedByUs) EndTurn(TurnOutcome.Interrupted, null);
        else EndTurn(TurnOutcome.Failed, "opencode ended during the turn.");
        var stderr = process.StderrText;
        sink.Emit(code is 0 or null || process.StoppedByUs
            ? new SessionStateChanged(AgentSessionState.Stopped)
            : new SessionStateChanged(AgentSessionState.Failed,
                $"opencode exited with code {code}." + (stderr.Length > 0 ? "\n" + AgentProcess.Tail(stderr) : "")));
    }
}
