using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using mTiles.AgentSessions;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Protocols;
using mTiles.AgentSessions.Protocols.Acp;
using mTiles.Models;

namespace mTiles.Services.Agents.Sessions.Grok;

/// <summary>
/// A conversation with xAI's Grok Build CLI over ACP — <c>grok agent stdio</c> — and the xAI extensions
/// it adds.
/// </summary>
/// <remarks>
/// <para><b>Not measured here: the CLI is not installed on the machine this was written on.</b> Every
/// detail is t3code's (<c>GrokAcpSupport.ts</c>, <c>XAiAcpExtension.ts</c>, <c>GrokAdapter.ts</c>), which
/// runs it in production; the first run against a real <c>grok</c> should be read against this list and
/// the list corrected where it is wrong.</para>
/// <list type="bullet">
/// <item>The login is <c>xai.api_key</c> when <c>XAI_API_KEY</c> is set and <c>cached_token</c> (from
/// <c>grok login</c>) otherwise.</item>
/// <item>A turn can end by <c>_x.ai/session/prompt_complete</c> before the prompt's own answer arrives;
/// whichever is first ends it, and <c>stopReason: "rate_limit"</c> is the usage limit.</item>
/// <item><c>x.ai/ask_user_question</c> is a request whose answer is
/// <c>{"outcome":"accepted","answers":{"&lt;question text&gt;":["label"]}}</c> or
/// <c>{"outcome":"cancelled"}</c>; the params may arrive wrapped in <c>{method, params}</c>.</item>
/// <item><c>x.ai/exit_plan_mode</c> carries the plan as <c>planContent</c> and is answered
/// <c>approved</c>, <c>request_changes</c> with feedback, or <c>abandoned</c>.</item>
/// <item>Each method is also accepted with a leading underscore, which t3code handles as the same.</item>
/// </list>
/// </remarks>
public sealed class GrokAcpSession : AcpAgentSession
{
    private readonly AgentSessionLaunch _launch;
    private readonly ConcurrentDictionary<string, string> _promptTurns = new(StringComparer.Ordinal);
    private int _promptCounter;

    private readonly GrokAgent _agent;

    // The model the agent runs now. session/set_model carries a model and an effort together, so an effort
    // changed alone has to name the model already running rather than the one asked for at launch.
    private string _model;

    public GrokAcpSession(AgentSessionLaunch launch, GrokAgent agent, IAgentEventSink sink)
        : base(launch.StartInfo([.. GrokAgent.AcpArguments(launch.Behaviour), .. launch.ExtraArgs]),
            launch.WorkingDirectory, launch.ResumeToken, sink)
    {
        _launch = launch;
        _agent = agent;
        _model = launch.Model;
    }

    protected override IReadOnlyList<SessionOption> ModeOptions =>
        SessionSettingOptions.Modes(_agent, _launch.Runtime.Instance);

    protected override IReadOnlyList<SessionOption> EffortOptions =>
        SessionSettingOptions.Efforts(_agent, _launch.Runtime.Instance);

    protected override string? CurrentModel => _model.Length > 0 ? _model : null;

    protected override string? CurrentMode => SessionSettingOptions.ModeId(_launch.Behaviour);

    protected override string? CurrentEffort => SessionSettingOptions.EffortId(_launch.Effort);

