using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using mTiles.AgentSessions;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Protocols;
using mTiles.Models;

namespace mTiles.Services.Agents.Sessions.Pi;

/// <summary>
/// A conversation with pi through <c>pi --mode rpc</c>.
/// </summary>
/// <remarks>
/// <para><b>Read from pi 0.84.4's <c>docs/rpc.md</c>.</b> JSON lines both ways: a command is
/// <c>{"id","type":"prompt"|"abort"|…}</c> answered by <c>{"type":"response","id","success"}</c>, and
/// everything else pi writes is an event. There is no ready message — commands can be written at once.
/// A prompt sent while pi is still working must say whether it steers or follows up; this sends
/// <c>followUp</c>, so a second message waits its turn as it does with every other agent here.</para>
/// <para><b>The session id is chosen here</b>, through <c>--session-id</c>, which both creates and
/// resumes: a fresh one for a new conversation, the stored one after that. Not the tile's own id — a
/// conversation started again in the same tile must not reopen the one it replaced.</para>
/// </remarks>
public sealed class PiRpcSession(AgentSessionLaunch launch, PiAgent agent, IAgentEventSink sink) : IAgentSession, IProcessBackedSession
{
    private readonly PiRpcMapper _mapper = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _responses = new();
    private readonly PendingReplies<IReadOnlyDictionary<string, IReadOnlyList<string>>?> _questions = new();
    private readonly Lock _turnGate = new();
    private AgentProcess? _process;
    private string? _turnId;
    private int _counter;

    /// <inheritdoc />
    public int? ChildProcessId => _process?.ProcessId;

    public async Task StartAsync(CancellationToken ct)
    {
        var sessionId = launch.ResumeToken is { Length: > 0 } token ? token : Guid.NewGuid().ToString();
        List<string> arguments = ["--mode", "rpc", "--session-id", sessionId];
        arguments.AddRange(agent.EffortArgs(launch.Effort, AiUsage.Interactive));
        arguments.AddRange(agent.ModelArgs(agent.QualifiedModel(launch.Runtime), AiUsage.Interactive));
        arguments.AddRange(launch.ExtraArgs);

        _process = AgentProcess.Start(launch.StartInfo(arguments), OnLine);
        _ = WatchExitAsync(_process);

        if (await CommandAsync(new JsonObject { ["type"] = "get_state" }, ct, TimeSpan.FromSeconds(60))
            is not { } state) return;
        var data = state.Prop("data");
        await ReportOptionsAsync(ct);
        sink.Emit(new SessionConfigured(ModelId(data.Prop("model")), null,
            data.Str("sessionId") ?? sessionId, SessionSettingOptions.EffortId(launch.Effort)));
        sink.Emit(new SessionStateChanged(AgentSessionState.Ready));
    }

    /// <summary>pi's models from <c>get_available_models</c> (<c>data.models[]</c> of <c>provider</c>, <c>id</c>,
    /// <c>name</c>, per its <c>docs/rpc.md</c>), spelled <c>provider/id</c> as <c>--model</c> takes them.</summary>
    private async Task ReportOptionsAsync(CancellationToken ct)
    {
        List<SessionOption> models = [];
        try
        {
            if (await CommandAsync(new JsonObject { ["type"] = "get_available_models" }, ct, TimeSpan.FromSeconds(20))
                is { } listed)
                models.AddRange(listed.Prop("data").Items("models")
                    .Select(m => (Id: ModelId(m), Name: m.Str("name")))
                    .Where(m => m.Id is not null)
                    .Select(m => new SessionOption(m.Id!, m.Name ?? m.Id!)));
        }
        catch (TimeoutException)
        {
        }

        // No modes: pi has no permission gate of its own, so a chooser would promise a change nothing makes.
        sink.Emit(new SessionOptionsReported(SessionSettingOptions.ModelsOfProvider(models, launch.Runtime), [],
            SessionSettingOptions.Efforts(agent, launch.Runtime.Instance)));
    }

    private static string? ModelId(JsonElement? model) =>
        model.Str("id") is { } id ? model.Str("provider") is { } provider ? $"{provider}/{id}" : id : null;

