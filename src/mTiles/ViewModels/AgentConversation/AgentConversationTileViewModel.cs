using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.AgentSessions.Checkpoints;
using mTiles.AgentSessions.Commands;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Hosting;
using mTiles.AgentSessions.Storage;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Agents.Sessions;
using mTiles.Services.Providers;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>
/// An agent held as a conversation: what it says and does, drawn by this application rather than by its
/// TUI.
/// </summary>
/// <remarks>
/// <para><b>Everything this does goes through <see cref="AgentConversationHost"/></b> — sending, stopping,
/// answering, restoring — and everything it draws is the host's <see cref="ConversationState"/>. The
/// agent is invisible here: the same view model draws Claude Code, codex, opencode, pi, agy and Grok, and
/// a browser will one day draw the same state without it.</para>
/// <para><b>Changes are drawn at most once per frame.</b> A session emits from its reader thread, a token
/// at a time; each change replaces the state waiting to be drawn and posts a draw only when none is
/// pending, so a fast stream costs one dispatch per frame rather than one per token.</para>
/// <para><b>Started by the view, not the constructor.</b> A workspace restores every tile it holds, and a
/// conversation's process is started when the tile is first shown — the same moment a terminal tile's
/// shell is.</para>
/// </remarks>
public sealed partial class AgentConversationTileViewModel : ObservableObject,
    IBusyTile, IMaximizableTile, ITextInputTile, IDescribedTile, ITileActions, IAgentTile, IProcessTile
{
    public const string NewConversationActionId = "new-conversation";
    public const string DeleteConversationActionId = "delete-conversation";

    private readonly string _workingDirectory;
    private readonly SettingsService _settings;
    private readonly IConversationStore _store;
    private readonly Func<string> _tileId;
    private readonly Action<Action> _post;
    private readonly Lock _drawGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly IAgentSessionStarter _sessionStarter;
    private AiAgentInstance? _latestPick;
    private ConversationSummary? _latestConversationPick;
    private string? _conversationId;
    private AgentConversationHost? _host;
    private ConversationState? _waitingToDraw;
    private bool _drawScheduled;
    private PlanUpdated? _drawnPlan;
    private bool _disposed;
    private bool _startRequested;

    [ObservableProperty] private string _draft = "";
    [ObservableProperty] private string? _launchProblem;
    [ObservableProperty] private bool _isStarting;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _usageText = "";
    [ObservableProperty] private QuestionRoundViewModel? _pendingQuestions;
    [ObservableProperty] private TileActivity _activity = TileActivity.Unknown;
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _modelText = "";
    [ObservableProperty] private SessionOption? _selectedMode;
    [ObservableProperty] private SessionOption? _selectedEffort;
    [ObservableProperty] private string? _composerNotice;

    private readonly Action? _requestSave;
    private SessionOverrides _overrides;
    private bool _restartQueued;
    private SessionOptionsReported? _drawnOptions;
    private bool _drawingSettings;
    private readonly ConversationAgentBinding _binding;

    public AgentConversationTileViewModel(string workingDirectory, SettingsService settings, IConversationStore store,
        AiAgentInstance instance, IAiAgent agent, Func<string> tileId, AgentSubstitution? substitution = null,
        SessionOverrides? overrides = null, Action? requestSave = null, Action<Action>? post = null,
        IAgentSessionStarter? sessionStarter = null, string? conversationId = null)
    {
        _sessionStarter = sessionStarter ?? AgentSessionStarter.Instance;
        _conversationId = conversationId is { Length: > 0 } ? conversationId : null;
        _workingDirectory = workingDirectory;
        _settings = settings;
        _store = store;
        _tileId = tileId;
        Instance = instance;
        Agent = agent;
        Substitution = substitution;
        _overrides = overrides ?? SessionOverrides.None;
        _requestSave = requestSave;
        _post = post ?? (action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background));
        FileMentions = new FileMentionsViewModel(new WorkspaceFileMentionSource(workingDirectory,
            settings.Settings.GitPath is { Length: > 0 } git ? git : "git"));
        _binding = new ConversationAgentBinding(store);
        Chooser = new AgentInstanceChooser(settings, () => Instance, IsRunning, () => ConversationAgentId, _post,
            instance => _ = RunAsync(() => SwitchInstanceAsync(instance)));
        Conversations = new ConversationChooser(store, workingDirectory, () => ConversationId, () => Agent.Id,
            RefusalFor, _post, summary => _ = RunAsync(() => SwitchConversationAsync(summary)),
            () => _ = RunAsync(NewConversationAsync));
    }

    /// <summary>The <c>@</c> file suggestions every box in this tile offers — the composer and an answer —
    /// the Goal tile's own, so a path is found the same way in both.</summary>
    public FileMentionsViewModel FileMentions { get; }

    /// <summary>What this tile runs differently from its instance — kept in the layout.</summary>
    public SessionOverrides Overrides => _overrides;

    /// <summary>The models the session offers; the model field also takes a name typed by hand.</summary>
    public ObservableCollection<string> ModelOptions { get; } = [];

    public ObservableCollection<SessionOption> ModeOptions { get; } = [];
    public ObservableCollection<SessionOption> EffortOptions { get; } = [];

    public bool HasModeOptions => ModeOptions.Count > 1;
    public bool HasEffortOptions => EffortOptions.Count > 1;

    /// <summary>Images going with the next message.</summary>
    public ObservableCollection<ImageAttachment> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    public string KindId => TileKindIds.AgentConversation;

    public AiAgentInstance Instance { get; private set; }
    public IAiAgent Agent { get; private set; }

    /// <summary>What the layout asked for, when it could not be honoured; saved in place of what runs.</summary>
    /// <remarks>Cleared the moment the user picks an instance themselves: what runs is then what was asked
    /// for, and saving the old request would put the tile back on a ghost at the next launch.</remarks>
    public AgentSubstitution? Substitution { get; private set; }

    /// <summary>The agents this conversation can be pointed at, and which of them can be picked now.</summary>
    public AgentInstanceChooser Chooser { get; }

    /// <summary>The conversations held in this workspace, and which of them this tile can be pointed at.</summary>
    public ConversationChooser Conversations { get; }

    /// <summary>
    /// The stored conversation this tile is showing.
    /// </summary>
    /// <remarks>
    /// <para><b>The tile's own id unless one was chosen</b>, which is what every conversation was before a
    /// list of them existed — so a tile nobody has pointed elsewhere opens exactly the conversation it always
    /// did, and its layout gains no field.</para>
    /// <para><b>Nothing is cached here.</b> The tile's id is read every time, because the id is installed on
    /// the leaf before the kind builds its content and a value taken any earlier would be the constructor's
    /// default — the trap <c>TileTreeSerializer</c> already spells out for <c>${tileId}</c>. A tile with no
    /// leaf behind it takes an id of its own when it starts (<see cref="ReplaceHostAsync"/>) and keeps it,
    /// rather than opening a throwaway conversation per launch.</para>
    /// </remarks>
    public string ConversationId => _conversationId ?? _tileId();

    /// <summary>The conversation to write into the layout, or null when it is the tile's own id and writing it
    /// would only say the same thing twice.</summary>
    public string? StoredConversationId => _conversationId is { } chosen && chosen != _tileId() ? chosen : null;

    /// <summary>Whether the agent is settled: a conversation belongs to the agent holding it.</summary>
    public bool IsBoundToItsAgent => ConversationAgentId is not null;

    /// <summary>The agent whose conversation this tile holds, or null while nothing has been said.</summary>
    private string? ConversationAgentId =>
        _binding.HeldAgentId(Agent.Id);

    /// <inheritdoc />
    /// <remarks>The CLI this tile started, so the workspace row's memory reading covers it: a
    /// conversation on Claude Code or opencode is a node process, and those are the heaviest things in
    /// most workspaces.</remarks>
    public int? ChildProcessId => _host?.ChildProcessId;

    /// <summary>A substitution onto another agent: its conversation is not this agent's to open.</summary>
    private bool RunsAnotherAgent => Substitution is { } substitution && substitution.RequestedAgentId != Agent.Id;

    public ObservableCollection<TimelineItemViewModel> Timeline { get; } = [];
    public ObservableCollection<ApprovalRequestViewModel> PendingApprovals { get; } = [];
    public ObservableCollection<PlanStep> PlanSteps { get; } = [];

    public bool HasPlan => PlanSteps.Count > 0;
    public bool HasLaunchProblem => LaunchProblem is not null;
    public bool IsEmpty => Timeline.Count == 0 && !IsStarting && LaunchProblem is null;

    /// <summary>Asked before anything is thrown away. Unwired answers no.</summary>
    public Func<string, Task<bool>>? ConfirmAction { get; set; }

    /// <summary>Raised when the timeline gained or changed an entry, so the view can follow the end.</summary>
    public event Action? TimelineChanged;

    public string HeaderNote => Model.Length > 0 ? $"{Instance.Name} · {Model}" : Instance.Name;

    public IReadOnlyList<TileAction> Actions =>
    [
        new(TileActionIds.Restart, "Restart agent", "restart", IsDestructive: true),
        // Not destructive any more, and that is the point of the list: a new conversation is started beside the
        // old one rather than over it, and the old one is a row in the chooser rather than something forgotten.
        new(NewConversationActionId, "New conversation", "new-conversation", NeedsLocalScreen: true),
        new(DeleteConversationActionId, "Delete this conversation", "delete", IsDestructive: true,
            NeedsLocalScreen: true),
    ];

    /// <summary>Opens the stored conversation and starts the agent, once.</summary>
    /// <remarks><b>Once is a flag, not <c>_host is null</c>.</b> The host is only assigned after the store has
    /// been read off the UI thread, and the view calls this on every attach — a tile re-parented while it
    /// is still starting (a layout rebuilt as the workspace opens, a split) attached twice in that window,
    /// and the second start queued behind the first and then disposed its freshly started agent to start
    /// another.</remarks>
    public void EnsureStarted()
    {
        if (_startRequested || _disposed) return;
        _startRequested = true;
        _ = StartAsync();
    }

    public async Task<TileActionResult> InvokeAsync(string id)
    {
        switch (id)
        {
            case TileActionIds.Restart:
                await StartAsync();
                return TileActionResult.Ok;
            case NewConversationActionId:
                await NewConversationAsync();
                return TileActionResult.Ok;
            case DeleteConversationActionId:
                await DeleteConversationAsync();
                return TileActionResult.Ok;
            default:
                return TileActionResult.Refused($"This tile has no '{id}'.");
        }
    }

    public bool TrySendText(string text, bool submit)
    {
        Draft = Draft.Length == 0 ? text : $"{Draft.TrimEnd()} {text}";
        if (submit) _ = SendAsync();
        return true;
    }

    public bool TryPressKey(TileKey key)
    {
        switch (key)
        {
            case TileKey.Enter:
                _ = SendAsync();
                return true;
            case TileKey.Escape when IsWorking:
                _ = InterruptAsync();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Hands the draft to the agent, and only clears it once there is an agent to hand it to — a message
    /// typed while the agent is still starting stays in the composer rather than being lost.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Draft;
        // Enter in the composer, a paired phone and dictation all call this without asking CanExecute.
        if (!CanSend() || (string.IsNullOrWhiteSpace(text) && Attachments.Count == 0) || _host is not { } host) return;
        List<ImageAttachment> images = [.. Attachments];
        // A host with no live agent refuses the message out loud, and the draft stays for the restart.
        if (host.HasSession)
        {
            Draft = "";
            Attachments.Clear();
            _binding.MessageSent();
            LastUsedAgentInstance.Remember(_settings, Instance);
            OnPropertyChanged(nameof(HasAttachments));
        }

        await RunAsync(() => host.ExecuteAsync(new SendMessage(text, images), _lifetime.Token));
    }

    private bool CanSend() => !IsStarting && LaunchProblem is null;

    /// <summary>The largest image handed to an agent, after the view has scaled it down.</summary>
    /// <remarks>Claude's API refuses an image over 5 MB, and every image is also stored in the conversation;
    /// the view scales a paste to at most 1568 pixels on its long edge first, so this is rarely reached.</remarks>
    public const int MaxImageBytes = 5 * 1024 * 1024;

    /// <summary>At most this many images go with one message.</summary>
    public const int MaxImages = 10;

    /// <summary>Adds an image to the next message.</summary>
    [RelayCommand]
    public void AttachImage(ImageAttachment image)
    {
        if (image.Base64Data.Length * 3L / 4 > MaxImageBytes)
        {
            ComposerNotice = "That image is larger than 5 MB and was not attached.";
            return;
        }

        if (Attachments.Count >= MaxImages)
        {
            ComposerNotice = $"At most {MaxImages} images go with one message.";
            return;
        }

        ComposerNotice = null;
        Attachments.Add(image);
        OnPropertyChanged(nameof(HasAttachments));
    }

    [RelayCommand]
    private void RemoveAttachment(ImageAttachment image)
    {
        Attachments.Remove(image);
        OnPropertyChanged(nameof(HasAttachments));
    }

    /// <summary>
    /// Switches the model, mode or effort: kept as this tile's override, handed to the running session, and —
    /// where the agent cannot switch while it runs — applied by starting the session again on the same
    /// conversation (<see cref="AgentConversationHost.RestartRequested"/>).
    /// </summary>
    /// <remarks>The override is kept only once the change is taken — with no session running (the next launch
    /// takes it), when the session applied it, or when it asks for a restart. A change the host refuses under a
    /// working agent is not kept, or the next launch would quietly start with what the screen says did not
    /// happen.</remarks>
    public async Task ChangeSettingsAsync(SessionSettings change)
    {
        if (change.IsEmpty) return;
        if (_host is not { } host || !host.HasSession)
        {
            KeepOverride(change);
            if (IsStarting) RestartOnceStarted();
            return;
        }

        await RunAsync(() => host.ExecuteAsync(new ChangeSessionSettings(change), _lifetime.Token));
    }

    /// <summary>
    /// A change made while the session is starting: that start may already have read the overrides, so the
    /// session is started again behind it, once, rather than coming up on what the chooser no longer says.
    /// </summary>
    private void RestartOnceStarted()
    {
        if (_restartQueued) return;
        _restartQueued = true;
        _ = StartAsync();
    }

    private void KeepOverride(SessionSettings change)
    {
        _overrides = _overrides.With(change with { Model = InstanceModel(change.Model) });
        _requestSave?.Invoke();
    }

    /// <summary>A model as the session spells it, turned into what the instance stores — the next launch
    /// qualifies it again, so a session's <c>provider/id</c> kept as it is would carry the provider twice.</summary>
    private string? InstanceModel(string? sessionModel) =>
        sessionModel is null
            ? null
            : Agent.InstanceModel(AgentRuntime.For(_settings.Settings, Instance, agent: Agent), sessionModel);

    /// <summary>The running session took a change: keep it, on the UI thread, where the layout is saved from.</summary>
    private void OnSettingsApplied(SessionSettings change) =>
        _post(() =>
        {
            if (!_disposed) KeepOverride(change);
        });

    /// <summary>
    /// Bypass asks first, as it does in Settings and on the Goal tile: it is the largest single grant, kept in
    /// the layout and so surviving every restart. No dialog means no, and a refusal puts the chooser back.
    /// </summary>
    private async Task ConfirmBypassThenChangeAsync(SessionOption mode)
    {
        var agreed = ConfirmAction is not null && await ConfirmAction(
            "Run this agent with no permission checks at all?\n\n" +
            "It will edit, create and delete files and run commands in this workspace without asking. " +
            "This applies to this tile, until you change it back.");

        if (agreed) await ChangeSettingsAsync(new SessionSettings(Mode: mode.Id));
        else if (_host is { } host) DrawSettings(host.State);
    }

    [RelayCommand]
    private Task ApplyModelAsync()
    {
        var typed = ModelText.Trim();
        return typed.Length == 0 || typed == Model ? Task.CompletedTask : ChangeSettingsAsync(new SessionSettings(Model: typed));
    }

    /// <summary>Puts back the model the session runs on, over a name typed and never confirmed.</summary>
    /// <remarks>Leaving the field is not a choice: what was typed is often only a filter for the list.</remarks>
    public void DiscardTypedModel() => ModelText = Model;

    /// <summary>How long the current turn has been going, beside the thinking dots — the Goal tile's clock.</summary>
    public ElapsedClock TurnClock { get; } = new();

    partial void OnIsWorkingChanged(bool value)
    {
        if (value) TurnClock.Start();
        else TurnClock.Stop();
    }

    partial void OnSelectedModeChanged(SessionOption? value)
    {
        if (_drawingSettings || value is null) return;
        _ = SessionSettingOptions.ParseMode(value.Id) == AiBehaviour.BypassPermissions
            ? RunAsync(() => ConfirmBypassThenChangeAsync(value))
            : ChangeSettingsAsync(new SessionSettings(Mode: value.Id));
    }

    partial void OnSelectedEffortChanged(SessionOption? value)
    {
        if (!_drawingSettings && value is not null) _ = ChangeSettingsAsync(new SessionSettings(Effort: value.Id));
    }

    /// <summary>A change the running agent could not take: start it again, on the UI thread, with the override.</summary>
    private void OnRestartRequested(SessionSettings change) =>
        _post(() =>
        {
            if (_disposed) return;
            KeepOverride(change);
            _ = StartAsync();
        });

    /// <summary>
    /// Points this conversation at another configured instance: the same agent on another account or model
    /// restarts the session and keeps everything, and another agent is only taken while nothing has been said.
    /// </summary>
    /// <remarks>
    /// <para><b>The agent is locked once the conversation has something in it</b> — t3code's rule and ours for
    /// the same reason: the resume token belongs to the CLI that issued it, and the stored conversation is
    /// that agent's. Such a switch is refused here rather than hidden, and the chooser says why.</para>
    /// <para>Switching agent onto a tile that already holds another agent's stored conversation starts nothing
    /// and says so (<see cref="StartAsync"/>), so the history is never thrown away behind a chooser: "Delete
    /// this conversation" is the one gesture that forgets.</para>
    /// </remarks>
    public async Task SwitchInstanceAsync(AiAgentInstance instance)
    {
        _latestPick = instance;
        await _switchGate.WaitAsync();
        try
        {
            // Picked again before this one had its turn: the later pick is the one the user meant.
            if (ReferenceEquals(_latestPick, instance)) await ApplySwitchAsync(instance);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    /// <summary>One switch, run alone: two overlapping ones would both pass the checks before either commits,
    /// and whichever finished last would win rather than whichever was picked last.</summary>
    private async Task ApplySwitchAsync(AiAgentInstance instance)
    {
        if (IsRunning(instance)) return;
        if (AiAgentCatalog.Find(instance.AgentId) is not { } agent) return;

        if (IsHeldByAnotherAgent(agent))
        {
            Chooser.RestoreSelection();
            return;
        }
        // Asked before anything is committed, and for another instance of the same agent too: a tile substituted
        // or still starting has not read the store yet, and a refused switch must not become the last-used
        // instance or the layout's, or the next tile and this one's next launch would open on an agent that
        // never ran here.
        if (await _binding.StoredAgentAsync(ConversationId) is { } stored && stored != agent.Id)
        {
            HoldStoredConversationOf(stored);
            Chooser.RestoreSelection();
            return;
        }
        if (!await ConfirmInterruptingTurnAsync(
                "Switch agent now? The agent is working, and restarting the session stops what it is doing."))
        {
            Chooser.RestoreSelection();
            return;
        }

        await UnderStartGateAsync(() => CommitSwitchAsync(instance, agent));
    }

    /// <summary>Takes the switch and replaces the host, under the start gate and with no await between the last
    /// check and the old host being let go.</summary>
    /// <remarks>Asked again here: the composer stays live while the store is read, the dialog is open and an
    /// earlier start holds the gate, and a message sent meanwhile binds the conversation to the agent it was sent
    /// to. Committed before the old host is gone, a message could still reach it and leave the tile, the last-used
    /// instance and the layout on an agent whose start then refuses that conversation.</remarks>
    private Task CommitSwitchAsync(AiAgentInstance instance, IAiAgent agent)
    {
        if (IsHeldByAnotherAgent(agent))
        {
            Chooser.RestoreSelection();
            return Task.CompletedTask;
        }

        TakeInstance(instance, agent, agent.Id != Agent.Id);
        return ReplaceHostAsync();
    }

    /// <summary>Moves the tile onto an instance: what a picked agent and a picked conversation both do, so the
    /// two cannot come to disagree about what a switch leaves behind.</summary>
    private void TakeInstance(AiAgentInstance instance, IAiAgent agent, bool otherAgent)
    {
        Instance = instance;
        Agent = agent;
        Substitution = null;
        _overrides = OverridesSurvivingSwitch(otherAgent);

        LastUsedAgentInstance.Remember(_settings, Instance);
        _requestSave?.Invoke();

        OnPropertyChanged(nameof(Instance));
        OnPropertyChanged(nameof(Agent));
        OnPropertyChanged(nameof(HeaderNote));
        Chooser.Draw();
    }

    /// <summary>What the chooser's overrides keep across a switch of instance.</summary>
    /// <remarks>The model never survives it: it is spelled for the provider behind the old instance, and another
    /// account does not serve it. Mode and effort are the application's own scale and stay with the same agent;
    /// another agent has never heard of any of them.</remarks>
    private SessionOverrides OverridesSurvivingSwitch(bool otherAgent) =>
        otherAgent ? SessionOverrides.None : _overrides with { Model = null };

    /// <summary>Whether something has been said in this conversation with an agent other than this one.</summary>
    private bool IsHeldByAnotherAgent(IAiAgent agent) => ConversationAgentId is { } held && agent.Id != held;

    /// <summary>Whether this instance is what actually runs here.</summary>
    /// <remarks>Not merely the tile's instance: a substitute onto another agent starts nothing, so picking it is
    /// how the user accepts it.</remarks>
    private bool IsRunning(AiAgentInstance instance) => instance.Id == Instance.Id && !RunsAnotherAgent;

    /// <summary>Whether something that replaces the session may end the turn in flight: restarting it ends the
    /// turn, so it asks the way Restart does. Nothing to interrupt is a yes; no dialog to ask in is a no.</summary>
    private async Task<bool> ConfirmInterruptingTurnAsync(string question) =>
        !IsWorking || (ConfirmAction is not null && await ConfirmAction(question));

    [RelayCommand]
    private Task InterruptAsync() =>
        _host is null ? Task.CompletedTask : RunAsync(() => _host.ExecuteAsync(new InterruptTurn(), _lifetime.Token));

    [RelayCommand]
    private Task RestartAsync() => StartAsync();

    /// <summary>Opens a new conversation beside this one, leaving it in the store.</summary>
    /// <remarks><b>It used to forget the old one</b>, and had to: a conversation was the tile's id, so the only
    /// way to have a new one in the same tile was to write over what was there. With a list to pick from, the old
    /// conversation is a row rather than a loss, so this asks nothing and destroys nothing — and forgetting is
    /// <see cref="DeleteConversationAsync"/>, which says out loud what it takes.</remarks>
    private Task NewConversationAsync() => UnderSwitchGateAsync(async () =>
    {
        if (!await ConfirmInterruptingTurnAsync(
                "Start a new conversation? The agent is working, and this stops what it is doing."))
            return;

        await MoveToConversationAsync(Guid.NewGuid().ToString());
    });

    /// <summary>Forgets this conversation and its checkpoints, and opens a new one.</summary>
    /// <remarks>
    /// <para><b>What is deleted is read before the question is asked</b>, and under the same gate a pick takes.
    /// A dialog is open for as long as somebody takes to answer it, and a conversation picked in that window
    /// becomes the open one — so a <c>ConversationId</c> read afterwards names the conversation the user has
    /// just asked to <i>open</i>, and that is what would be destroyed.</para>
    /// <para>The agent is the <b>stored</b> conversation's, not the one running: a tile refusing to start
    /// because it holds another agent's conversation is exactly the tile somebody deletes, and a host built on
    /// the wrong agent throws rather than forgetting anything.</para>
    /// </remarks>
    [RelayCommand]
    private Task DeleteConversationAsync() => UnderSwitchGateAsync(async () =>
    {
        var forgotten = ConversationId;
        if (IsShownByAnotherTile(forgotten)) return;
        var agentId = (await Task.Run(() => _store.Find(forgotten)))?.AgentId ?? Agent.Id;
        if (ConfirmAction is null || !await ConfirmAction(
                "Delete this conversation? It and its checkpoints will be forgotten, and this cannot be undone."))
            return;
        if (forgotten != ConversationId || IsShownByAnotherTile(forgotten)) return;

        // Held throughout: released on the move, it would be offered to another tile in the window where it is
        // still in the store, and its events and checkpoints would then be deleted under that tile's live host.
        await MoveToConversationAsync(Guid.NewGuid().ToString(), keepHolding: forgotten);
        // And forgotten only once the tile is off it, so nothing of ours is reading the events being deleted.
        await RunAsync(() => ForgetConversationAsync(forgotten, agentId));
        OpenConversations.Release(forgotten, _tileId());
        await Conversations.RefreshAsync();
    });

    /// <summary>Whether another tile holds this conversation — a tile refused it at start still names it, and
    /// deleting it from there would take the events and checkpoints out from under that tile's live host.</summary>
    /// <remarks>Asked again after the question, because the dialog is open long enough for another tile to
    /// take a conversation this one never held.</remarks>
    private bool IsShownByAnotherTile(string conversationId)
    {
        if (!OpenConversations.IsHeldByAnother(conversationId, _tileId())) return false;
        LaunchProblem = "Another tile is showing this conversation, so it cannot be deleted from here. " +
                        "Delete it from that tile, or close that tile first.";
        return true;
    }

    /// <summary>
    /// Points this tile at a conversation the user chose, switching agent with it where they differ.
    /// </summary>
    /// <remarks>
    /// <para><b>The agent comes with the conversation, not the other way round.</b> A conversation belongs to
    /// the agent that holds it — its resume token is that CLI's and its stored events are that agent's — so
    /// picking one held by another agent moves the tile onto an instance of that agent. A machine with no such
    /// instance refuses the pick rather than opening the transcript on a CLI that has never seen it, which is
    /// the same rule <see cref="ConversationAgentBinding"/> keeps from the other side.</para>
    /// <para>Serialized on the same gate as an agent switch: both replace the host, and two of them at once
    /// would leave whichever finished last in charge rather than whichever was picked last.</para>
    /// </remarks>
    public async Task SwitchConversationAsync(ConversationSummary summary)
    {
        if (summary.Id == ConversationId) return;
        // Picked again before this one had its turn: the later pick is the one the user meant, and without this
        // the one in between still has its agent spawned and torn down. The rule SwitchInstanceAsync keeps.
        _latestConversationPick = summary;
        await UnderSwitchGateAsync(async () =>
        {
            if (!ReferenceEquals(_latestConversationPick, summary)) return;
            if (RefusalFor(summary) is not null)
            {
                // Refused since the list was drawn — another tile took it, or its agent's instance went — so the
                // list is read again, which is what puts the row back dimmed with the sentence saying why.
                await Conversations.RefreshAsync();
                return;
            }

            if (!await ConfirmInterruptingTurnAsync(
                    "Open another conversation? The agent is working, and this stops what it is doing."))
            {
                Conversations.RestoreSelection();
                return;
            }

            if (summary.AgentId != Agent.Id)
            {
                if (InstanceOf(summary.AgentId) is not { } instance)
                {
                    Conversations.RestoreSelection();
                    return;
                }

                TakeInstance(instance, AiAgentCatalog.Find(summary.AgentId)!, otherAgent: true);
            }

            await MoveToConversationAsync(summary.Id);
        });
    }

    /// <summary>One thing that moves the tile off its conversation at a time.</summary>
    /// <remarks>Shared by picking a conversation, starting one and deleting one, because all three read
    /// <see cref="ConversationId"/> and then act on it across an await — a dialog, the store, the start gate —
    /// and two of them interleaved would each act on what the other had already moved.</remarks>
    private async Task UnderSwitchGateAsync(Func<Task> change)
    {
        await _switchGate.WaitAsync();
        try
        {
            await change();
        }
        finally
        {
            _switchGate.Release();
        }
    }

    /// <summary>Why a stored conversation cannot be opened here, or null.</summary>
    private string? RefusalFor(ConversationSummary summary)
    {
        if (summary.Id == ConversationId) return null;
        if (OpenConversations.IsHeldByAnother(summary.Id, _tileId()))
            return "Another tile is already showing this conversation.";
        if (summary.AgentId == Agent.Id || InstanceOf(summary.AgentId) is not null) return null;

        var name = AiAgentCatalog.Find(summary.AgentId)?.DisplayName ?? summary.AgentId;
        return $"This conversation is held with {name}, and there is no {name} instance available here. " +
               "A conversation is only ever continued by the agent that holds it.";
    }

    /// <summary>An instance of that agent this machine can actually run — the tile's own first, so a pick does
    /// not move a conversation onto a different account for no reason.</summary>
    private AiAgentInstance? InstanceOf(string agentId)
    {
        if (Instance.AgentId == agentId) return Instance;
        return _settings.Settings.AiAgentInstances.FirstOrDefault(instance =>
            instance.AgentId == agentId && AiAgentCatalog.IsAvailable(instance, _settings.Settings));
    }

    /// <summary>Takes the conversation and restarts on it, writing it into the layout so it comes back.</summary>
    private Task MoveToConversationAsync(string conversationId, string? keepHolding = null)
    {
        _conversationId = conversationId;
        _requestSave?.Invoke();
        OnPropertyChanged(nameof(ConversationId));
        return UnderStartGateAsync(async () =>
        {
            // Released only once the host of the old conversation has closed: released earlier, another tile could
            // open that conversation while this host is still writing to it. Released at all, because a tile going
            // on refusing a conversation it has left would make it unreachable for the rest of the session.
            await DisposeHostAsync();
            OpenConversations.ReleaseAllOf(_tileId(), keepHolding);
            await ReplaceHostAsync();
            await Conversations.RefreshAsync();
        });
    }

    /// <summary>
    /// One start at a time: two overlapping starts would each build a host, and the one overwritten
    /// would keep its agent process alive and write the same sequence numbers into the same conversation.
    /// </summary>
    private Task StartAsync() => UnderStartGateAsync(ReplaceHostAsync);

    private async Task UnderStartGateAsync(Func<Task> start)
    {
        await _startGate.WaitAsync();
        try
        {
            // Every start from here on reads the overrides as they are now, so a queued restart is covered.
            _restartQueued = false;
            await start();
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task ReplaceHostAsync()
    {
        await DisposeHostAsync();
        await OpenAndStartHostAsync();
    }

    private async Task DisposeHostAsync()
    {
        var previous = _host;
        _host = null;
        if (previous is not null)
        {
            previous.Changed -= OnChanged;
            previous.RestartRequested -= OnRestartRequested;
            previous.SettingsApplied -= OnSettingsApplied;
            await previous.DisposeAsync();
            // The next host numbers its own events, so a state kept from this one must not outrank them.
            lock (_drawGate) _waitingToDraw = null;
        }
    }

    private async Task OpenAndStartHostAsync()
    {
        if (_disposed) return;
        // Read once: a switch made while this start awaits queues a start of its own, and this one must go on
        // opening, preparing and launching the agent it began with rather than whichever was picked since.
        var agent = Agent;
        var instance = _overrides.ApplyTo(Instance);
        if (RunsAnotherAgent)
        {
            LaunchProblem = Substitution!.Notice;
            return;
        }

        // A tile with no leaf behind it has no id to be named after; it takes one and keeps it, so its
        // conversation survives a restart of the agent rather than being thrown away with each start.
        if (ConversationId.Length == 0) _conversationId = Guid.NewGuid().ToString();
        var conversationId = ConversationId;
        var holder = _tileId();
        // A conversation is one tile's at a time: two hosts of it number their events from what the store held
        // when each was built, so both write the same sequence numbers and the store keeps whichever landed last.
        if (holder.Length > 0 && !OpenConversations.TryHold(conversationId, holder))
        {
            LaunchProblem = "Another tile is already showing this conversation. " +
                            "Open a different one here, or close the tile that has it.";
            return;
        }

        IsStarting = true;
        LaunchProblem = null;
        try
        {
            // A host of this conversation closed by an earlier tile may still be writing its last events.
            await ConversationClosings.WhenClosedAsync(conversationId).WaitAsync(_lifetime.Token);
            if (_disposed) return;
            // Inside the try: a conversation store that cannot be opened is this tile's problem, said on it.
            if (await Task.Run(() => _store.Find(conversationId)) is { } stored && stored.AgentId != agent.Id)
            {
                // A record with nothing said in it is only the previous agent's start: it holds nobody.
                if (await _binding.HasSomethingSaidAsync(conversationId))
                {
                    HoldStoredConversationOf(stored.AgentId);
                    LaunchProblem = AnotherAgentsConversationNotice(stored.AgentId);
                    return;
                }
                await ForgetConversationAsync(conversationId, stored.AgentId);
            }
            HoldStoredConversationOf(null);

            if (await OpenHostAsync(conversationId, agent) is not { } host) return;
            var (launch, problem) = await _sessionStarter.PrepareAsync(_settings.Settings, agent, instance,
                _workingDirectory, conversationId, host.ResumeToken, _lifetime.Token);
            // Closed while preparing: Dispose has already ended this host, and nothing may start on it.
            if (_disposed) return;
            if (launch is null)
            {
                LaunchProblem = problem;
                return;
            }

            await host.StartAsync(sink => _sessionStarter.Create(agent, launch, sink), _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[AgentConversation] Starting {agent.Id} failed: {ex}");
            LaunchProblem = ex.Message;
        }
        finally
        {
            IsStarting = false;
        }
    }

    /// <summary>Records which other agent's stored conversation stopped this start, so the chooser offers that
    /// agent back instead of refusing it against the one that was picked.</summary>
    private void HoldStoredConversationOf(string? agentId)
    {
        _binding.HoldStoredConversationOf(agentId);
        OnPropertyChanged(nameof(IsBoundToItsAgent));
        Chooser.DrawIfBindingChanged();
    }

    private string AnotherAgentsConversationNotice(string storedAgentId) =>
        $"This tile holds a conversation with {AiAgentCatalog.Find(storedAgentId)?.DisplayName ?? storedAgentId}. " +
        "Switch the tile back to that agent to continue it, open another conversation, or delete this one.";

    /// <summary>Forgets a stored conversation — events and checkpoints — through a host of its own agent.</summary>
    private async Task ForgetConversationAsync(string conversationId, string agentId)
    {
        await using var host = await Task.Run(() => CreateHost(conversationId, agentId));
        await host.ForgetAsync(CancellationToken.None);
    }

    private AgentConversationHost CreateHost(string conversationId, string agentId) =>
        new(new ConversationRecord(conversationId, agentId, _workingDirectory, null, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
            _store,
            new GitTurnCheckpoints(_workingDirectory, GitService.ResolveGitPath(_settings.Settings.GitPath)));

    /// <summary>Opens the conversation, or answers null when the tile was closed while it was being read.</summary>
    /// <remarks>Built off the UI thread: opening reads and replays every stored event, and a long conversation
    /// is megabytes of JSON — a workspace of such tiles would otherwise freeze the window as it opens.</remarks>
    private async Task<AgentConversationHost?> OpenHostAsync(string conversationId, IAiAgent agent)
    {
        var host = await Task.Run(() => CreateHost(conversationId, agent.Id));
        if (_disposed)
        {
            ConversationClosings.Close(host);
            return null;
        }

        _host = host;
        _binding.Opened(agent.Id);
        host.Changed += OnChanged;
        host.RestartRequested += OnRestartRequested;
        host.SettingsApplied += OnSettingsApplied;
        Draw(host.State);
        // So the strip names the conversation rather than calling it empty until somebody opens the list: the
        // chooser is built before anything has been read, and what it holds until then is a placeholder row.
        _ = Conversations.RefreshAsync();
        return host;
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[AgentConversation] {ex}");
        }
    }

    private Task AnswerApprovalAsync(string requestId, ApprovalDecision decision) =>
        _host is null
            ? Task.CompletedTask
            : RunAsync(() => _host.ExecuteAsync(new RespondToApproval(requestId, decision), _lifetime.Token));

    private Task AnswerQuestionsAsync(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers) =>
        _host is null
            ? Task.CompletedTask
            : RunAsync(() => _host.ExecuteAsync(new AnswerQuestions(requestId, answers), _lifetime.Token));

    private Task<string> LoadDiffAsync(CheckpointEntry checkpoint, string? path) =>
        _host?.DiffAsync(checkpoint, path, _lifetime.Token) ?? Task.FromResult("");

    private async Task RestoreAsync(CheckpointEntry checkpoint)
    {
        if (_host is null || ConfirmAction is null) return;
        var files = checkpoint.Files.Count;
        if (!await ConfirmAction(
                $"Put the working tree back to how it was before this turn? {files} {(files == 1 ? "file" : "files")} " +
                "changed by it — and anything changed after it — will be reverted."))
            return;

        await RunAsync(() => _host.ExecuteAsync(new RestoreCheckpoint(checkpoint.BaseCheckpointId), _lifetime.Token));
    }

    private void OnChanged(ConversationState state, AgentEvent _)
    {
        lock (_drawGate)
        {
            // Two threads can raise this at once; the state numbered later wins whichever arrives last.
            if (_waitingToDraw is null || state.LastSequence >= _waitingToDraw.LastSequence) _waitingToDraw = state;
            if (_drawScheduled) return;
            _drawScheduled = true;
        }

        _post(() =>
        {
            ConversationState? latest;
            lock (_drawGate)
            {
                latest = _waitingToDraw;
                _drawScheduled = false;
            }

            if (!_disposed && latest is not null) Draw(latest);
        });
    }

    /// <summary>Brings every bound collection and property into step with one state.</summary>
    internal void Draw(ConversationState state)
    {
        TimelineSync.Sync(Timeline, state.Timeline, CreateItem);
        _binding.Drawn(state);
        SyncApprovals(state);

        if (PendingQuestions?.Round != state.PendingQuestions.FirstOrDefault())
            PendingQuestions = state.PendingQuestions.FirstOrDefault() is { } round
                ? new QuestionRoundViewModel(round, AnswerQuestionsAsync)
                : null;

        if (!ReferenceEquals(_drawnPlan, state.Plan))
        {
            _drawnPlan = state.Plan;
            PlanSteps.Clear();
            foreach (var step in state.Plan?.Steps ?? []) PlanSteps.Add(step);
            OnPropertyChanged(nameof(HasPlan));
        }

        IsWorking = state.IsWorking;
        Model = state.Model ?? "";
        DrawSettings(state);
        UsageText = UsageDisplay(state.Usage);
        StatusText = StatusOf(state);
        Activity = state.IsWaitingForUser
            ? TileActivity.Blocked
            : state.IsWorking
                ? TileActivity.Working
                : state.SessionState == AgentSessionState.Ready ? TileActivity.Idle : TileActivity.Unknown;

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsBoundToItsAgent));
        Chooser.DrawIfBindingChanged();
        TimelineChanged?.Invoke();
    }

    partial void OnModelChanged(string value)
    {
        OnPropertyChanged(nameof(HeaderNote));
        ModelText = value;
    }

    /// <summary>The choosers, in step with what the session offers and runs as — without that counting as a
    /// choice somebody made.</summary>
    private void DrawSettings(ConversationState state)
    {
        _drawingSettings = true;
        try
        {
            if (!ReferenceEquals(_drawnOptions, state.Options) && state.Options is { } options)
            {
                _drawnOptions = options;
                Replace(ModelOptions, options.Models.Select(m => m.Id));
                Replace(ModeOptions, options.Modes);
                Replace(EffortOptions, options.Efforts);
                OnPropertyChanged(nameof(HasModeOptions));
                OnPropertyChanged(nameof(HasEffortOptions));
            }

            SelectedMode = ModeOptions.FirstOrDefault(o => o.Id == state.Mode);
            SelectedEffort = EffortOptions.FirstOrDefault(o => o.Id == state.Effort);
        }
        finally
        {
            _drawingSettings = false;
        }
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items) collection.Add(item);
    }

    partial void OnLaunchProblemChanged(string? value)
    {
        OnPropertyChanged(nameof(HasLaunchProblem));
        OnPropertyChanged(nameof(IsEmpty));
        SendCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsStartingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        SendCommand.NotifyCanExecuteChanged();
    }

    private TimelineItemViewModel CreateItem(TimelineEntry entry) => entry switch
    {
        MessageEntry message => new MessageItemViewModel(message),
        WorkGroupEntry group => new WorkGroupItemViewModel(group),
        ProposedPlanEntry plan => new PlanProposalItemViewModel(plan),
        QuestionsEntry round => new QuestionsRecordItemViewModel(round),
        CheckpointEntry checkpoint => new CheckpointItemViewModel(checkpoint, LoadDiffAsync, RestoreAsync),
        NoticeEntry notice => new NoticeItemViewModel(notice),
        _ => new NoticeItemViewModel(new NoticeEntry(entry.Id, NoticeLevel.Info, entry.GetType().Name)),
    };

    private void SyncApprovals(ConversationState state)
    {
        var wanted = state.PendingApprovals.Select(a => a.RequestId).ToHashSet();
        for (var i = PendingApprovals.Count - 1; i >= 0; i--)
            if (!wanted.Contains(PendingApprovals[i].Request.RequestId))
                PendingApprovals.RemoveAt(i);

        foreach (var request in state.PendingApprovals)
            if (PendingApprovals.All(shown => shown.Request.RequestId != request.RequestId))
                PendingApprovals.Add(new ApprovalRequestViewModel(request, AnswerApprovalAsync));
    }

    /// <summary>"42.1k / 200k tokens · $0.31" — whatever of it the agent said.</summary>
    internal static string UsageDisplay(TokenUsage? usage)
    {
        if (usage is null) return "";
        var parts = new List<string>();
        if (usage.UsedTokens is { } used)
            parts.Add(usage.ContextWindow is { } window
                ? $"{Tokens(used)} / {Tokens(window)} tokens"
                : $"{Tokens(used)} tokens");
        if (usage.CostUsd is { } cost and > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"${cost:0.00}"));
        return string.Join(" · ", parts);
    }

    /// <summary>Invariant, like every other figure the application draws in its English interface.</summary>
    private static string Tokens(long count) => count switch
    {
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{count / 1_000_000d:0.#}M"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{count / 1_000d:0.#}k"),
        _ => count.ToString(CultureInfo.InvariantCulture),
    };

    private string StatusOf(ConversationState state) => state switch
    {
        { IsWaitingForUser: true } => "Waiting for you",
        { IsWorking: true } => "Working",
        { SessionState: AgentSessionState.Starting } => "Starting",
        { SessionState: AgentSessionState.Ready } => "Ready",
        { SessionState: AgentSessionState.Failed } => "Stopped with an error",
        _ => IsStarting ? "Starting" : "Not running",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        OpenConversations.ReleaseAllOf(_tileId());
        Chooser.Dispose();
        FileMentions.Dispose();
        TurnClock.Dispose();
        if (_host is { } host)
        {
            host.Changed -= OnChanged;
            host.RestartRequested -= OnRestartRequested;
            host.SettingsApplied -= OnSettingsApplied;
            ConversationClosings.Close(host);
        }
    }
}