    /// <summary>
    /// A model and an effort through <c>session/set_model</c> with <c>_meta.reasoningEffort</c>, as t3code
    /// switches them; the permission mode is Grok's command line (<c>--permission-mode</c>) and needs a restart.
    /// </summary>
    public override async Task<SettingsChangeOutcome> ChangeSettingsAsync(SessionSettings settings, CancellationToken ct)
    {
        if (settings.Mode is not null) return SettingsChangeOutcome.NeedsRestart;
        if (SessionId is null) return SettingsChangeOutcome.NeedsRestart;

        var model = settings.Model is { Length: > 0 } chosenModel ? chosenModel : _model;
        if (model.Length == 0) return SettingsChangeOutcome.NeedsRestart;

        string? effort = null;
        // Tool default is the absence of a level of ours, and _meta.reasoningEffort has no way to say that:
        // left out, the session goes on reasoning at the level it was started with, so nothing would change
        // while the chooser said it had. The session is started again instead, without the flag.
        if (settings.Effort is not null)
        {
            if (SessionSettingOptions.ParseEffort(settings.Effort) is not { } chosen
                || AiEfforts.Name(chosen) is not { } level)
                return SettingsChangeOutcome.NeedsRestart;

            effort = level;
        }

        try
        {
            await Peer.RequestAsync("session/set_model", new
            {
                sessionId = SessionId,
                modelId = model,
                _meta = effort is null ? null : new { reasoningEffort = effort },
            }, ct, TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is JsonRpcException or TimeoutException)
        {
            Sink.Emit(new NoticeRaised(NoticeLevel.Warning, $"Grok did not take the change: {ex.Message}"));
            return SettingsChangeOutcome.Rejected;
        }

        _model = model;
        Sink.Emit(new SessionConfigured(model, null, null, settings.Effort));
        return SettingsChangeOutcome.Applied;
    }

    protected override string? AuthenticationMethod(JsonElement initializeResult)
    {
        var offered = initializeResult.Items("authMethods").Select(m => m.Str("id")).OfType<string>().ToList();
        var hasKey = _launch.Environment.TryGetValue("XAI_API_KEY", out var key)
            ? !string.IsNullOrEmpty(key)
            : !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XAI_API_KEY"));

        var wanted = hasKey ? "xai.api_key" : "cached_token";
        return offered.Count == 0 || offered.Contains(wanted) ? wanted : offered[0];
    }

    protected override async Task AfterSessionStartedAsync(JsonElement session, CancellationToken ct)
    {
        var model = _launch.Model;
        var current = session.Prop("models").Str("currentModelId");
        var effort = AiEfforts.Name(_launch.Effort);
        if (model.Length == 0) _model = current ?? "";
        if (model.Length == 0 && effort is null) return;
        if (SessionId is null) return;

        try
        {
            await Peer.RequestAsync("session/set_model", new
            {
                sessionId = SessionId,
                modelId = model.Length > 0 ? model : current,
                _meta = effort is null ? null : new { reasoningEffort = effort },
            }, ct, TimeSpan.FromSeconds(30));
        }
        catch (JsonRpcException ex)
        {
            Sink.Emit(new NoticeRaised(NoticeLevel.Warning, $"Grok did not take the model or effort: {ex.Message}"));
        }
    }

    protected override object PromptParameters(string sessionId, IReadOnlyList<object> prompt)
    {
        var promptId = $"mtiles-xai-prompt-{Interlocked.Increment(ref _promptCounter)}";
        if (CurrentTurnId is { } turn) _promptTurns[promptId] = turn;
        return new { sessionId, prompt, _meta = new { promptId, requestId = promptId } };
    }

    protected override void OnExtensionNotification(string method, JsonElement parameters)
    {
        if (Unprefixed(method) != "x.ai/session/prompt_complete") return;

        var turn = parameters.Str("promptId") is { } promptId && _promptTurns.TryRemove(promptId, out var owner)
            ? owner
            : CurrentTurnId;
        if (turn is null) return;

        switch (parameters.Str("stopReason"))
        {
            case "cancelled":
                CompleteTurn(turn, TurnOutcome.Interrupted);
                break;
            case "rate_limit":
                CompleteTurn(turn, TurnOutcome.Failed, "Grok's usage limit has been reached.");
                break;
            case "error":
                CompleteTurn(turn, TurnOutcome.Failed,
                    parameters.Prop("agentResult").Str("message") ?? "Grok reported an error.");
                break;
            default:
                CompleteTurn(turn, TurnOutcome.Completed);
                break;
        }
    }

    protected override async Task<object?> OnExtensionRequestAsync(string method, JsonElement parameters)
    {
        // t3code accepts the params bare or wrapped as {method, params}.
        var body = parameters.Prop("params") is { ValueKind: JsonValueKind.Object } inner ? inner : parameters;

        switch (Unprefixed(method))
        {
            case "x.ai/ask_user_question":
                return await AskQuestionsAsync(body);
            case "x.ai/exit_plan_mode":
                return await ReviewPlanAsync(body);
            default:
                Trace.TraceInformation($"[AgentSessions] Grok asked for {method}, which is not handled.");
                throw new JsonRpcException(JsonRpcPeer.MethodNotFound, $"Method not found: {method}");
        }
    }

    private async Task<object> AskQuestionsAsync(JsonElement body)
    {
        var questions = body.Items("questions").Select(q => new UserQuestion(
            q.Str("question") ?? "",
            null,
            q.Str("question") ?? "",
            [.. q.Items("options").Select(o => new QuestionOption(o.Str("label") ?? "", o.Str("description")))],
            q.Bool("multiSelect") == true,
            AllowsCustomAnswer: true)).ToList();

        var answers = await AskAsync(questions);
        if (answers is null) return new { outcome = "cancelled" };

        var labels = new Dictionary<string, IReadOnlyList<string>>();
        var notes = new Dictionary<string, object>();
        foreach (var question in questions)
        {
            if (!answers.TryGetValue(question.Id, out var chosen)) continue;
            var known = chosen.Where(answer => question.Options.Any(o => o.Label == answer)).ToList();
            var typed = chosen.Except(known).ToList();
            labels[question.Id] = typed.Count > 0 ? [.. known, "Other"] : known;
            if (typed.Count > 0) notes[question.Id] = new { notes = string.Join("\n", typed) };
        }

        return new { outcome = "accepted", answers = labels, annotations = notes };
    }

    private async Task<object> ReviewPlanAsync(JsonElement body)
    {
        var plan = body.Str("planContent");
        if (!string.IsNullOrWhiteSpace(plan)) Sink.Emit(new PlanProposed(plan) { TurnId = CurrentTurnId });

        const string approve = "Implement the plan";
        const string revise = "Revise the plan";
        var answers = await AskAsync(
        [
            new UserQuestion("plan", "Plan", "Grok has finished planning.",
                [new QuestionOption(approve), new QuestionOption(revise, "Type what to change below.")],
                MultiSelect: false, AllowsCustomAnswer: true),
        ]);

        var chosen = answers?.GetValueOrDefault("plan") ?? [];
        if (answers is null) return new { outcome = "abandoned" };
        if (chosen.Contains(approve)) return new { outcome = "approved" };

        var feedback = string.Join("\n", chosen.Where(answer => answer != revise));
        return new { outcome = "request_changes", feedback = feedback.Length > 0 ? feedback : "Please revise the plan." };
    }

    private static string Unprefixed(string method) => method.StartsWith('_') ? method[1..] : method;
}