    /// <summary><c>set_model</c> and <c>set_thinking_level</c>, both commands of the running process. pi has no
    /// permission mode to switch, so one is refused rather than reported as taken.</summary>
    public async Task<SettingsChangeOutcome> ChangeSettingsAsync(SessionSettings settings, CancellationToken ct)
    {
        if (settings.Mode is not null)
        {
            sink.Emit(new NoticeRaised(NoticeLevel.Warning, "pi has no permission mode to switch."));
            return SettingsChangeOutcome.Rejected;
        }

        if (_process is null) return SettingsChangeOutcome.NeedsRestart;

        // Tool default is the absence of a level of ours, and set_thinking_level has no way to say that: skipped,
        // pi goes on thinking at the level it was started with, so nothing would change while the chooser said it
        // had. The session is started again instead, without the flag.
        var level = SessionSettingOptions.ParseEffort(settings.Effort) is { } chosen ? AiEfforts.Name(chosen) : null;
        if (settings.Effort is not null && level is null) return SettingsChangeOutcome.NeedsRestart;

        try
        {
            if (settings.Model is { Length: > 0 } model)
            {
                var slash = model.IndexOf('/');
                var command = new JsonObject { ["type"] = "set_model", ["modelId"] = slash > 0 ? model[(slash + 1)..] : model };
                if (slash > 0) command["provider"] = model[..slash];
                if (!await TookAsync(command, $"pi did not switch to {model}", ct)) return SettingsChangeOutcome.Rejected;
            }

            if (level is not null
                && !await TookAsync(new JsonObject { ["type"] = "set_thinking_level", ["level"] = level },
                    $"pi did not switch to {level} thinking", ct))
                return SettingsChangeOutcome.Rejected;
        }
        catch (TimeoutException)
        {
            sink.Emit(new NoticeRaised(NoticeLevel.Warning, "pi did not answer the change."));
            return SettingsChangeOutcome.Rejected;
        }

        sink.Emit(new SessionConfigured(settings.Model, null, null, settings.Effort));
        return SettingsChangeOutcome.Applied;
    }

    /// <summary>Sends a command and says whether pi took it; a refusal is reported with pi's own reason.</summary>
    private async Task<bool> TookAsync(JsonObject command, string refusal, CancellationToken ct)
    {
        var answer = await CommandAsync(command, ct, TimeSpan.FromSeconds(20));
        if (answer is { } taken && taken.Bool("success") != false) return true;
        sink.Emit(new NoticeRaised(NoticeLevel.Warning, $"{refusal}: {answer?.Str("error") ?? "pi exited."}"));
        return false;
    }

    public async Task SendAsync(AgentTurnInput input, CancellationToken ct)
    {
        if (_process is null) return;

        var command = new JsonObject { ["type"] = "prompt", ["message"] = input.Text };
        if (input.Images.Count > 0)
            command["images"] = new JsonArray([
                .. input.Images.Select(image => (JsonNode)new JsonObject
                {
                    ["type"] = "image", ["data"] = image.Base64Data, ["mimeType"] = image.MimeType,
                }),
            ]);

        bool startsTurn;
        lock (_turnGate)
        {
            startsTurn = _turnId is null;
            if (startsTurn)
            {
                _turnId = $"turn-{Guid.NewGuid():N}";
                _mapper.BeginTurn();
                sink.Emit(new TurnStarted { TurnId = _turnId });
            }
            else
            {
                command["streamingBehavior"] = "followUp";
            }
        }

        try
        {
            // No response because pi exited: its exit watcher ends the turn.
            if (await CommandAsync(command, ct, TimeSpan.FromSeconds(60)) is not { } response
                || response.Bool("success") != false) return;
            var error = response.Str("error") ?? "pi refused the message.";
            // A refused follow-up leaves the running turn as it was: it is still pi's, and still working.
            if (startsTurn) EndTurn(TurnOutcome.Failed, error);
            else sink.Emit(new NoticeRaised(NoticeLevel.Error, error));
        }
        catch (TimeoutException)
        {
            // Accepted or not, the events say what happened next.
        }
    }

