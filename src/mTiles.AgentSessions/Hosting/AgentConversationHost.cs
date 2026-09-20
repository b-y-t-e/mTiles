using System.Diagnostics;
using System.Threading.Channels;
using mTiles.AgentSessions.Checkpoints;
using mTiles.AgentSessions.Commands;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Storage;

namespace mTiles.AgentSessions.Hosting;

/// <summary>
/// One conversation, alive: the agent's session, the stored events, the picture they make, and the
/// checkpoints between turns.
/// </summary>
/// <remarks>
/// <para><b>Every viewer goes through this and nothing else</b> — the desktop tile now, a browser later.
/// It owns the three rules that must not be decided twice: an event is numbered, stored and folded in
/// that order and under one lock; a user message is recorded by the host, never echoed by an agent; and a
/// turn is bracketed by two checkpoints.</para>
/// <para><b>Writes are off the caller's thread and in order.</b> A session emits from its reader thread,
/// often a token at a time; each event is folded into <see cref="State"/> at once and queued for the
/// store, where consecutive text deltas of one message are merged into one row before the batch is
/// written. Replaying merged deltas makes the same text, so nothing visible is lost.</para>
/// </remarks>
public sealed class AgentConversationHost : IAgentEventSink, IAsyncDisposable
{
    private readonly IConversationStore _store;
    private readonly ITurnCheckpoints? _checkpoints;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Channel<AgentEvent> _pending = Channel.CreateUnbounded<AgentEvent>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private readonly SemaphoreSlim _checkpointChain = new(1, 1);
    private readonly SemaphoreSlim _sendOrder = new(1, 1);

    private ConversationRecord _record;
    private SessionAccount? _account;
    private ConversationState _state;
    private bool _recordChanged;
    private long _sequence;
    private int _checkpointCount;
    private Task<string?>? _turnBaseCheckpoint;
    private Task<string?>? _queuedTurnBaseCheckpoint;
    private Task _turnEndCapture = Task.CompletedTask;
    private IAgentSession? _session;
    private long _sessionEpoch;
    private bool _closing;
    private bool _disposed;

