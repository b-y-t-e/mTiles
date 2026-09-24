using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using mTiles.AgentSessions;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Protocols;
using mTiles.Models;

namespace mTiles.Services.Agents.Sessions.Claude;

/// <summary>
/// A conversation with Claude Code over its stream-json protocol, the one the Agent SDK speaks.
/// </summary>
/// <remarks>
/// <para><b>Measured 2026-09-15 against Claude Code 2.1.272 and the SDK's own source (0.3.272,
/// <c>sdk.mjs</c>).</b> The process stays up for the whole conversation: each user message is one line on
/// stdin, and the CLI answers with stream events and one <c>result</c> per turn. Permission to use a tool
/// is asked through the control channel — <c>--permission-prompt-tool stdio</c> makes the CLI send
/// <c>{"type":"control_request","request":{"subtype":"can_use_tool",…}}</c> and wait for a
/// <c>control_response</c> carrying <c>{"behavior":"allow","updatedInput":…}</c> or
/// <c>{"behavior":"deny","message":…}</c>; the SDK always sends <c>updatedInput</c> on allow, and older
/// CLIs require it.</para>
/// <para><b>A new conversation gets a new session id</b> — never the tile's own, which the terminal tile
/// uses: a conversation can be thrown away and started again in the same tile, and handing Claude Code
/// the old id would bring the old conversation back (2.1.251 refuses a taken id with
/// <c>Session ID … is already in use</c>). Every later launch passes <c>--resume</c> with the id the CLI
/// reported in <c>system/init</c>, which the host stores.</para>
/// <para>Two tools are not permissions at all and are answered as what they are:
/// <c>AskUserQuestion</c> becomes a round of questions whose answers go back in <c>updatedInput.answers</c>
/// keyed by the question's text, and <c>ExitPlanMode</c> becomes the plan on screen with an approval that
/// either lets the agent implement it or keeps it planning.</para>
/// </remarks>
public sealed class ClaudeStreamSession(AgentSessionLaunch launch, IAiAgent agent, IAgentEventSink sink)
    : IAgentSession, IProcessBackedSession, ICompactingSession
{
    private readonly ClaudeStreamMapper _mapper = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _controlReplies = new();
    private readonly PendingReplies<ApprovalDecision> _approvals = new();
    private readonly PendingReplies<IReadOnlyDictionary<string, IReadOnlyList<string>>?> _questions = new();
    private readonly Lock _turnGate = new();
    private AgentProcess? _process;
    private string? _turnId;
    private int _requestCounter;

    /// <inheritdoc />
    public int? ChildProcessId => _process?.ProcessId;

    public async Task StartAsync(CancellationToken ct)
    {
        if (await TryStartAsync(launch.ResumeToken, ct)) return;

        // A stored session Claude Code no longer has — deleted, or kept under another account's
        // directory — would otherwise fail every restart the same way; the history stays on screen.
        if (launch.ResumeToken is { Length: > 0 })
        {
            ct.ThrowIfCancellationRequested();
            await DiscardFailedProcessAsync();
            if (await TryStartAsync(null, ct))
            {
                sink.Emit(new NoticeRaised(NoticeLevel.Warning,
                    "Claude Code could not resume the previous session, so a new one was started."));
                return;
            }
        }

        ct.ThrowIfCancellationRequested();
        var stderr = _process?.StderrText ?? "";
        throw new InvalidOperationException(stderr.Length > 0
            ? AgentProcess.Tail(stderr)
            : "Claude Code ended before the conversation started.");
    }

    public async Task SendAsync(AgentTurnInput input, CancellationToken ct)
    {
        if (_process is null) return;

        // In the order the message says it — text, the image a marker names, the text after it. A slash
        // command is still read as one: it is the first words of the text, so it is the first block.
        var content = new JsonArray([.. input.Blocks<JsonNode?>(
            text => new JsonObject { ["type"] = "text", ["text"] = text },
            image => new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64", ["media_type"] = image.MimeType, ["data"] = image.Base64Data,
                },
            })]);

        var message = new JsonObject
        {
            ["type"] = "user",
            ["session_id"] = "",
            ["parent_tool_use_id"] = null,
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
        };

        // A message sent while a turn is running is a *steer*, not a second turn: Claude Code folds it
        // into the loop it is already in and answers the two together with one `result`. Counted as a
        // turn of its own it was a turn nothing could ever close — the single `result` ended the first
        // one and opened the phantom, and the tile said Working until the session was restarted. The
        // whole of the bookkeeping for it is therefore this: a turn is opened only when none is open.
        lock (_turnGate)
        {
            if (_turnId is null) BeginTurn();
        }

        await _process.WriteLineAsync(message.ToJsonString(), ct);
    }

    /// <summary>
    /// <c>/compact</c>, sent as an ordinary message — which is what a slash command is to Claude Code.
    /// </summary>
    /// <remarks>Measured 2026-09-20 against the stream-json interface: the CLI answers
    /// <c>{"type":"system","subtype":"status","status":"compacting"}</c>, then a fresh <c>system/init</c>,
    /// then <c>compact_boundary</c> with <c>compact_metadata.trigger</c> of <c>manual</c>, then
    /// <c>result</c> — so it is a turn like any other and the turn bookkeeping is
    /// <see cref="SendAsync"/>'s unchanged. What it does <em>not</em> go through is the host's own
    /// <c>SendMessage</c>, which would write "/compact" into the transcript as something the user said.
    /// The summary Claude Code injects afterwards arrives as a <c>user</c> line, which the mapper reads
    /// for tool results only, so none of it lands in the conversation either.</remarks>
    public Task CompactAsync(CancellationToken ct) => SendAsync(AgentTurnInput.FromText("/compact"), ct);

    /// <remarks>Measured 2026-09-24 against 2.1.281: <c>interrupt</c> also stops every background sub-agent —
    /// each answers <c>task_updated</c> "killed" and <c>task_notification</c> "stopped" — and does so with no
    /// turn open, which is what Stop does while only sub-agents are working.</remarks>
    public async Task InterruptAsync(CancellationToken ct)
    {
        _approvals.AbandonAll(ApprovalDecision.Cancel);
        _questions.AbandonAll(null);
        if (_process is null) return;

        try
        {
            await ControlAsync(new JsonObject { ["subtype"] = "interrupt" }, ct, TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            sink.Emit(new NoticeRaised(NoticeLevel.Warning, "Claude Code did not confirm the interruption."));
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
        if (_process is not null) await _process.DisposeAsync();
    }

    private async Task<bool> TryStartAsync(string? resume, CancellationToken ct)
    {
        List<string> arguments =
        [
            "-p", "--output-format", "stream-json", "--input-format", "stream-json", "--verbose",
            "--include-partial-messages", "--permission-prompt-tool", "stdio",
        ];
        if (resume is { Length: > 0 }) arguments.AddRange(["--resume", resume]);
        else arguments.Add($"--session-id={Guid.NewGuid()}");
        arguments.AddRange(agent.BehaviourArgs(launch.Behaviour, AiUsage.Interactive));
        arguments.AddRange(agent.EffortArgs(launch.Effort, AiUsage.Interactive));
        // Whether the rtk hook is written depends on Locate, which falls back to the login shell's PATH;
        // a tile restored at startup must not launch unfiltered only because that read had not finished.
        if (launch.Runtime.Instance.UseOutputProxy) await OutputProxy.WhenShellsPathIsKnownAsync();
        arguments.AddRange(agent.SessionDefaultArgs(launch.Runtime));
        arguments.AddRange(launch.ExtraArgs);

        var process = AgentProcess.Start(launch.StartInfo(arguments), OnLine);
        _process = process;

        var initialized = ControlAsync(new JsonObject { ["subtype"] = "initialize", ["hooks"] = null }, ct,
            TimeSpan.FromSeconds(90));
        var finished = await Task.WhenAny(initialized, process.Exited);
        if (finished != initialized || !initialized.IsCompletedSuccessfully) return false;

        _ = WatchExitAsync(process);
        ReportOptions(initialized.Result);
        sink.Emit(new SessionStateChanged(AgentSessionState.Ready));
        return true;
    }

    /// <summary>
    /// The models Claude Code offers, read from its answer to <c>initialize</c>, beside the modes and efforts
    /// this agent supports.
    /// </summary>
    /// <remarks>Measured 2026-09-15 on 2.1.272: the answer carries <c>models[]</c> with <c>value</c> (what
    /// <c>set_model</c> takes, <c>default</c> included), <c>displayName</c> and <c>description</c>.
    /// That list is Anthropic's own aliases whatever the instance points at, so on a provider none is offered
    /// and the field takes a name typed by hand — an alias the gateway does not serve would otherwise be picked,
    /// kept in the layout and handed to every later launch as <c>ANTHROPIC_MODEL</c>.</remarks>
    private void ReportOptions(JsonElement initialized)
    {
        var instance = launch.Runtime.Instance;
        sink.Emit(new SessionOptionsReported(
            OwnModels(initialized),
            SessionSettingOptions.Modes(agent, instance),
            SessionSettingOptions.Efforts(agent, instance)));
        sink.Emit(new SessionConfigured(null, SessionSettingOptions.ModeId(launch.Behaviour), null,
            SessionSettingOptions.EffortId(launch.Effort)));
    }

    /// <summary>Claude Code's own model list, or none when the instance runs through a provider.</summary>
    private IReadOnlyList<SessionOption> OwnModels(JsonElement initialized) =>
        launch.Runtime.Provider is not null
            ? []
            :
            [
                .. initialized.Items("models")
                    .Where(m => m.Str("value") is not null)
                    .Select(m => new SessionOption(m.Str("value")!, m.Str("displayName") ?? m.Str("value")!, m.Str("description"))),
            ];

    /// <summary>
    /// A model through <c>set_model</c> and a mode through <c>set_permission_mode</c>, both control requests
    /// of the running process (the SDK's own, <c>sdk.mjs</c> 0.3.272); an effort is a launch flag
    /// (<c>--effort</c>) and needs a restart.
    /// </summary>
    public async Task<SettingsChangeOutcome> ChangeSettingsAsync(SessionSettings settings, CancellationToken ct)
    {
        if (settings.Effort is not null) return SettingsChangeOutcome.NeedsRestart;
        if (_process is null) return SettingsChangeOutcome.NeedsRestart;

        var mode = SessionSettingOptions.ParseMode(settings.Mode);
        // Tool default is the absence of a flag of ours, and set_permission_mode has no way to say that: its
        // own "default" is a mode like any other, and it would override whatever the user's ~/.claude/settings.json
        // says for the rest of the session. The session is started again instead, without the flag.
        if (mode is { } wanted && PermissionMode(wanted) is null) return SettingsChangeOutcome.NeedsRestart;

        try
        {
            if (settings.Model is { Length: > 0 } model)
                await ControlAsync(new JsonObject { ["subtype"] = "set_model", ["model"] = model }, ct, TimeSpan.FromSeconds(15));

            if (mode is { } chosen)
                await ControlAsync(new JsonObject { ["subtype"] = "set_permission_mode", ["mode"] = PermissionMode(chosen)! },
                    ct, TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            sink.Emit(new NoticeRaised(NoticeLevel.Warning, $"Claude Code did not take the change: {ex.Message}"));
            return SettingsChangeOutcome.Rejected;
        }

        string? turn;
        lock (_turnGate) turn = _turnId;
        sink.Emit(new SessionConfigured(settings.Model, settings.Mode, null) { TurnId = turn });
        return SettingsChangeOutcome.Applied;
    }

    /// <summary>Claude Code's own name for a mode, read off the flag this agent passes for it at launch, so
    /// the two routes cannot spell one mode two ways — null where it passes no flag at all.</summary>
    private string? PermissionMode(mTiles.Models.AiBehaviour mode)
    {
        var args = agent.BehaviourArgs(mode, AiUsage.Interactive);
        var flag = args.ToList().IndexOf("--permission-mode");
        if (flag >= 0 && flag + 1 < args.Count) return args[flag + 1];
        return args.Contains("--dangerously-skip-permissions") ? "bypassPermissions" : null;
    }

    private async Task DiscardFailedProcessAsync()
    {
        var failed = _process;
        _process = null;
        if (failed is not null) await failed.DisposeAsync();
    }

    /// <summary>One line of the CLI's output. Internal for the tests, which read recordings through it.</summary>
    internal void OnLine(string line)
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
            case "control_response" when root.Prop("response") is { } response
                                         && response.Str("request_id") is { } id
                                         && _controlReplies.TryRemove(id, out var reply):
                if (response.Str("subtype") == "error")
                    reply.TrySetException(new InvalidOperationException(response.Str("error") ?? "Claude Code refused."));
                else
                    reply.TrySetResult(response.Prop("response") ?? default);
                return;
            case "control_request" when root.Str("request_id") is { } id && root.Prop("request") is { } request:
                _ = AnswerControlAsync(id, request);
                return;
            case "control_cancel_request" when root.Str("request_id") is { } id:
                _approvals.Resolve(id, ApprovalDecision.Cancel);
                _questions.Resolve(id, null);
                return;
        }

        // A turn nobody sent a message for: the agent woke by itself, which it does when a background sub-agent
        // it launched finishes — measured against 2.1.281, the parent's `result` has long closed the turn by
        // then, and the answer arrives as a fresh `system/init`, an assistant message and a `result` of its
        // own. Unopened, all of it was drawn with no spinner and no Stop while the agent worked, and its
        // `result` closed nothing — or closed the turn the user had opened by sending in the meantime, whose
        // own answer then came in with nothing saying it was coming. `system/init` itself is not taken as the
        // start: a settings change may say it again without a turn behind it, and a turn opened on that would
        // never be closed.
        if (ClaudeStreamMapper.OpensTurn(root))
            lock (_turnGate)
                if (_turnId is null)
                    BeginTurn();

        string? turnId;
        lock (_turnGate) turnId = _turnId;

        // The end of the turn is not allowed to depend on the rest of the line being understood. A
        // `result` is the only thing that puts this tile's "Working" down, and mapping it — or storing
        // what came out — can throw: the pump logs that and reads on, the turn is never closed, and the
        // tile says Working until the session is restarted, with the agent sitting idle behind it. So
        // whatever the mapping did, the line that says the turn ended still ends it.
        try
        {
            foreach (var e in _mapper.Map(root, turnId)) sink.Emit(e);
        }
        finally
        {
            if (ClaudeStreamMapper.EndsTurn(root)) EndTurn(root);
        }
    }

    private void BeginTurn()
    {
        _turnId = $"turn-{Guid.NewGuid():N}";
        sink.Emit(new TurnStarted { TurnId = _turnId });
    }

    private void EndTurn(JsonElement result)
    {
        var (outcome, error) = ClaudeStreamMapper.OutcomeOf(result);
        lock (_turnGate)
        {
            if (_turnId is null) return;
            _approvals.AbandonTurn(ApprovalDecision.Cancel);
            sink.Emit(new TurnCompleted(outcome, error) { TurnId = _turnId });
            _turnId = null;
        }
    }

    private async Task<JsonElement> ControlAsync(JsonObject request, CancellationToken ct, TimeSpan timeout)
    {
        var id = $"mtiles-{Interlocked.Increment(ref _requestCounter)}";
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _controlReplies[id] = reply;
        try
        {
            var message = new JsonObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request };
            await _process!.WriteLineAsync(message.ToJsonString(), ct);
            return await reply.Task.WaitAsync(timeout, ct);
        }
        finally
        {
            _controlReplies.TryRemove(id, out _);
        }
    }

    private async Task AnswerControlAsync(string requestId, JsonElement request)
    {
        JsonNode response;
        try
        {
            response = request.Str("subtype") == "can_use_tool"
                ? await CanUseToolAsync(requestId, request)
                : throw new InvalidOperationException($"Unsupported control request {request.Str("subtype")}.");

            await WriteControlResponseAsync(new JsonObject
            {
                ["subtype"] = "success", ["request_id"] = requestId, ["response"] = response,
            });
        }
        catch (Exception ex)
        {
            await WriteControlResponseAsync(new JsonObject
            {
                ["subtype"] = "error", ["request_id"] = requestId, ["error"] = ex.Message,
            });
        }
    }

    private Task WriteControlResponseAsync(JsonObject response) =>
        _process!.WriteLineAsync(new JsonObject { ["type"] = "control_response", ["response"] = response }.ToJsonString(),
            CancellationToken.None);

    private string? CurrentTurn
    {
        get
        {
            lock (_turnGate) return _turnId;
        }
    }

    private async Task<JsonNode> CanUseToolAsync(string requestId, JsonElement request)
    {
        var name = request.Str("tool_name") ?? "tool";
        var input = request.Prop("input") ?? default;
        // Measured 2026-09-24 against 2.1.281: a sub-agent's request carries the task id it runs under.
        var subAgent = request.Str("agent_id");
        var inputNode = input.ValueKind == JsonValueKind.Object ? JsonNode.Parse(input.GetRawText())! : new JsonObject();

        switch (name)
        {
            case "AskUserQuestion":
                return await AskAsync(requestId, input, (JsonObject)inputNode, subAgent);
            case "ExitPlanMode":
                sink.Emit(new PlanProposed(input.Str("plan") ?? "") { TurnId = CurrentTurn });
                return await ApproveAsync(requestId, ApprovalKind.Other, "Implement this plan?", null,
                    request.Str("tool_use_id"), inputNode,
                    [
                        new ApprovalOption(ApprovalDecision.Accept, "Implement"),
                        new ApprovalOption(ApprovalDecision.Decline, "Keep planning"),
                        new ApprovalOption(ApprovalDecision.Cancel, "Stop"),
                    ], null, "The user wants to revise the plan. Wait for their feedback.", subAgent);
        }

        var detail = ClaudeTools.DetailOf(name, input);
        var described = request.Str("description") ?? request.Str("decision_reason");
        var text = detail.Command ?? detail.Diff ?? (detail.Paths is { Count: > 0 } paths ? string.Join("\n", paths) : null)
                   ?? detail.Input ?? described;

        return await ApproveAsync(requestId, ClaudeTools.ApprovalKindOf(name), ClaudeTools.TitleOf(name, input), text,
            request.Str("tool_use_id"), inputNode,
            [
                new ApprovalOption(ApprovalDecision.Accept, "Allow"),
                new ApprovalOption(ApprovalDecision.AcceptForSession, "Allow for this session"),
                new ApprovalOption(ApprovalDecision.Decline, "Deny"),
                new ApprovalOption(ApprovalDecision.Cancel, "Deny and stop"),
            ], SessionRules(name, request), "The user declined this tool call.", subAgent);
    }

    private async Task<JsonNode> ApproveAsync(string requestId, ApprovalKind kind, string title, string? detail,
        string? toolUseId, JsonNode input, IReadOnlyList<ApprovalOption> options, JsonArray? sessionRules,
        string declineMessage, string? subAgent)
    {
        var turn = CurrentTurn;
        // Waiting before it is announced: a viewer may answer from inside the announcement itself,
        // and an answer that finds nothing waiting is lost and the turn hangs.
        var pending = _approvals.WaitAsync(requestId, CancellationToken.None, bySubAgent: subAgent is not null);
        sink.Emit(new ApprovalRequested(requestId, kind, title, detail, toolUseId, options)
        {
            TurnId = turn, SubAgentId = subAgent,
        });
        var decision = await pending;

        sink.Emit(new ApprovalResolved(requestId, decision) { TurnId = turn });

        return decision switch
        {
            ApprovalDecision.Accept => new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input },
            ApprovalDecision.AcceptForSession => new JsonObject
            {
                ["behavior"] = "allow", ["updatedInput"] = input, ["updatedPermissions"] = sessionRules,
            },
            ApprovalDecision.Decline => new JsonObject { ["behavior"] = "deny", ["message"] = declineMessage },
            _ => new JsonObject
            {
                ["behavior"] = "deny", ["message"] = "The user stopped the turn.", ["interrupt"] = true,
            },
        };
    }

    /// <summary>
    /// The rules "allow for this session" adds: the CLI's own suggestions, pinned to the session.
    /// </summary>
    /// <remarks>Measured on 2.1.272, a Write's suggestion is <c>{"type":"setMode","mode":"acceptEdits",
    /// "destination":"session"}</c>. Where the CLI suggests nothing, the tool itself is allowed for the
    /// session — what t3code sends in the same case.</remarks>
    private static JsonArray SessionRules(string toolName, JsonElement request)
    {
        var rules = new JsonArray();
        foreach (var suggestion in request.Items("permission_suggestions"))
        {
            if (JsonNode.Parse(suggestion.GetRawText()) is not JsonObject rule) continue;
            rule["destination"] = "session";
            rules.Add(rule);
        }

        if (rules.Count == 0)
            rules.Add(new JsonObject
            {
                ["type"] = "addRules",
                ["rules"] = new JsonArray(new JsonObject { ["toolName"] = toolName }),
                ["behavior"] = "allow",
                ["destination"] = "session",
            });
        return rules;
    }

    private async Task<JsonNode> AskAsync(string requestId, JsonElement input, JsonObject inputNode, string? subAgent)
    {
        var questions = input.Items("questions").Select(q => new UserQuestion(
            q.Str("question") ?? "",
            q.Str("header"),
            q.Str("question") ?? "",
            [.. q.Items("options").Select(o => new QuestionOption(o.Str("label") ?? "", o.Str("description")))],
            q.Prop("multiSelect") is { ValueKind: JsonValueKind.True },
            AllowsCustomAnswer: true)).ToList();

        var turn = CurrentTurn;
        // Waiting before it is announced: a viewer may answer from inside the announcement itself,
        // and an answer that finds nothing waiting is lost and the turn hangs.
        var pending = _questions.WaitAsync(requestId, CancellationToken.None);
        sink.Emit(new QuestionsAsked(requestId, questions) { TurnId = turn, SubAgentId = subAgent });
        var answers = await pending;
        sink.Emit(new QuestionsAnswered(requestId, answers) { TurnId = turn });

        if (answers is null)
            return new JsonObject { ["behavior"] = "deny", ["message"] = "The user dismissed the questions." };

        // Keyed by the question's full text: the CLI looks answers up that way (t3code, 2.1.121 onward).
        var answered = new JsonObject();
        foreach (var (question, chosen) in answers) answered[question] = string.Join(", ", chosen);
        inputNode["answers"] = answered;
        return new JsonObject { ["behavior"] = "allow", ["updatedInput"] = inputNode };
    }

    private async Task WatchExitAsync(AgentProcess process)
    {
        var code = await process.Exited;
        if (!ReferenceEquals(process, _process)) return;

        _approvals.AbandonAll(ApprovalDecision.Cancel);
        _questions.AbandonAll(null);
        lock (_turnGate)
        {
            if (_turnId is not null)
                sink.Emit(process.StoppedByUs
                    ? new TurnCompleted(TurnOutcome.Interrupted) { TurnId = _turnId }
                    : new TurnCompleted(TurnOutcome.Failed, "Claude Code ended during the turn.") { TurnId = _turnId });
            _turnId = null;
        }

        var stderr = process.StderrText;
        sink.Emit(code is 0 or null || process.StoppedByUs
            ? new SessionStateChanged(AgentSessionState.Stopped)
            : new SessionStateChanged(AgentSessionState.Failed,
                $"Claude Code exited with code {code}." + (stderr.Length > 0 ? "\n" + AgentProcess.Tail(stderr) : "")));
    }
}