    public async Task InterruptAsync(CancellationToken ct)
    {
        _questions.AbandonAll(null);
        if (_process is null) return;
        try
        {
            await CommandAsync(new JsonObject { ["type"] = "abort" }, ct, TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            sink.Emit(new NoticeRaised(NoticeLevel.Warning, "pi did not confirm the interruption."));
        }
    }

    /// <summary>pi asks for no approvals, so there is nothing to answer.</summary>
    public Task RespondToApprovalAsync(string requestId, ApprovalDecision decision, CancellationToken ct) =>
        Task.CompletedTask;

    public Task AnswerQuestionsAsync(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers,
        CancellationToken ct)
    {
        _questions.Resolve(requestId, answers);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _questions.AbandonAll(null);
        if (_process is not null) await _process.DisposeAsync();
    }

    /// <summary>
    /// Writes a command and waits for pi's response to it — or answers <c>null</c> once pi has exited, since
    /// nothing will ever answer then and the exit watcher has already said why.
    /// </summary>
    private async Task<JsonElement?> CommandAsync(JsonObject command, CancellationToken ct, TimeSpan timeout)
    {
        var process = _process!;
        var id = $"mtiles-{Interlocked.Increment(ref _counter)}";
        command["id"] = id;
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _responses[id] = response;
        try
        {
            await process.WriteLineAsync(command.ToJsonString(), ct);
            var finished = await Task.WhenAny(response.Task, process.Exited).WaitAsync(timeout, ct);
            return finished == response.Task ? await response.Task : null;
        }
        finally
        {
            _responses.TryRemove(id, out _);
        }
    }

    private void OnLine(string line)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        switch (root.Str("type"))
        {
            case "response" when root.Str("id") is { } id && _responses.TryGetValue(id, out var waiting):
                waiting.TrySetResult(root);
                return;
            case "agent_settled":
                EndTurn(_mapper.WasAborted ? TurnOutcome.Interrupted : TurnOutcome.Completed, null);
                return;
            case "extension_ui_request":
                _ = ExtensionRequestAsync(root);
                return;
        }

        string? turnId;
        lock (_turnGate) turnId = _turnId;
        foreach (var e in _mapper.Map(root, turnId)) sink.Emit(e);
    }

    /// <summary>
    /// An extension asking the user something: <c>select</c>, <c>confirm</c>, <c>input</c> and
    /// <c>editor</c> wait for an answer; <c>notify</c> is a notice; the rest are about pi's own TUI.
    /// </summary>
    private async Task ExtensionRequestAsync(JsonElement request)
    {
        if (request.Str("id") is not { } id) return;
        var method = request.Str("method");
        if (method == "notify")
        {
            sink.Emit(new NoticeRaised(request.Str("notifyType") switch
            {
                "error" => NoticeLevel.Error,
                "warning" => NoticeLevel.Warning,
                _ => NoticeLevel.Info,
            }, request.Str("message") ?? ""));
            return;
        }

        if (method is not ("select" or "confirm" or "input" or "editor")) return;

        var title = request.Str("title") ?? "pi asks";
        var question = method switch
        {
            "select" => new UserQuestion("answer", null, title,
                [.. request.Items("options").Select(o => new QuestionOption(o.GetString() ?? ""))], false, false),
            "confirm" => new UserQuestion("answer", null, $"{title}\n{request.Str("message")}".Trim(),
                [new QuestionOption("Yes"), new QuestionOption("No")], false, false),
            _ => new UserQuestion("answer", null, title, [], false, true),
        };

        string? turn;
        lock (_turnGate) turn = _turnId;
        // Waiting before it is announced: a viewer may answer from inside the announcement itself,
        // and an answer that finds nothing waiting is lost and the turn hangs.
        var pending = _questions.WaitAsync(id, CancellationToken.None);
        sink.Emit(new QuestionsAsked(id, [question]) { TurnId = turn });
        var answers = await pending;
        sink.Emit(new QuestionsAnswered(id, answers) { TurnId = turn });

        var reply = new JsonObject { ["type"] = "extension_ui_response", ["id"] = id };
        var chosen = answers?.GetValueOrDefault("answer")?.FirstOrDefault();
        if (chosen is null) reply["cancelled"] = true;
        else if (method == "confirm") reply["confirmed"] = chosen == "Yes";
        else reply["value"] = chosen;

        await _process!.WriteLineAsync(reply.ToJsonString(), CancellationToken.None);
    }

    private void EndTurn(TurnOutcome outcome, string? error)
    {
        lock (_turnGate)
        {
            if (_turnId is null) return;
            _questions.AbandonAll(null);
            sink.Emit(new TurnCompleted(outcome, error) { TurnId = _turnId });
            _turnId = null;
        }
    }

    private async Task WatchExitAsync(AgentProcess process)
    {
        var code = await process.Exited;
        if (process.StoppedByUs) EndTurn(TurnOutcome.Interrupted, null);
        else EndTurn(TurnOutcome.Failed, "pi ended during the turn.");
        var stderr = process.StderrText;
        sink.Emit(code is 0 or null || process.StoppedByUs
            ? new SessionStateChanged(AgentSessionState.Stopped)
            : new SessionStateChanged(AgentSessionState.Failed,
                $"pi exited with code {code}." + (stderr.Length > 0 ? "\n" + AgentProcess.Tail(stderr) : "")));
    }
}
