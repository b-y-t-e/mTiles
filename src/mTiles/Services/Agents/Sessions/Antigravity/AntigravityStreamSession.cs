using System.Text.Json;
using System.Text.Json.Nodes;
using mTiles.AgentSessions;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Protocols;
using mTiles.Models;

namespace mTiles.Services.Agents.Sessions.Antigravity;

/// <summary>
/// A conversation with agy through its print mode, kept open: <c>--input-format stream-json
/// --output-format stream-json</c>.
/// </summary>
/// <remarks>
/// <para><b>Measured 2026-09-15 against agy 1.1.26</b>, and chosen over ACP for a reason outside our
/// control: t3code speaks ACP to a separate <c>agy_acp_server</c> binary that the CLI install does not
/// carry. What the CLI has is this — each line written to stdin as
/// <c>{"event":"user","message":{"role":"user","content":[{"type":"text","text":…}]}}</c> runs a turn,
/// and the process stays for the next one. Three things measured about it: the input must not start
/// with a byte order mark, every message needs the <c>event</c> field, and only text content is accepted.
/// </para>
/// <para><b>Headless mode cannot ask for permission</b> — agy auto-denies and says so (<c>a tool required
/// the "command" permission that headless mode cannot prompt for</c>). So there are no approval buttons
/// for agy: what it may do is the instance's mode, and a denial arrives as a notice naming it.</para>
/// <para>There is no interrupt message either, so stopping a turn ends the process and the next message
/// starts it again on the same conversation (<c>--conversation</c>).</para>
/// <para><c>--add-dir</c> names the workspace: without it agy works in its own scratch directory and
/// goes looking for the files it was asked about (measured — it read its own transcript to find out
/// where it was).</para>
/// </remarks>
public sealed class AntigravityStreamSession(AgentSessionLaunch launch, AntigravityAgent agent, IAgentEventSink sink)
    : IAgentSession, IProcessBackedSession
{
    private readonly AntigravityStreamMapper _mapper = new();
    private readonly Lock _gate = new();
    private AgentProcess? _process;
    private string? _conversationId = launch.ResumeToken;
    private string? _turnId;
    private int _queuedTurns;
    private bool _stopping;

    /// <inheritdoc />
    public int? ChildProcessId => _process?.ProcessId;

    public Task StartAsync(CancellationToken ct)
    {
        StartProcess();
        sink.Emit(new SessionStateChanged(AgentSessionState.Ready));
        return Task.CompletedTask;
    }

    public async Task SendAsync(AgentTurnInput input, CancellationToken ct)
    {
        if (input.Images.Count > 0)
            sink.Emit(new NoticeRaised(NoticeLevel.Warning, "agy's stream input takes text only; the images were not sent."));

        AgentProcess process;
        lock (_gate)
        {
            process = _process ?? StartProcess();
            // agy answers each line with its own result, so a message sent mid-turn is a turn of its own.
            if (_turnId is null) BeginTurn();
            else _queuedTurns++;
        }

        var message = new JsonObject
        {
            ["event"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = input.Text }),
            },
        };
        await process.WriteLineAsync(message.ToJsonString(), ct);
    }

    public async Task InterruptAsync(CancellationToken ct)
    {
        AgentProcess? process;
        lock (_gate)
        {
            if (_turnId is null) return;
            process = _process;
            _process = null;
            _stopping = true;
        }

        if (process is not null) await process.DisposeAsync();
        lock (_gate)
        {
            _queuedTurns = 0;
            EndTurn(TurnOutcome.Interrupted, null);
            _stopping = false;
        }
    }

    /// <summary>agy asks nothing in this mode.</summary>
    public Task RespondToApprovalAsync(string requestId, ApprovalDecision decision, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>agy asks nothing in this mode.</summary>
    public Task AnswerQuestionsAsync(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers,
        CancellationToken ct) =>
        Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        AgentProcess? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
            _stopping = true;
        }

        if (process is not null) await process.DisposeAsync();
    }

    private AgentProcess StartProcess()
    {
        List<string> arguments = ["--input-format", "stream-json", "--output-format", "stream-json"];
        arguments.AddRange(agent.BehaviourArgs(launch.Behaviour, AiUsage.Interactive));
        arguments.AddRange(agent.EffortArgs(launch.Effort, AiUsage.Interactive));
        arguments.AddRange(agent.ModelArgs(launch.Model, AiUsage.Interactive));
        arguments.AddRange(["--add-dir", launch.WorkingDirectory]);
        if (_conversationId is { Length: > 0 } conversation) arguments.AddRange(["--conversation", conversation]);
        arguments.AddRange(launch.ExtraArgs);
        // The prompt is the print flag's own value and comes from stdin, so it is empty and attached.
        arguments.Add("--print=");

        var process = AgentProcess.Start(launch.StartInfo(arguments), OnLine);
        _process = process;
        _ = WatchExitAsync(process);
        return process;
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

        string? turnId;
        lock (_gate) turnId = _turnId;
        foreach (var e in _mapper.Map(root, turnId))
        {
            if (e is SessionConfigured { ResumeToken: { } token }) _conversationId = token;
            sink.Emit(e);
        }

        if (root.Str("event") == "result")
        {
            var (outcome, error) = AntigravityStreamMapper.OutcomeOf(root);
            lock (_gate) EndTurn(outcome, error);
        }
    }

    private void BeginTurn()
    {
        _turnId = $"turn-{Guid.NewGuid():N}";
        _mapper.BeginTurn();
        sink.Emit(new TurnStarted { TurnId = _turnId });
    }

    /// <summary>Ends the current turn and opens the next queued one — unless the turn was stopped, which
    /// ends the process and takes the queued messages with it.</summary>
    private void EndTurn(TurnOutcome outcome, string? error)
    {
        if (_turnId is null) return;
        sink.Emit(new TurnCompleted(outcome, error) { TurnId = _turnId });
        _turnId = null;

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

    private async Task WatchExitAsync(AgentProcess process)
    {
        var code = await process.Exited;
        lock (_gate)
        {
            if (_stopping || !ReferenceEquals(_process, process)) return;
            _process = null;
            // The messages waiting behind this turn were written to the process that has just ended.
            _queuedTurns = 0;
            EndTurn(TurnOutcome.Failed, "agy ended during the turn.");
        }

        var stderr = process.StderrText;
        if (code is not (0 or null) && !process.StoppedByUs)
            sink.Emit(new NoticeRaised(NoticeLevel.Error,
                $"agy exited with code {code}." + (stderr.Length > 0 ? "\n" + AgentProcess.Tail(stderr) : "")));
    }
}