    public AgentConversationHost(ConversationRecord record, IConversationStore store,
        ITurnCheckpoints? checkpoints, TimeProvider? time = null)
    {
        _store = store;
        _checkpoints = checkpoints;
        _time = time ?? TimeProvider.System;

        // A tile moved onto another agent keeps its id, and the old agent's conversation is not this one's:
        // its events describe work the new agent never did, and its token would be handed to a CLI that
        // has never seen it. Nor is it the host's to delete — the viewer asks, then forgets it on purpose.
        var stored = store.Find(record.Id);
        if (stored is not null && stored.AgentId != record.AgentId)
            throw new ConversationOfAnotherAgentException(record.Id, stored.AgentId);
        _record = stored ?? record;

        var events = store.ReadEvents(_record.Id);
        _state = ConversationReducer.Replay(events);
        _sequence = store.LastSequence(_record.Id);
        _checkpointCount = events.Count(e => e is CheckpointCaptured);
        _state = ConversationReducer.Apply(_state, Stamp(new SessionStateChanged(AgentSessionState.Stopped)));

        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Raised after every event is folded in, on the thread that emitted it.</summary>
    public event Action<ConversationState, AgentEvent>? Changed;

    public string ConversationId => _record.Id;

    /// <summary>The agent's handle on this conversation, for the next launch.</summary>
    public string? ResumeToken => _record.ResumeToken;

    public ConversationState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    /// <summary>Starts a session built by the agent, bound to this conversation.</summary>
    /// <param name="createSession">The agent's factory, handed a sink bound to this start alone: a session
    /// this host has already replaced goes on reporting — its exit watcher says "stopped" whether it was
    /// disposed or died — and that arriving after the new session is ready would leave the host believing
    /// nothing is running while the CLI answers perfectly well.</param>
    /// <param name="account">Who the agent is running as, stamped onto every <see cref="SessionConfigured"/>
    /// this session reports. The session knows its CLI and not the row in Settings it came from, so this is
    /// the one place the two are joined — and it is set before the session starts, because the first thing
    /// several agents report is the id that resumes them.</param>
    public async Task StartAsync(
        Func<IAgentEventSink, IAgentSession> createSession, SessionAccount? account, CancellationToken ct)
    {
        await StopSessionAsync();
        if (IsClosing) return;
        lock (_gate) _account = account;
        var sink = NewSessionSink();
        var session = createSession(sink);
        if (!TryAttach(session))
        {
            await session.DisposeAsync();
            return;
        }

        Emit(new SessionStateChanged(AgentSessionState.Starting));
        try
        {
            await session.StartAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Emit(new SessionStateChanged(AgentSessionState.Failed, ex.Message));
        }
        finally
        {
            // A host closed while the session was starting has already stopped it, but a start still in
            // flight can have spawned its process after that stop — disposing again is what reaches it.
            if (IsClosing) await session.DisposeAsync();
        }
    }

    private bool IsClosing
    {
        get
        {
            lock (_gate) return _closing;
        }
    }

    /// <summary>Attaches the session unless the host is already closing, where nobody would ever stop it.</summary>
    private bool TryAttach(IAgentSession session)
    {
        lock (_gate)
        {
            if (_closing) return false;
            _session = session;
            return true;
        }
    }

    /// <summary>Opens the next session's sink, which is what silences the one before it.</summary>
    private SessionSink NewSessionSink()
    {
        lock (_gate) return new SessionSink(this, ++_sessionEpoch);
    }

    /// <summary>One session's own voice: what it emits after the host has moved on is dropped.</summary>
    private sealed class SessionSink(AgentConversationHost host, long epoch) : IAgentEventSink
    {
        public void Emit(AgentEvent agentEvent) => host.Emit(agentEvent, epoch);
    }

    /// <summary>Carries out a viewer's request.</summary>
    public async Task ExecuteAsync(AgentCommand command, CancellationToken ct)
    {
        var session = LiveSession;
        switch (command)
        {
            case SendMessage send when session is not null:
                await SendAsync(session, send, ct);
                break;
            case InterruptTurn when session is not null:
                await session.InterruptAsync(ct);
                break;
            // Not a SendMessage carrying a slash command: only one of the three agents that can do this
            // takes it as a message at all, and a "/compact" written into the transcript as something the
            // user said is a line the other two would never have produced.
            case CompactContext when session is ICompactingSession compacting:
                await compacting.CompactAsync(ct);
                break;
            case CompactContext when session is not null:
                Emit(new NoticeRaised(NoticeLevel.Warning, "This agent cannot compact its own context."));
                break;
            case RespondToApproval approval when session is not null:
                await session.RespondToApprovalAsync(approval.RequestId, approval.Decision, ct);
                break;
            case AnswerQuestions answers when session is not null:
                await session.AnswerQuestionsAsync(answers.RequestId, answers.Answers, ct);
                break;
            case RestoreCheckpoint restore:
                await RestoreAsync(restore.CheckpointId, ct);
                break;
            case ChangeSessionSettings change when session is not null:
                await ChangeSettingsAsync(session, change.Settings, ct);
                break;
            default:
                Emit(new NoticeRaised(NoticeLevel.Warning, "The agent is not running."));
                break;
        }
    }

    /// <summary>
    /// Raised when a settings change needs the session started again — the one thing the host cannot do
    /// itself, because the launch belongs to whoever started it.
    /// </summary>
    /// <remarks>Raised only between turns: restarting under a working agent would end its turn, so a change
    /// that needs a restart while one runs is refused out loud instead.</remarks>
    public event Action<SessionSettings>? RestartRequested;

    /// <summary>Raised when the running session took a settings change — the moment it is worth keeping.</summary>
    /// <remarks>Not raised for a change refused under a working agent, so a viewer that keeps only what this or
    /// <see cref="RestartRequested"/> reports never keeps a change nothing is running under.</remarks>
    public event Action<SessionSettings>? SettingsApplied;

    /// <summary>
    /// Hands the session one setting at a time, so a model it took is reported as taken even when the mode
    /// beside it is refused or needs a restart — asked all at once, a partial success read as a refusal while
    /// the agent already ran on the new model.
    /// </summary>
    private async Task ChangeSettingsAsync(IAgentSession session, SessionSettings settings, CancellationToken ct)
    {
        var applied = new SessionSettings();
        var needsRestart = new SessionSettings();
        foreach (var setting in settings.OneByOne())
        {
            var outcome = await session.ChangeSettingsAsync(setting, ct);
            if (outcome == SettingsChangeOutcome.Applied) applied = applied.With(setting);
            else if (outcome == SettingsChangeOutcome.NeedsRestart) needsRestart = needsRestart.With(setting);
        }

        if (!applied.IsEmpty)
        {
            RecordChosenModel(applied);
            SettingsApplied?.Invoke(applied);
        }
        if (!needsRestart.IsEmpty) RequestRestart(needsRestart);
    }

    private void RequestRestart(SessionSettings settings)
    {
        if (State.IsWorking)
        {
            Emit(new NoticeRaised(NoticeLevel.Warning,
                "This agent cannot switch while it works. Stop the turn, or change it again once the turn is over."));
            return;
        }

        RecordChosenModel(settings);
        RestartRequested?.Invoke(settings);
    }

    /// <summary>Writes down a model somebody picked, as distinct from the one a session reports running.
    /// </summary>
    /// <remarks>Only a change that was taken, and only here: a session's own report is mostly the CLI's
    /// resolution of the instance's answer, and a viewer restoring that would freeze it.</remarks>
    private void RecordChosenModel(SessionSettings settings)
    {
        if (settings.Model is { Length: > 0 } model) Emit(new SessionModelChosen(model));
    }

    /// <summary>The unified diff of one turn, for one file or — with none named — all of them.</summary>
    /// <remarks>A file is asked for under every name it had in the turn, which for a rename is two: git
    /// applies the pathspec before it looks for renames, so the new name alone answers with the file as
    /// though it had just been created.</remarks>
    public Task<string> DiffAsync(CheckpointEntry checkpoint, ChangedFile? file, CancellationToken ct) =>
        _checkpoints is null
            ? Task.FromResult("")
            : _checkpoints.DiffAsync(checkpoint.BaseCheckpointId, checkpoint.Id, PathsOf(file), ct);

    private static IReadOnlyList<string>? PathsOf(ChangedFile? file) => file is null
        ? null
        : file.OldPath is { Length: > 0 } old ? [old, file.Path] : [file.Path];

    /// <summary>Whether a session is attached that a message can be handed to.</summary>
    public bool HasSession => LiveSession is not null;

    /// <summary>The agent's own process, where this agent runs as one and is running now.</summary>
    /// <remarks>Asked rather than remembered, so a session that has ended answers nothing: the tile
    /// reports this as the root of what it started, and a stale id is somebody else's process.</remarks>
    public int? ChildProcessId => (LiveSession as IProcessBackedSession)?.ChildProcessId;

    /// <summary>Removes the conversation, its events and its checkpoints for good, and ends this host.</summary>
    /// <remarks>Everything still on its way to the store — the session's last events, queued deltas, the
    /// closing turn's checkpoint — is written <em>before</em> the delete, or it lands afterwards under the
    /// same id and the next conversation on this tile opens with the remains of this one.</remarks>
    public async Task ForgetAsync(CancellationToken ct)
    {
        await DrainAsync();
        if (_checkpoints is not null) await _checkpoints.ForgetAsync(_record.Id, ct);
        _store.Delete(_record.Id);
    }

    public void Emit(AgentEvent agentEvent) => Emit(agentEvent, null);

    /// <summary>Folds an event in, unless it comes from a session this host has already replaced.</summary>
    /// <param name="sessionEpoch">The session that emitted it, or null for the host speaking for itself.</param>
    private void Emit(AgentEvent agentEvent, long? sessionEpoch)
    {
        ConversationState state;
        AgentEvent stamped;
        lock (_gate)
        {
            if (_disposed || IsSecondClosingOfTurn(agentEvent)) return;
            // Tested under the gate the epoch is raised in, or a stale event could pass a check made before it.
            if (sessionEpoch is { } epoch && epoch != _sessionEpoch) return;
            stamped = Stamp(agentEvent with { Sequence = ++_sequence });
            _state = ConversationReducer.Apply(_state, stamped);
            state = _state;
            // Queued and recorded in the order they were numbered; a viewer orders by LastSequence.
            // A transient event is not: it says what the session running now can do, which the next session
            // says again at start, and storing it would grow the conversation by a model catalogue per launch.
            if (!stamped.IsTransient) _pending.Writer.TryWrite(stamped);
            UpdateRecord(stamped);
            // Under the gate, so a drain reading _turnEndCapture afterwards cannot miss this turn's capture.
            if (stamped is TurnCompleted) CloseTurn();
        }

        React(stamped);

        try
        {
            Changed?.Invoke(state, stamped);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[AgentSessions] A viewer failed on {stamped.GetType().Name}: {ex}");
        }
    }

    public async ValueTask DisposeAsync() => await DrainAsync();

    /// <summary>Stops the session and waits until everything it produced is stored. Safe to call twice.</summary>
    private async Task DrainAsync()
    {
        lock (_gate) _closing = true;
        await StopSessionAsync();
        // A stopped session's own "interrupted" arrives from its exit watcher, which can still be on its way;
        // the host closes the turn itself rather than wait on it, and whichever comes second is dropped.
        if (HasOpenTurn) Emit(new TurnCompleted(TurnOutcome.Interrupted));
        // The last turn's checkpoint is what its diff and Undo are drawn from, so it is waited for
        // while events can still be recorded.
        Task turnEndCapture;
        lock (_gate) turnEndCapture = _turnEndCapture;
        await turnEndCapture;
        lock (_gate) _disposed = true;
        _pending.Writer.TryComplete();
        await _writer;
    }

    private bool HasOpenTurn
    {
        get
        {
            lock (_gate) return _state.IsWorking || _turnBaseCheckpoint is not null;
        }
    }

    /// <summary>A turn closed by the drain and then again by the session it stopped. Called under the gate.</summary>
    private bool IsSecondClosingOfTurn(AgentEvent e) =>
        e is TurnCompleted && _closing && !_state.IsWorking && _turnBaseCheckpoint is null;

    /// <summary>
    /// The attached session, once it has said it is ready and until its process ends. A session that stopped
    /// or failed stays attached until the next start, and a message handed to it would open a turn nothing
    /// ever closes — a tile showing "Working" for good. One still starting has no thread to put a message
    /// in, so it would be recorded, photographed and then silently dropped by the session.
    /// </summary>
    private IAgentSession? LiveSession
    {
        get
        {
            lock (_gate)
                return _state.SessionState is AgentSessionState.Ready or AgentSessionState.Running
                    or AgentSessionState.WaitingForUser
                    ? _session
                    : null;
        }
    }

    private async Task SendAsync(IAgentSession session, SendMessage send, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(send.Text) && send.Images is not { Count: > 0 }) return;

        // In order: a message sent while the previous one's photograph is still being taken reaches the agent
        // after it, and finds that turn's baseline already claimed rather than replacing it.
        await _sendOrder.WaitAsync(ct);
        TaskCompletionSource<string?>? claim = null;
        try
        {
            claim = ClaimTurnBaseline();
            Emit(new UserMessageAdded($"user-{Guid.NewGuid():N}", send.Text.Trim(), send.Images ?? []));
            if (claim is not null) await CaptureTurnBaselineAsync(claim, ct);
            await session.SendAsync(new AgentTurnInput(send.Text.Trim(), send.Images ?? []), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && claim is not null)
        {
            CloseRefusedTurn(claim.Task, ex);
            throw;
        }
        finally
        {
            _sendOrder.Release();
        }
    }

    /// <summary>
    /// Claims the next turn's baseline when no turn is open — neither running nor still being photographed —
    /// in one step under the gate, so two messages cannot both believe they start it.
    /// </summary>
    private TaskCompletionSource<string?>? ClaimTurnBaseline()
    {
        lock (_gate)
        {
            if (_state.IsWorking || _turnBaseCheckpoint is not null) return null;
            var claim = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _turnBaseCheckpoint = claim.Task;
            _queuedTurnBaseCheckpoint = null;
            return claim;
        }
    }

    /// <summary>
    /// Closes the turn a message opened when the session refused it — a timeout or a failed request — since
    /// nothing else ever will, and a turn left open keeps the tile "Working" and Undo refused until a restart.
    /// </summary>
    /// <remarks>Only while that message's baseline is still the open turn's: if the session already closed
    /// it, a second closing would end whatever came after.</remarks>
    private void CloseRefusedTurn(Task<string?> baseline, Exception refusal)
    {
        lock (_gate)
            if (_turnBaseCheckpoint != baseline) return;
        Emit(new TurnCompleted(TurnOutcome.Failed, refusal.Message));
    }

    /// <summary>The photograph the agent's work is measured against, taken before the agent has the message.</summary>
    private async Task CaptureTurnBaselineAsync(TaskCompletionSource<string?> claim, CancellationToken ct)
    {
        try
        {
            claim.SetResult(await CaptureAsync(null, ct));
        }
        catch (OperationCanceledException)
        {
            // The message never reaches the agent, so no turn opens on this claim.
            lock (_gate)
                if (_turnBaseCheckpoint == claim.Task) _turnBaseCheckpoint = null;
            claim.SetResult(null);
            throw;
        }
    }

    /// <summary>Keeps the conversation's row in step with the events that change it. Called under the gate.</summary>
    /// <remarks>Only marks the row: it is written by the writer, because the thread emitting a user message is
    /// the UI thread, and a store busy with a batch would hold it for as long as the store takes.</remarks>
    private void UpdateRecord(AgentEvent e)
    {
        switch (e)
        {
            case SessionConfigured { ResumeToken: { Length: > 0 } token } when token != _record.ResumeToken:
                _record = _record with { ResumeToken = token, UpdatedAt = e.At };
                _recordChanged = true;
                break;
            case UserMessageAdded:
                _record = _record with { UpdatedAt = e.At };
                _recordChanged = true;
                break;
        }
    }

    /// <summary>What the host does about a turn beginning or ending.</summary>
    private void React(AgentEvent e)
    {
        switch (e)
        {
            case TurnStarted:
                AdoptQueuedTurnBase();
                break;
        }
    }

    /// <summary>
    /// A turn the agent started from a message queued during the previous one had no photograph of its
    /// own taken; the previous turn's closing checkpoint is where its work begins.
    /// </summary>
    private void AdoptQueuedTurnBase()
    {
        lock (_gate)
        {
            if (_turnBaseCheckpoint is not null) return;
            _turnBaseCheckpoint = _queuedTurnBaseCheckpoint;
            _queuedTurnBaseCheckpoint = null;
        }
    }

    /// <summary>Starts the turn's closing checkpoint. Called under the gate, so the capture runs off it.</summary>
    private void CloseTurn()
    {
        if (_turnBaseCheckpoint is not { } baseline) return;
        _turnBaseCheckpoint = null;
        var closing = Task.Run(() => CaptureTurnEndAsync(baseline));
        _queuedTurnBaseCheckpoint = closing;
        _turnEndCapture = closing;
    }

    private async Task<string?> CaptureTurnEndAsync(Task<string?> baselineCapture)
    {
        try
        {
            var baseline = await baselineCapture;
            return baseline is null ? null : await CaptureAsync(baseline, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Takes a checkpoint and, where it closes a turn, says what the turn changed.</summary>
    private async Task<string?> CaptureAsync(string? baseline, CancellationToken ct)
    {
        if (_checkpoints is null) return null;

        await _checkpointChain.WaitAsync(ct);
        try
        {
            var id = await _checkpoints.CaptureAsync(_record.Id, _checkpointCount, ct);
            if (id is null) return null;
            _checkpointCount++;

            var files = baseline is null ? [] : await _checkpoints.ChangesAsync(baseline, id, ct);
            Emit(new CheckpointCaptured(id, baseline, files));
            return id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceWarning($"[AgentSessions] Checkpoint for {_record.Id} failed: {ex.Message}");
            return null;
        }
        finally
        {
            _checkpointChain.Release();
        }
    }

    private async Task RestoreAsync(string checkpointId, CancellationToken ct)
    {
        if (_checkpoints is null) return;

        await _checkpointChain.WaitAsync(ct);
        try
        {
            // Asked holding the chain: a turn whose baseline is still being photographed holds it, and once that
            // photograph is taken the turn is open, so the agent is never handed a tree being overwritten.
            if (HasOpenTurn)
            {
                Emit(new NoticeRaised(NoticeLevel.Warning, "Files cannot be restored while the agent is working."));
                return;
            }

            var replaced = await _checkpoints.RestoreAsync(checkpointId, ct);
            // The files are no longer what the last turn left, so the next turn is photographed afresh.
            lock (_gate) _queuedTurnBaseCheckpoint = null;
            Emit(new CheckpointRestored(checkpointId));
            Emit(new NoticeRaised(NoticeLevel.Info,
                $"The files as they were before this are kept in git: git checkout {replaced} -- ."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Emit(new NoticeRaised(NoticeLevel.Error, $"Restoring the files failed: {ex.Message}"));
        }
        finally
        {
            _checkpointChain.Release();
        }
    }

    private async Task StopSessionAsync()
    {
        IAgentSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
            // Raised here as well as at the next start, so a drain that starts nothing after it is covered.
            _sessionEpoch++;
        }

        if (session is null) return;
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[AgentSessions] Stopping the session of {_record.Id} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the conversation's row ahead of the events that belong to it: on the first batch where it is not
    /// stored yet, and after that whenever an event changed it.
    /// </summary>
    /// <remarks>Only the writer saves, so the latest record read here can never be overwritten by an older one.</remarks>
    private void SaveRecordBeforeBatch(bool firstWrite)
    {
        ConversationRecord record;
        bool changed;
        lock (_gate)
        {
            record = _record;
            changed = _recordChanged;
            _recordChanged = false;
        }

        try
        {
            if (changed || (firstWrite && _store.Find(record.Id) is null)) _store.Save(record);
        }
        catch when (changed)
        {
            // Tried again with the next batch, or a resume token lost to one failed write is lost for good.
            lock (_gate) _recordChanged = true;
            throw;
        }
    }

    /// <summary>Fills in what the host knows and the event did not carry: the time, and the account.</summary>
    /// <remarks>An event that already names an account keeps it — a mapper replaying what a CLI said about
    /// an earlier session is describing that session, not this one.</remarks>
    private AgentEvent Stamp(AgentEvent e)
    {
        var stamped = e.At == default ? e with { At = _time.GetUtcNow() } : e;
        return stamped switch
        {
            SessionConfigured { Account: null } configured when _account is not null =>
                configured with { Account = _account },
            // The session reports what its CLI can be switched to; whether it can compact is a fact about
            // the object we are holding, which only the host is looking at.
            SessionOptionsReported options => options with { CanCompact = _session is ICompactingSession },
            _ => stamped,
        };
    }

    private async Task WriteLoopAsync()
    {
        var reader = _pending.Reader;
        // A batch the store refused stays at the front of the next one: its events are already on screen, and
        // dropped here they would be missing when the conversation is reopened — messages, and the checkpoint
        // a turn's diff and Undo are drawn from. Rewriting them is harmless, since each carries its sequence.
        var unwritten = new List<AgentEvent>();
        var firstWrite = true;
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var e)) unwritten.Add(e);
            if (!TryWriteBatch(unwritten, firstWrite)) continue;
            unwritten.Clear();
            firstWrite = false;
        }

        // The last chance for events still refused when the host closed.
        if (unwritten.Count > 0) TryWriteBatch(unwritten, firstWrite);
    }

    private bool TryWriteBatch(IReadOnlyList<AgentEvent> batch, bool firstWrite)
    {
        try
        {
            SaveRecordBeforeBatch(firstWrite);
            _store.Append(_record.Id, EventBatch.Coalesce(batch));
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[AgentSessions] Writing {batch.Count} events of {_record.Id} failed: {ex.Message}");
            return false;
        }
    }
}
