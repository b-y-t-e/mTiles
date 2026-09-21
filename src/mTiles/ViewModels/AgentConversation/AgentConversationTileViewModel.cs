using System.Collections.ObjectModel;
using mTiles.AgentSessions;
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
    /// <summary>Which conversation <see cref="_waitingToDraw"/> is of, so a state raised by the host
    /// being replaced is not drawn as the conversation being opened.</summary>
    private string? _waitingToDrawConversation;
    /// <summary>The handler the live host's <c>Changed</c> is subscribed with, which carries that host's
    /// own conversation id — the event itself names no sender, and <c>_conversationId</c> has already
    /// moved on by the time the outgoing host's last state is drawn.</summary>
    private Action<ConversationState, AgentEvent>? _hostChanged;
    private bool _drawScheduled;
    private PlanUpdated? _drawnPlan;
    private bool _disposed;
    private bool _startRequested;

    /// <summary>Which conversation the timeline on screen is of. See <see cref="TranscriptOpened"/>.</summary>
    private string? _drawnConversation;

    [ObservableProperty] private string _draft = "";

    /// <summary>Where the caret is in the composer, so an image or a file lands where the user is writing.</summary>
    [ObservableProperty] private int _draftCaretIndex;
    [ObservableProperty] private string? _launchProblem;
    [ObservableProperty] private bool _isStarting;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string _statusText = "";

    /// <summary>Which colour the status word takes — decided with the word, so the two cannot disagree.</summary>
    [ObservableProperty] private AgentStatusTone _statusTone = AgentStatusTone.Quiet;
    [NotifyPropertyChangedFor(nameof(HasUsageReading))]
    [NotifyPropertyChangedFor(nameof(ContextBarText))]
    [NotifyPropertyChangedFor(nameof(ShowsCompact))]
    [ObservableProperty] private string _usageText = "";

    /// <summary>Whether the agent has said anything about its context yet.</summary>
    public bool HasUsageReading => UsageText.Length > 0;

    /// <summary>What the foot of the tile reads, said even before the first figure arrives.</summary>
    /// <remarks><b>The bar is always there.</b> It used to appear with the first reading, so a tile that
    /// had not spoken yet was a composer with nothing under it and then, one message later, a row that
    /// pushed the whole conversation up. What it says while nothing is known says exactly that — the
    /// context is not known yet and nothing has been spent — rather than inventing a window or a
    /// percentage, which is the rule <see cref="ContextGauge"/> keeps for the bar itself.</remarks>
    public string ContextBarText => HasUsageReading ? UsageText : "context not known yet · $0.00";

    /// <summary>Whether Compact is drawn at all.</summary>
    /// <remarks>Drawn where the running agent has a route for it, and — before anything is known — drawn
    /// and <b>disabled</b>, so the row has the shape it will keep instead of growing a button when the
    /// first reading lands. It is never enabled here: <c>CanCompactNow</c> still decides that, and an
    /// agent with no route for compaction simply never becomes pressable.</remarks>
    public bool ShowsCompact => CanCompact || !HasUsageReading;

    /// <summary>What the agent is doing, beside the thinking dots. See <see cref="TurnStage"/>.</summary>
    [ObservableProperty] private string _turnStageText = "";

    /// <summary>How full the model's context is, or null when the agent did not say how big it is.</summary>
    /// <remarks>Null draws no bar rather than an empty one — see <see cref="ContextGauge"/>. The figures
    /// stand either way, which is why they are a separate property from this one.</remarks>
    [ObservableProperty] private double? _contextPercent;

    /// <summary>Whether the agent running now can be asked to compact its own context.</summary>
    /// <remarks>The session's answer (<see cref="SessionOptionsReported.CanCompact"/>), and not the
    /// agent's: three of the six have a route for it and three do not, and a control that is drawn and
    /// then says the agent cannot is worse than one that was never there. False while nothing is
    /// running, because the options survive the session that reported them.</remarks>
    [ObservableProperty] private bool _canCompact;
    [ObservableProperty] private QuestionRoundViewModel? _pendingQuestions;
    [ObservableProperty] private TileActivity _activity = TileActivity.Unknown;
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private SessionOption? _selectedMode;
    [ObservableProperty] private SessionOption? _selectedEffort;
    [ObservableProperty] private string? _composerNotice;

    /// <summary>
    /// What this tile is asking the user to do about the process it is running — the terminal agent tile's
    /// bar, by the same name and with the same two halves.
    /// </summary>
    /// <remarks>
    /// <para><b>Not a <c>NoticeRaised</c> in the conversation, and that was the bug.</b> A notice is a
    /// stored event (<c>AgentEvent.IsTransient</c> is false for it), so every skill change wrote another
    /// identical line into <c>conversations.db</c>: three databases ticked one after another while the
    /// agent worked left three copies of the same sentence, none of which went away after the restart it
    /// asked for, and all of which came back every time the conversation was opened. A request about the
    /// process running now is not part of what was said in the conversation.</para>
    /// <para>Whole lines through <see cref="LaunchNotices"/> for the reason the terminal agent tile uses
    /// it: a line is put up once however often the cause repeats, and whoever put one up takes its own
    /// line down without touching anybody else's.</para>
    /// </remarks>
    [NotifyPropertyChangedFor(nameof(HasLaunchNotice))]
    [ObservableProperty] private string _launchNotice = "";

    /// <summary>Whether anything is on the notice bar at all.</summary>
    public bool HasLaunchNotice => LaunchNotice.Length > 0;

    /// <summary>Puts the whole bar down — the user's answer to everything standing on it.</summary>
    [RelayCommand]
    private void DismissLaunchNotice() => LaunchNotice = "";

    private readonly Action? _requestSave;
    private SessionOverrides _overrides;
    private bool _restartQueued;

    /// <summary>The account this tile runs as was just picked, so the next start must not adopt the stored one.
    /// </summary>
    /// <remarks>A switch is answered by replacing the host, and the state that host replays still names the
    /// account the <i>previous</i> stretch ran as — adopted unconditionally, it puts the tile straight back on
    /// the account the user has just been asked about and agreed to leave, and the session starts on the old
    /// one with no seam ever drawn. Set where the switch is committed and held until the conversation records the
    /// picked account (<see cref="IsPickedAccountStillUnrecorded"/>) or the tile moves to another conversation
    /// — and kept in the layout (<see cref="AccountPickedButUnrecorded"/>), because a start that fails before
    /// the picked account is recorded, followed by closing the application, would otherwise have the next run
    /// adopt the old stretch and put the tile back on the account the user agreed to leave.
    /// </remarks>
    private bool _accountJustPicked;

    /// <summary>A conversation was just opened in this tile, so its next start restores the settings it ran on.
    /// </summary>
    /// <remarks>Only then, and never at every start: what a session reports is mostly the instance's own
    /// answer resolved, and adopted at each restart it would be pinned into the layout as an override nobody
    /// chose — freezing a model resolved from <c>AiModelChoice.FirstLoaded</c> and taking every later change
    /// in Settings away from this tile. A tile restored from its layout already carries its own overrides.
    /// </remarks>
    private bool _conversationJustOpened;

    /// <summary>The stored-login notice this tile put on its bar, so it can be taken down again, or null.</summary>
    private string? _storedLoginNotice;

    /// <summary>A skill change is waiting to be answered with a start of the agent.</summary>
    private bool _skillRestartWanted;

    /// <summary>Something is already waiting to answer it, so a second change adds no second start.</summary>
    private bool _skillRestartWaiting;

    /// <summary>A start of the agent is under way: the old process is going or the new one is coming up.
    /// </summary>
    /// <remarks>Held for the whole of <see cref="UnderStartGateAsync"/> rather than being read off
    /// <c>_host</c>, which is null from the first line of <see cref="ReplaceHostAsync"/> until the new
    /// session exists — seconds of it, since that window holds a CLI being disposed of and a model being
    /// resolved over the network. A skill change landing in there answered "nothing has started, so
    /// nothing has read it" about a process that was at that moment starting and reading exactly that
    /// directory, and the change was dropped without so much as a notice.</remarks>
    private bool _startUnderWay;
    private SessionOptionsReported? _drawnOptions;
    private bool _drawingSettings;
    private readonly ConversationAgentBinding _binding;

    public AgentConversationTileViewModel(string workingDirectory, SettingsService settings, IConversationStore store,
        AiAgentInstance instance, IAiAgent agent, Func<string> tileId, AgentSubstitution? substitution = null,
        SessionOverrides? overrides = null, Action? requestSave = null, Action<Action>? post = null,
        IAgentSessionStarter? sessionStarter = null, string? conversationId = null,
        WorkspaceAgentFiles? agentFiles = null, bool accountPicked = false)
    {
        _accountJustPicked = accountPicked;
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
        _contextWindow = new ContextWindowFollower(ContextWindowOfAsync, _post, RedrawAgainstTheWindow);
        FileMentions = new FileMentionsViewModel(new WorkspaceFileMentionSource(workingDirectory,
            settings.Settings.GitPath is { Length: > 0 } git ? git : "git"));
        _binding = new ConversationAgentBinding(store);
        Chooser = new AgentInstanceChooser(settings, () => Instance, IsRunning, () => ConversationAgentId, _post,
            instance => _ = RunAsync(() => SwitchInstanceAsync(instance)));
        Conversations = new ConversationChooser(store, workingDirectory, () => ConversationId, () => Agent.Id,
            RefusalFor, _post, summary => _ = RunAsync(() => SwitchConversationAsync(summary)));

        _agentFiles = agentFiles;
        if (_agentFiles is not null) _agentFiles.SkillsChanged += OnSkillsChanged;
    }

    private readonly WorkspaceAgentFiles? _agentFiles;

    /// <summary>
    /// The databases this workspace grants reached the agents' skills directories — react, or do not.
    /// </summary>
    /// <remarks>
    /// <para>Raised off whichever thread wrote the file, so the first thing it does is get onto the one
    /// this tile draws on: everything the answer depends on — whether a turn is in flight, what is in the
    /// composer — is this view model's, and this view model is the UI thread's.</para>
    /// <para>What to do is <see cref="SkillChangePolicy"/>'s, not this method's, so the reasoning about
    /// what a restart costs on pi, agy and Grok is argued once in a table test rather than buried in a
    /// handler. Here there is only the reading of the situation and the carrying out.</para>
    /// </remarks>
    private void OnSkillsChanged(string skill) => _post(() =>
    {
        if (_disposed) return;

        switch (ResponseToSkillChange())
        {
            case SkillChangeResponse.Restart:
                // Asked for, not performed: a run of clicks is one restart — see RestartWhenTheRunSettlesAsync.
                WantARestart();
                return;
            case SkillChangeResponse.Tell:
                // Onto the tile's own bar rather than into the transcript: a notice is a stored event, so
                // the transcript kept one line per tick of a database for the life of the conversation and
                // lost none of them to the restart that answered them. Added to whatever the bar already
                // says, and added once — see LaunchNotice.
                Tell();
                return;
            default:
                return;
        }
    });

    /// <summary>What this tile should do about the skills having moved, read as it stands now.</summary>
    private SkillChangeResponse ResponseToSkillChange() => SkillChangePolicy.For(
        agentFollowsIt: Agent.WatchesSkillsDirectory(AgentSurface.Structured),
        // The host, not the agent: it is opened before the process is started and survives a start that
        // never happened — a resolution that failed (LaunchProblem), a conversation another tile is
        // holding. In both of those nothing has read anything, so the policy's "nothing started, so
        // nothing read it" is exactly the answer, and asking `_host is not null` instead turned every
        // tick of a database into a restart that resolved the model again and came back with the same
        // problem. `HasSession` is the same question the terminal tile's `HasRunningSession` asks.
        // A start under way counts as running for the same reason: the process it is bringing up reads
        // the skills directory as it starts, and a change that lands while it does is one the new process
        // may already have missed — see _startUnderWay.
        isRunning: _host is { HasSession: true } || _startUnderWay,
        isBusy: IsWorking || PendingQuestions is not null || PendingApprovals.Count > 0,
        hasUnsentWork: Draft.Trim().Length > 0 || Attachments.HasItems);

    private void Tell() => LaunchNotice = LaunchNotices.With(LaunchNotice, SkillChangePolicy.Notice);

    /// <summary>
    /// Records that the agent wants starting again, and makes sure exactly one thing is waiting to do it.
    /// </summary>
    /// <remarks>Every database added and every RW toggle writes the skill again, so the changes arrive in
    /// runs; started on each, the tile tore the agent down and brought it up once per click, of which only
    /// the last was wanted. What is kept is a wish rather than a queue: however many changes land, the
    /// agent is started again once after them.</remarks>
    private void WantARestart()
    {
        _skillRestartWanted = true;
        if (_skillRestartWaiting) return;
        _skillRestartWaiting = true;
        _ = RunAsync(RestartWhenTheRunSettlesAsync);
    }

    /// <summary>
    /// Waits for the run of changes to stop, then starts the agent again — once, however many there were.
    /// </summary>
    /// <remarks>
    /// <para>Three things collapse into one start here. A change during the quiet window pushes the window
    /// out rather than adding a start. A change during the start itself is answered by one more lap — which
    /// is what <see cref="_startUnderWay"/> buys: the tile holds no host through most of a restart, and read
    /// off the host such a change answered "nothing has started" and was dropped, notice and all. And the
    /// situation is read
    /// <em>again</em> at the moment the start would happen: the window is long enough for the user to have
    /// sent a turn into the agent meanwhile, and a restart must never interrupt one — that change gets the
    /// notice it would have got had it arrived a second later.</para>
    /// <para>On the thread this tile draws on throughout: every flag here is this view model's, and the
    /// delay resumes on the dispatcher it was started from.</para>
    /// </remarks>
    private async Task RestartWhenTheRunSettlesAsync()
    {
        try
        {
            while (_skillRestartWanted)
            {
                _skillRestartWanted = false;
                await Task.Delay(SkillChangePolicy.QuietWindow, _lifetime.Token);
                if (_skillRestartWanted) continue;
                if (_disposed) return;

                switch (ResponseToSkillChange())
                {
                    case SkillChangeResponse.Restart:
                        await StartAsync();
                        break;
                    case SkillChangeResponse.Tell:
                        Tell();
                        break;
                }
            }
        }
        finally
        {
            _skillRestartWaiting = false;
        }
    }

    /// <summary>A process is about to be started in this tile, so the restart it was asked for has happened.
    /// </summary>
    /// <remarks>Without it the bar went on asking for a restart the tile had already performed — a request
    /// nobody can satisfy, which is how a notice bar stops being read. Only its own line comes down, so a
    /// sentence somebody else put up stays, and so does one the user has already dismissed.</remarks>
    private void OnSessionStarting() =>
        LaunchNotice = LaunchNotices.Without(LaunchNotice, SkillChangePolicy.Notice);

    /// <summary>The <c>@</c> file suggestions every box in this tile offers — the composer and an answer —
    /// the Goal tile's own, so a path is found the same way in both.</summary>
    public FileMentionsViewModel FileMentions { get; }

    /// <summary>What this tile runs differently from its instance — kept in the layout.</summary>
    public SessionOverrides Overrides => _overrides;

    /// <summary>An account was picked in this tile and no session of its conversation has recorded it yet —
    /// kept in the layout.</summary>
    public bool AccountPickedButUnrecorded => _accountJustPicked;

    /// <summary>Records whether a picked account still waits to be recorded, saving the layout when that moves.
    /// </summary>
    private void MarkAccountPicked(bool picked)
    {
        if (_accountJustPicked == picked) return;
        _accountJustPicked = picked;
        _requestSave?.Invoke();
    }

    /// <summary>The models the session offers; the model field also takes a name typed by hand.</summary>
    public ObservableCollection<string> ModelOptions { get; } = [];

    public ObservableCollection<SessionOption> ModeOptions { get; } = [];
    public ObservableCollection<SessionOption> EffortOptions { get; } = [];

    public bool HasModeOptions => ModeOptions.Count > 1;
    public bool HasEffortOptions => EffortOptions.Count > 1;

    /// <summary>Every image pasted since the last message went, whether or not the draft still names it —
    /// so a marker deleted by accident and brought back by an undo still names its picture.</summary>
    private readonly List<ComposerImage> _waitingImages = [];

    /// <summary>The images going with the next message: those whose markers <see cref="Draft"/> holds, in
    /// the order it holds them.</summary>
    public ComposerChips<ComposerImage> Attachments { get; } = new();

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

    /// <summary>Asked before something whose expected answer is yes. <b>Unwired answers yes.</b></summary>
    /// <remarks>A separate delegate rather than a flag on <see cref="ConfirmAction"/> because the two
    /// answer differently when there is no window to ask in, and one of them is a rule this application
    /// keeps everywhere: a question about throwing something away goes unanswered as <i>no</i>. This one
    /// takes nothing away — the transcript is ours and is untouched — so its unanswered value is yes, and
    /// the dialog it reaches opens with Yes under the keyboard. Keeping them apart is what stops a later
    /// caller reaching for the convenient default on a question where it is the wrong one.</remarks>
    public Func<string, Task<bool>>? ConfirmExpectingYes { get; set; }

    public string HeaderNote => Model.Length > 0 ? $"{Instance.Name} · {Model}" : Instance.Name;

    /// <summary>What the strip's model control says at rest.</summary>
    /// <remarks>An empty model is the ordinary state and not a failure: the session reports one only once it
    /// has started, and three of the agents never report one at all — they run on whatever their own
    /// configuration says. Drawn as an empty control that is what the strip looked like before this existed,
    /// a field with nothing in it and no sign that anything could go in it. The words are an invitation
    /// instead, which is what t3code's picker says in the same place.</remarks>
    public string ModelLabel => Model.Length > 0 ? Model : "Choose model";

    /// <summary>What the strip's permission control says at rest.</summary>
    /// <remarks>The label rather than the id, because the id is the CLI's spelling and the label is this
    /// application's own vocabulary (<c>AiBehaviours</c>) — the whole point of which is that one word means
    /// the same thing whichever of the six agents is running.</remarks>
    public string ModeLabel => SelectedMode?.Label ?? "Permissions";

    /// <summary>What the strip's effort control says at rest.</summary>
    public string EffortLabel => SelectedEffort?.Label ?? "Effort";

    public IReadOnlyList<TileAction> Actions =>
    [
        new(TileActionIds.Restart, "Restart agent", "restart", IsDestructive: true, PreferOverflow: true),
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
            case TileKey.Escape when IsWorking && CanInterrupt:
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
        // Enter in the composer, a paired phone and dictation all call this without asking CanExecute.
        if (!CanSend() || (string.IsNullOrWhiteSpace(Draft) && !Attachments.HasItems) || _host is not { } host) return;

        // A reader who was half way up the transcript has just said something, and the reply arrives at
        // the bottom: the view takes this as being asked for the end. Raised before the send rather than
        // after it, so the message's own arrival is already measured with the transcript following.
        SentByUser?.Invoke();
        HoldTheStopButton();
        var (text, images) = OutgoingMessage();
        // A host with no live agent refuses the message out loud, and the draft stays for the restart.
        if (host.HasSession)
        {
            _waitingImages.Clear();
            Draft = "";
            _binding.MessageSent();
            LastUsedAgentInstance.Remember(_settings, Instance);
        }

        await RunAsync(() => host.ExecuteAsync(new SendMessage(text, images), _lifetime.Token));
    }

    private bool CanSend() => !IsStarting && LaunchProblem is null;

    /// <summary>Raised when a whole transcript arrives at once, rather than a line at a time.</summary>
    /// <remarks>
    /// <para>Opening a conversation is being handed the end of it — what somebody wants to see is what
    /// was last said, the way every messaging window in the world opens. A transcript replayed out of
    /// the store arrives as one change to the timeline, and the anchor has no way to tell that from a
    /// reader who had scrolled: it keeps where they were, which for a conversation nobody has opened
    /// yet is the top.</para>
    /// <para>Raised on the conversation *changing*, which covers the tile's first draw, the picker, a
    /// new conversation and one adopted from the layout — rather than on each of those gestures, where
    /// the one that was forgotten is the one that would be found by somebody months later.</para>
    /// </remarks>
    public event Action? TranscriptOpened;

    /// <summary>Raised when the reader hands a message over, however they asked for it.</summary>
    /// <remarks>On the view model and not on each of the controls that can send: Enter, the button, a
    /// paired phone and a dictated sentence all arrive at <see cref="SendAsync"/>, and only there is it
    /// known that something was really sent.</remarks>
    public event Action? SentByUser;

    /// <summary>
    /// The draft as it is sent: markers renumbered from one in the order they are read, the images in that
    /// same order — which is what <see cref="AgentTurnInput"/> means by its list — and a marker naming no
    /// image taken out, since it would reach the agent as a picture that is not there.
    /// </summary>
    /// <remarks>An image whose marker is gone from the text stays behind: its chip is gone too, so sending
    /// it would be sending a picture the screen no longer shows.</remarks>
    internal (string Text, IReadOnlyList<ImageAttachment> Images) OutgoingMessage()
    {
        var named = Attachments.Items;
        var text = ImageMarkers.DropExcept(Draft, [.. named.Select(image => image.Index)]);
        var renumber = named.Select((image, position) => (image.Index, position)).ToDictionary(p => p.Index, p => p.position + 1);
        return (ImageMarkers.Renumber(text, renumber), [.. named.Select(image => image.Image)]);
    }

    /// <summary>Redraws both chip strips from the text: an image leaves when its marker does, a file when
    /// its mention does.</summary>
    partial void OnDraftChanged(string value)
    {
        Attachments.Show(ComposerImageChips.NamedIn(value, _waitingImages, image => image.Index));
        ComposerFiles.Show(FileScanner.In(value));
    }

    /// <summary>Names a file that is not a picture where the caret is — see <see cref="ComposerFileReference"/>.</summary>
    public async Task AttachFileAsync(string path)
    {
        var (mention, notice) = await ComposerFileReference.ForAsync(path, _workingDirectory);
        InsertIntoDraft(mention);
        ComposerNotice = notice;
    }

    private ComposerFileScanner? _fileScanner;

    private ComposerFileScanner FileScanner => _fileScanner ??= new ComposerFileScanner(_workingDirectory);

    /// <summary>The files the draft names, in the order it names them — see <see cref="ComposerFile"/>.</summary>
    public ComposerChips<ComposerFile> ComposerFiles { get; } = new();

    /// <summary>Takes a file's mention out of the draft, which takes its chip with it.</summary>
    [RelayCommand]
    private void RemoveComposerFile(ComposerFile file)
    {
        ApplyToDraft(DraftEdit.RemoveFile(file));
    }

    /// <summary>Puts text into the composer where the caret is — see <see cref="ComposerEdit.Insert"/>.</summary>
    public void InsertIntoDraft(string text) => ApplyToDraft(DraftEdit.Insert(text));

    private ComposerEdit DraftEdit => new(Draft, DraftCaretIndex);

    private void ApplyToDraft(ComposerEdit edit)
    {
        Draft = edit.Text;
        DraftCaretIndex = edit.Caret;
    }

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

        if (Attachments.Items.Count >= MaxImages)
        {
            ComposerNotice = $"At most {MaxImages} images go with one message.";
            return;
        }

        ComposerNotice = null;
        // Numbered past every image still waiting, never into a gap: a number is reused only once nothing
        // in the draft can still mean the image that had it.
        var composed = new ComposerImage(_waitingImages.Count == 0 ? 1 : _waitingImages.Max(a => a.Index) + 1, image);
        _waitingImages.Add(composed);
        InsertIntoDraft(composed.Marker);
    }

    /// <summary>Takes an image's marker out of the text, which takes its chip — and the image — out of the
    /// message.</summary>
    [RelayCommand]
    private void RemoveAttachment(ComposerImage image) => ApplyToDraft(DraftEdit.RemoveImage(image.Index));

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

    internal void KeepOverride(SessionSettings change)
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

    /// <summary>Runs this conversation on the named model, if it is not the one already running.</summary>
    /// <remarks>A listed model and one typed into the picker's search arrive here alike, so the two cannot
    /// come to mean different things.</remarks>
    public Task ApplyPickedModel(string? name)
    {
        var chosen = (name ?? string.Empty).Trim();
        return chosen.Length == 0 || chosen == Model
            ? Task.CompletedTask
            : ChangeSettingsAsync(new SessionSettings(Model: chosen));
    }

    /// <summary>How long the current turn has been going, beside the thinking dots — the Goal tile's clock.</summary>
    public ElapsedClock TurnClock { get; } = new();

    partial void OnIsWorkingChanged(bool value)
    {
        if (value) TurnClock.Start();
        else TurnClock.Stop();
        CompactCommand.NotifyCanExecuteChanged();
    }

    partial void OnCanCompactChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsCompact));
        CompactCommand.NotifyCanExecuteChanged();
    }

    partial void OnContextPercentChanged(double? value)
    {
        OnPropertyChanged(nameof(IsContextTight));
        OnPropertyChanged(nameof(CompactTip));
    }

    partial void OnSelectedModeChanged(SessionOption? value)
    {
        OnPropertyChanged(nameof(ModeLabel));
        if (_drawingSettings || value is null) return;
        _ = SessionSettingOptions.ParseMode(value.Id) == AiBehaviour.BypassPermissions
            ? RunAsync(() => ConfirmBypassThenChangeAsync(value))
            : ChangeSettingsAsync(new SessionSettings(Mode: value.Id));
    }

    partial void OnSelectedEffortChanged(SessionOption? value)
    {
        OnPropertyChanged(nameof(EffortLabel));
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
    /// restarts the session and keeps everything, and another agent is handed the work with a brief.
    /// </summary>
    /// <remarks>
    /// <para><b>The session is settled once the conversation has something in it, and the work is not</b> — the
    /// resume token belongs to the CLI that issued it and the stored conversation is that agent's, so no other
    /// agent can <i>continue</i> it. That was read for a long time as a refusal; the transcript is ours and the
    /// working tree is on disk, so picking another agent now asks (<see cref="ConfirmHandoverAsync"/>) and hands
    /// the work over, rather than sending the user off to type the state of it again into a fresh conversation.
    /// </para>
    /// <para>Which agent holds the conversation is asked of the store as well as of the screen, because a tile
    /// restored from a layout or substituted onto another agent has not read the store yet.</para>
    /// <para><b>A declined handover throws nothing away</b>: the conversation goes on belonging to whoever holds
    /// it, said out loud rather than left to be rediscovered, and "Delete this conversation" is still the one
    /// gesture that forgets. The refusal that remains is <see cref="RefusalFor"/>'s — an agent this machine
    /// cannot run at all has nothing to hand the work to.</para>
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

        // Asked before anything is committed, and of the store as well as of the screen: a tile substituted
        // or still starting has not read the store yet, and a switch that becomes the last-used instance or
        // the layout's would have the next tile and this one's next launch open on an agent that never ran
        // here. The drawn answer outranks it, since that is the agent whose host is writing now.
        var heldBy = ConversationAgentId ?? await _binding.StoredAgentAsync(ConversationId);
        var handingOver = heldBy is not null && heldBy != agent.Id;

        if (!await ConfirmInterruptingTurnAsync(handingOver
                ? "Hand the work over now? The agent is working, and this stops what it is doing."
                : "Switch agent now? The agent is working, and restarting the session stops what it is doing."))
        {
            Chooser.RestoreSelection();
            return;
        }

        if (handingOver && !await ConfirmHandoverAsync(instance, agent, heldBy!))
        {
            // Declined: the conversation goes on belonging to whoever holds it, said out loud rather than
            // left to be rediscovered — this is often the first time the tile has learnt it, since a tile
            // restored from a layout knows nothing until its start has read the store.
            HoldStoredConversationOf(heldBy);
            Chooser.RestoreSelection();
            return;
        }

        if (!await ConfirmLeavingTheAccountAsync(instance, agent))
        {
            Chooser.RestoreSelection();
            return;
        }

        await UnderStartGateAsync(() => CommitSwitchAsync(instance, agent, handingOver));
    }

    /// <summary>
    /// Asks before the work is handed to another agent, naming what travels and what does not.
    /// </summary>
    /// <remarks>
    /// <para><b>This used to be a refusal</b>, and the refusal was right about the mechanism and wrong about
    /// the user: a resume token belongs to the CLI that issued it, so no other agent can continue the
    /// session — but the transcript is ours, the working tree is on disk, and both survive. What the
    /// conversation could not do was <i>say</i> that, so the only routes out were a new conversation and
    /// typing the state of the work again.</para>
    /// <para><b>It names the loss before the gain</b>, because the gain is the part the user can see for
    /// themselves a moment later. The brief is written from what this application recorded, so the question
    /// can be answered without asking either CLI anything — which is what makes it available when the
    /// outgoing agent has already crashed, the case somebody switches in most often.</para>
    /// <para><b>The permission mode travels, and where it is bypass the question says so.</b> Carrying it is
    /// what the user asked for — a switch that silently dropped them back to the tool's own asking is a
    /// change of permissions nobody was told about — but bypass reached this way is a grant given for one
    /// agent arriving at another, so it is said out loud rather than inherited in silence.</para>
    /// <para><b>No dialog to ask in is a no here</b>, unlike a change of account on the same agent: that one
    /// loses nothing the transcript does not still hold, while this starts a different CLI in somebody's
    /// repository on a brief nobody has read.</para>
    /// <para>The refusal that remains is <see cref="RefusalFor"/>'s: an agent this machine cannot run at all
    /// has nothing to hand the work to.</para>
    /// </remarks>
    private async Task<bool> ConfirmHandoverAsync(AiAgentInstance instance, IAiAgent agent, string heldBy)
    {
        if (ConfirmAction is null) return false;

        var leaving = AiAgentCatalog.Find(heldBy)?.DisplayName ?? heldBy;
        var bypass = BehaviourNow == AiBehaviour.BypassPermissions
            ? $" It starts in bypass mode, as {leaving} was running, so it edits without asking."
            : "";

        return await ConfirmAction(
            $"Hand this work over to \"{instance.Name}\"? {leaving} cannot be resumed by {agent.DisplayName}, " +
            $"so {agent.DisplayName} starts cold and is given a written brief instead: what was asked for, " +
            "what was decided, the plan as it stands and which files have changed. The transcript here stays, " +
            $"and the permission mode and effort travel with the work.{bypass}");
    }

    /// <summary>
    /// Asks before a switch that lands on another login of the same agent.
    /// </summary>
    /// <remarks>
    /// <para><b>The loss is silent and comes after the fact.</b> The transcript is ours and is drawn whatever
    /// happens, but the resume token belongs to the CLI and lives in the account's own directory, so the same
    /// agent on a second subscription finds nothing to resume and starts cold — and the only thing that ever
    /// said so was a notice arriving once the new session had already begun. The terminal agent tile has asked
    /// this question from the start (<c>TerminalAgentTileViewModel.ConfirmationForSwitchTo</c>); this is the
    /// same question, in the one place it was missing.</para>
    /// <para>Only when the login actually moves: another model or another key on the same account resumes
    /// perfectly well, and a dialog in front of every pick is one nobody reads. <b>No dialog to ask in is a
    /// yes</b> — unlike a destructive action, nothing here is lost that the transcript does not still hold,
    /// and refusing would leave a tile with no way to change account at all.</para>
    /// </remarks>
    private async Task<bool> ConfirmLeavingTheAccountAsync(AiAgentInstance instance, IAiAgent agent)
    {
        var moving = agent.Id == Agent.Id && HoldsASessionToResume
            && !AccountOf(agent, instance).SharesLoginWith(AccountNow(agent));
        if (!moving || ConfirmAction is null) return true;

        return await ConfirmAction(
            $"Run this conversation as \"{instance.Name}\"? It is a different account, so {agent.DisplayName} " +
            "starts a new session — the transcript stays, what the model remembers does not.");
    }

    /// <summary>Whether the conversation holds a session the CLI could resume, which is what a change of login
    /// costs.</summary>
    /// <remarks>A conversation nothing has been said in yet has no token, so the switch loses nothing and the
    /// dialog's warning would be untrue — the case the terminal agent tile tells apart by
    /// <c>HoldsASessionIdOfItsOwn</c>.</remarks>
    private bool HoldsASessionToResume => _host?.ResumeToken is { Length: > 0 };

    /// <summary>Takes the switch and replaces the host, under the start gate and with no await between the last
    /// check and the old host being let go.</summary>
    /// <remarks>Asked again here: the composer stays live while the store is read, the dialog is open and an
    /// earlier start holds the gate, and a message sent meanwhile binds the conversation to the agent it was sent
    /// to. Committed before the old host is gone, a message could still reach it and leave the tile, the last-used
    /// instance and the layout on an agent whose start then refuses that conversation.</remarks>
    private async Task CommitSwitchAsync(AiAgentInstance instance, IAiAgent agent, bool handingOver)
    {
        if (!handingOver && IsHeldByAnotherAgent(agent))
        {
            Chooser.RestoreSelection();
            return;
        }

        // Folded before anything is torn down: the brief is written from what the outgoing host replayed,
        // and a host disposed first would leave nothing to write it from.
        var handedOver = handingOver ? await ConversationToHandOverAsync() : null;
        var brief = handedOver is null ? null : ConversationHandover.Write(handedOver);
        var from = handedOver?.Account;

        // The seam before the tile moves: taking the instance saves the layout, and a layout naming the new
        // agent over a conversation the store still says belongs to the old one is a tile that refuses to
        // start until somebody works out which of the two is lying.
        if (brief is not null && !await RecordHandoverAsync(from, AccountOf(agent, instance), brief))
        {
            // The seam is what makes the move true; without it the tile must stay exactly where it was, and
            // the picker with it, or it would show an agent that is not running here.
            Chooser.RestoreSelection();
            await ReplaceHostAsync();
            return;
        }

        TakeInstance(instance, agent,
            handingOver ? InstanceSwitch.HandingTheWorkOver : InstanceSwitch.SameConversation);
        MarkAccountPicked(true);
        await ReplaceHostAsync();
    }

    /// <summary>Writes the seam, between the two hosts, and keeps the brief for the session about to start.
    /// </summary>
    /// <remarks>
    /// <para><b>The old host goes first.</b> The seam is numbered off the store, so it must be written when
    /// nothing else is writing — and the next host refuses to open at all while the stored row still names
    /// the agent that is leaving, which is the check this is on the other side of.</para>
    /// <para>A conversation with no stored row is one no session ever started: there is nothing to hand over
    /// and nothing that would refuse the new agent, so the switch is an ordinary one.</para>
    /// <para><b>A write that fails is answered, not logged.</b> Everything here runs under
    /// <see cref="RunAsync"/>, which turns an exception into a line in the log: the tile would go on running
    /// the agent that is leaving while the picker showed the one arriving, and the user would be told
    /// nothing. So the failure comes back as an answer, the caller stays where it was, and the sentence goes
    /// on the tile's own bar — <c>HandoverWriter</c> puts the stored row back, so what is left behind is the
    /// conversation exactly as it was rather than a row and a transcript naming two different agents.</para>
    /// </remarks>
    /// <returns>Whether the seam was written and the conversation has moved.</returns>
    private async Task<bool> RecordHandoverAsync(SessionAccount? from, SessionAccount to, string brief)
    {
        await DisposeHostAsync();
        var conversationId = ConversationId;
        try
        {
            await Task.Run(() =>
            {
                if (_store.Find(conversationId) is { } record)
                    HandoverWriter.Write(_store, record, from, to, brief);
            });
            // A retry that works takes its own sentence down: left standing, the bar would go on saying the
            // work "stays with the agent it was already running" over a conversation that has just moved.
            LaunchNotice = LaunchNotices.Without(LaunchNotice, HandoverNotWrittenNotice);
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[AgentConversation] the handover could not be written: {ex}");
            LaunchNotice = LaunchNotices.With(LaunchNotice, HandoverNotWrittenNotice);
            return false;
        }
    }

    /// <summary>What the tile says when the seam could not be stored.</summary>
    internal const string HandoverNotWrittenNotice =
        "The work could not be handed over — this conversation's record could not be written, so it stays " +
        "with the agent it was already running. Try again in a moment.";

    /// <summary>The conversation the brief is folded out of.</summary>
    /// <remarks><b>The store answers where no host does.</b> A tile whose start was refused — its stored
    /// conversation belonging to another agent, which is exactly the state a handover is asked for in — a
    /// tile substituted onto another agent, and a tile restored from a layout all reach the switch with no
    /// host at all. Folded from an empty state, the brief would come out as the preamble alone: a handover
    /// recorded, a token cleared and an arriving agent told that there is work it is taking over and not one
    /// sentence of what it is.</remarks>
    private async Task<ConversationState> ConversationToHandOverAsync()
    {
        if (_host is { } host) return host.State;

        var conversationId = ConversationId;
        return await Task.Run(() => ConversationReducer.Replay(_store.ReadEvents(conversationId)));
    }

    /// <summary>Moves the tile onto an instance: what a picked agent and a picked conversation both do, so the
    /// two cannot come to disagree about what a switch leaves behind.</summary>
    private void TakeInstance(AiAgentInstance instance, IAiAgent agent,
        InstanceSwitch switching = InstanceSwitch.SameConversation)
    {
        // Worked out before the instance moves: what travels is what this tile was actually running, and half
        // of that is the outgoing instance's own answer.
        var surviving = OverridesSurvivingSwitch(switching);

        Instance = instance;
        Agent = agent;
        Substitution = null;
        _overrides = surviving;

        LastUsedAgentInstance.Remember(_settings, Instance);
        _requestSave?.Invoke();

        OnPropertyChanged(nameof(Instance));
        OnPropertyChanged(nameof(Agent));
        OnPropertyChanged(nameof(HeaderNote));
        Chooser.Draw();
    }

    /// <summary>Why the tile is moving onto another instance, which is what decides what its own picks are
    /// still about.</summary>
    /// <remarks><b>Three answers and not a flag</b>: opening somebody else's conversation and handing this
    /// one's work over both change the agent, and they want opposite things from the mode the user picked —
    /// one is a different piece of work, the other is this one continuing. Told apart by a bool, the second
    /// spoke for the first and a picked conversation quietly inherited a <c>bypass</c> nobody was asked
    /// about on it.</remarks>
    private enum InstanceSwitch
    {
        /// <summary>The conversation stays and the agent may too: another account, another model, or the
        /// account a stored conversation names.</summary>
        SameConversation,

        /// <summary>The tile is moving onto a conversation that is somebody else's work.</summary>
        AnotherConversation,

        /// <summary>This conversation's work is being handed to another agent.</summary>
        HandingTheWorkOver,
    }

    /// <summary>What the chooser's overrides keep across a switch of instance.</summary>
    /// <remarks>
    /// <para>The model never survives it: it is spelled for the provider behind the old instance, and another
    /// account does not serve it.</para>
    /// <para><b>Nothing survives onto another conversation.</b> A mode and an effort are answers about a piece
    /// of work, and the conversation being opened is a different one — with a mode of its own, which
    /// <see cref="AdoptStoredSettings"/> then restores. Carried over instead, a tile working in bypass would
    /// start the agent of every conversation the user merely looked at in bypass as well, with no dialog
    /// anywhere naming the grant.</para>
    /// <para><b>The mode and the effort survive even onto another agent</b>, which they did not before. They
    /// are this application's own canonical scale rather than any CLI's words, so they mean the same thing
    /// wherever they land, and the narrowing is already done where it belongs: every launch fits them to the
    /// arriving agent's own lists through <c>AiProcessRunner.Fit</c> — rounded down for a mode it lacks,
    /// to the nearest for an effort. Dropped here instead, a switch quietly put a user who was working in
    /// bypass back on the tool's own asking, which is a change of permissions nobody was told about.</para>
    /// <para><b>A handover carries the mode and the effort as values, not as the overrides that produced
    /// them.</b> An override is what the tile runs <i>differently from its instance</i>, so a tile whose
    /// picker was never touched has none at all and runs on the outgoing instance's own defaults — carried
    /// as overrides, that tile hands the arriving agent nothing and it starts on <i>its</i> instance's
    /// defaults instead. Both directions of that are a change of permissions nobody was told about, and one
    /// of them is a CLI silently starting in bypass. So what travels is <see cref="BehaviourNow"/> and
    /// <see cref="EffortNow"/> — what this tile is actually running, which is also what the confirmation
    /// names.</para>
    /// </remarks>
    private SessionOverrides OverridesSurvivingSwitch(InstanceSwitch switching) => switching switch
    {
        InstanceSwitch.HandingTheWorkOver => new SessionOverrides(Behaviour: BehaviourNow, Effort: EffortNow),
        InstanceSwitch.AnotherConversation => SessionOverrides.None,
        _ => _overrides with { Model = null },
    };

    /// <summary>The permission mode this tile is actually running in: its own pick, or its instance's answer
    /// where nobody has picked one.</summary>
    /// <remarks>The same reading <c>SessionOverrides.ApplyTo</c> makes at every launch, and the one a
    /// handover has to carry and say out loud — an unset override is not "no mode", it is the instance's.
    /// </remarks>
    private AiBehaviour BehaviourNow => _overrides.Behaviour ?? Instance.DefaultBehaviour;

    /// <inheritdoc cref="BehaviourNow"/>
    private AiEffort EffortNow => _overrides.Effort ?? Instance.DefaultEffort;

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

    /// <summary>Whether the agent can be stopped right now.</summary>
    /// <remarks>False for half a second after a message is sent. Send and Stop are one slot — the
    /// button becomes the other the moment the turn begins — so a second press landing where the first
    /// one did is a turn started and stopped before the agent has said a word, and nothing on screen
    /// explains what happened. The window is the double-click one and no longer: stopping is the thing
    /// somebody wants *urgently*, and a guard long enough to be felt is worse than the accident it
    /// prevents. Escape is gated by the same answer, since it reaches the same command.</remarks>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InterruptCommand))]
    private bool _canInterrupt = true;

    /// <summary>Keeps Stop from being pressed by the click that sent the message.</summary>
    internal void HoldTheStopButton()
    {
        CanInterrupt = false;
        var sending = ++_sendNumber;
        _ = Task.Delay(StopButtonHold).ContinueWith(_ => _post(() =>
        {
            // Only the send that armed it releases it: two messages in quick succession would
            // otherwise have the first one's timer unlock the button under the second.
            if (sending == _sendNumber) CanInterrupt = true;
        }), TaskScheduler.Default);
    }

    /// <summary>How long Stop is held after a send — the double-click window and nothing more.</summary>
    internal static readonly TimeSpan StopButtonHold = TimeSpan.FromMilliseconds(500);

    private int _sendNumber;

    [RelayCommand(CanExecute = nameof(CanInterrupt))]
    private Task InterruptAsync() =>
        _host is null ? Task.CompletedTask : RunAsync(() => _host.ExecuteAsync(new InterruptTurn(), _lifetime.Token));

    /// <summary>Asks the agent to summarise what has been said and carry on from the summary.</summary>
    /// <remarks>
    /// <para><b>It asks first, and the question opens on Yes</b>
    /// (<see cref="ConfirmExpectingYes"/>). What it protects against is not loss — nothing this
    /// application holds is touched, and the transcript is unchanged — but cost and surprise: compaction
    /// is a model call, on somebody's own budget, that then changes what the agent remembers for the rest
    /// of the conversation, and the control sits a few pixels from the composer everybody types in. So
    /// the question is a pause rather than an obstacle: Enter takes it, and it is the one confirmation in
    /// this application where the cautious answer is not the one under the keyboard.</para>
    /// <para>Asked before the command rather than inside the host, because the host is what a browser
    /// would drive too and a dialog is this window's business.</para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCompactNow))]
    private async Task CompactAsync()
    {
        if (_host is null) return;
        if (ConfirmExpectingYes is { } ask && !await ask(
                "Compact the context? The agent summarises what has been said so far and carries on " +
                "from the summary. The transcript here is not touched.")) return;

        // Asked and answered, and the tile may have moved on meanwhile — a turn can have started while
        // the dialog was open, and both agents that run this as a turn of their own refuse it then.
        if (!CanCompactNow() || _host is not { } host) return;
        await RunAsync(() => host.ExecuteAsync(new CompactContext(), _lifetime.Token));
    }

    private bool CanCompactNow() => CanCompact && !IsWorking && CanSend();

    /// <summary>Why the button is worth pressing, and — where it is urgent — why it is coloured.</summary>
    /// <remarks>The sentence always travels with the colour: a control drawn in <c>WarnText</c> with
    /// nothing saying what the warning is about is a mark the user has to guess the meaning of, which is
    /// the rule <c>TileAction.Urgency</c> set for the tile header.</remarks>
    public string CompactTip => IsContextTight
        ? "The context window is nearly full. Compact it: the agent summarises what has been said so far " +
          "and carries on from the summary. The transcript here is not touched."
        : "Compact the context: the agent summarises what has been said so far and carries on from the " +
          "summary. The transcript here is not touched.";

    /// <summary>Whether the window is full enough that compacting is the next thing to do.</summary>
    /// <remarks>80%, which is the margin <c>ModelContextWindow</c> already chose for the point at which
    /// Claude Code is told to compact on its own — one number for one idea, rather than this screen
    /// having an opinion of its own about when a window is nearly full.</remarks>
    public bool IsContextTight => ContextPercent >= 80;

    [RelayCommand]
    private Task RestartAsync() => StartAsync();

    /// <summary>Opens a new conversation beside this one, leaving it in the store.</summary>
    /// <remarks><para><b>It used to forget the old one</b>, and had to: a conversation was the tile's id, so the
    /// only way to have a new one in the same tile was to write over what was there. With a list to pick from, the
    /// old conversation is a row rather than a loss, so this destroys nothing — forgetting is
    /// <see cref="DeleteConversationAsync"/>, which says out loud what it takes.</para>
    /// <para><b>It still asks, every time.</b> It is a button on the strip now, beside the list, and to the eye
    /// a new conversation is a cleared screen: one misclick empties the transcript somebody was reading, and
    /// the way back is a search through the list. No dialog to ask in is a no.</para></remarks>
    [RelayCommand]
    private Task NewConversationAsync() => UnderSwitchGateAsync(async () =>
    {
        var question = IsWorking
            ? "Start a new conversation? The agent is working, and this stops what it is doing. " +
              "This one stays in the list of conversations."
            : "Start a new conversation? This one stays in the list of conversations.";
        if (ConfirmAction is null || !await ConfirmAction(question)) return;

        // Logged rather than thrown: bound straight to a button, a failure here would otherwise reach the
        // crash handler rather than the log, where the pick that used to start a conversation sent it.
        await RunAsync(() => MoveToConversationAsync(Guid.NewGuid().ToString()));
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

                TakeInstance(instance, AiAgentCatalog.Find(summary.AgentId)!, InstanceSwitch.AnotherConversation);
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
        // A switch belongs to the conversation it was made in; the one opened now is put back on its own account.
        MarkAccountPicked(false);
        _conversationJustOpened = true;
        WithdrawStoredLoginNotice();
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
        _startUnderWay = true;
        try
        {
            // Every start from here on reads the overrides as they are now, so a queued restart is covered.
            _restartQueued = false;
            await start();
        }
        finally
        {
            _startUnderWay = false;
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
            if (_hostChanged is not null) previous.Changed -= _hostChanged;
            _hostChanged = null;
            previous.RestartRequested -= OnRestartRequested;
            previous.SettingsApplied -= OnSettingsApplied;
            await previous.DisposeAsync();
            // The next host numbers its own events, so a state kept from this one must not outrank them.
            lock (_drawGate)
            {
                _waitingToDraw = null;
                _waitingToDrawConversation = null;
            }
        }
    }

    private async Task OpenAndStartHostAsync()
    {
        if (_disposed) return;
        // Read once: a switch made while this start awaits queues a start of its own, and this one must go on
        // opening, preparing and launching the agent it began with rather than whichever was picked since.
        var agent = Agent;
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
            // After the host has replayed and before anything is resolved from it: the conversation names the
            // account and the settings it last ran as, and a tile opening it on whichever instance of this agent
            // came first would authenticate as somebody else and run on that row's own mode and effort.
            AdoptStoredSession(host.State, agent, IsPickedAccountStillUnrecorded(host.State, agent),
                settingsToo: _conversationJustOpened);
            _conversationJustOpened = false;
            var instance = _overrides.ApplyTo(Instance);
            var (launch, problem) = await _sessionStarter.PrepareAsync(_settings.Settings, agent, instance,
                _workingDirectory, conversationId, host.ResumeToken, _lifetime.Token);
            // Closed while preparing: Dispose has already ended this host, and nothing may start on it.
            if (_disposed) return;
            if (launch is null)
            {
                LaunchProblem = problem;
                return;
            }

            // Read off the host, which has just replayed this conversation, rather than off the store a
            // second time: the same answer, without reading every event of a long conversation twice.
            launch = launch with
            {
                HasHistory = host.State.Timeline.OfType<MessageEntry>().Any(m => m.Role == MessageRole.User),
            };

            // The symmetric half of OnSkillsChanged, and the reason it is here rather than at the top of
            // this method: a start that never reached a process — a model that could not be resolved, a
            // conversation another tile is holding — read nothing off disk, so the request stands. From
            // here a process does read it, whether the user pressed restart or the policy did.
            OnSessionStarting();

            // The gauge's denominator, settled with the model this launch resolved. Asked for every
            // agent, unlike the windows the launch puts in Claude Code's environment, and not awaited:
            // the session must not wait on a bar, and the answer redraws the gauge when it comes.
            _contextWindow.Settle(launch.Model);

            await host.StartAsync(sink => _sessionStarter.Create(agent, launch, sink), AccountNow(agent),
                _lifetime.Token);
            await DeliverHandoverBriefAsync(host);
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

    /// <summary>
    /// Gives the session that has just started the brief the work was handed over with.
    /// </summary>
    /// <remarks>
    /// <para><b>Sent rather than recorded as a message.</b> The transcript already carries it, folded, on the
    /// handover entry; written a second time as something the user said, a page of Markdown they never typed
    /// would stand above the new agent's first answer as their own words.</para>
    /// <para><b>Kept when the start did not reach a live session.</b> A launch that failed told the agent
    /// nothing, so the brief is still owed and the next start delivers it — the alternative is a conversation
    /// whose transcript says the work was handed over to an agent that was never told anything about it. The
    /// debt is read back off the conversation (<see cref="ConversationHandover.BriefOwedIn"/>) rather than
    /// held on this tile, so it survives the tile being closed, the application being shut down and a send
    /// that threw — all of which a field would lose while the seam stayed in the store.</para>
    /// </remarks>
    private async Task DeliverHandoverBriefAsync(AgentConversationHost host)
    {
        if (ConversationHandover.BriefOwedIn(host.State) is not { Length: > 0 } brief) return;
        if (host.State.SessionState is not (AgentSessionState.Ready or AgentSessionState.Running
            or AgentSessionState.WaitingForUser)) return;

        await host.ExecuteAsync(new SendMessage(brief, null, Recorded: false), _lifetime.Token);
    }

    /// <summary>Whether an account the user picked has not yet been recorded by a session of this conversation.
    /// </summary>
    /// <remarks>Held until the conversation itself names the picked account, not until the first start after
    /// the switch: that start can refuse early or its session can die before reporting, and a flag spent on it
    /// would let the next start adopt the stretch before the switch — moving the tile back, in silence, onto
    /// the account the user agreed to leave.</remarks>
    private bool IsPickedAccountStillUnrecorded(ConversationState state, IAiAgent agent)
    {
        if (_accountJustPicked && state.Account is { } recorded && recorded.IsSameAs(AccountNow(agent)))
            MarkAccountPicked(false);
        return _accountJustPicked;
    }

    /// <summary>Who this tile is about to run as, for the host to stamp onto what the session reports.</summary>
    private SessionAccount AccountNow(IAiAgent agent) => AccountOf(agent, Instance);

    private static SessionAccount AccountOf(IAiAgent agent, AiAgentInstance instance) =>
        new(agent.Id, instance.Id, instance.Name, instance.SignInId is { Length: > 0 } signIn ? signIn : null);

    /// <summary>
    /// Puts the tile back on the account and the settings the conversation last ran as.
    /// </summary>
    /// <remarks>
    /// <para><b>The record names the agent and only the events name the account.</b> Without this a
    /// conversation reopened from the list took whichever instance of its agent <see cref="InstanceOf"/>
    /// found first — on a machine with two subscriptions that is a coin toss, and the losing side resumes
    /// nothing because the CLI keeps the session in the other account's directory.</para>
    /// <para><b>The model comes back through <see cref="InstanceModel"/> and never as it was reported.</b>
    /// What the session said is the model spelled that CLI's way — opencode and pi qualify it with their
    /// registry's provider name — so laid over the instance as it stands it would be qualified a second time
    /// at the next launch, into an id no provider has. That is the same round trip a model picked in the
    /// strip already takes (<see cref="KeepOverride"/>), which is why it is that method's helper and not a
    /// second rule here. Mode and effort are this application's own canonical ids and need no translation.
    /// </para>
    /// <para>Nothing here overrules what the user has already chosen in this tile: an override the layout
    /// carried, or one picked in the strip, is left exactly as it is.</para>
    /// <para><b>A picked account is not adopted away.</b> A switch replaces the host, and what that host
    /// replays still names the account the previous stretch ran as — taken as an instruction it would put the
    /// tile straight back on the account the user was just asked about and agreed to leave.</para>
    /// </remarks>
    /// <param name="accountWasJustPicked">The account is the user's own choice of a moment ago, so only the
    /// mode and effort of the stored session are worth restoring.</param>
    /// <param name="settingsToo">The conversation was just opened in this tile, so the model, mode and effort
    /// it ran on are restored as well; every other start leaves the tile's own overrides as they are.</param>
    internal void AdoptStoredSession(ConversationState state, IAiAgent agent, bool accountWasJustPicked = false,
        bool settingsToo = true)
    {
        WithdrawStoredLoginNotice();
        if (!accountWasJustPicked) AdoptStoredAccount(state.Account, agent);
        if (settingsToo) AdoptStoredSettings(state, agent);
    }

    /// <summary>Puts the tile back on the instance the conversation last ran as, or says the login moves.
    /// </summary>
    /// <remarks>What to do is <see cref="StoredSessionPolicy.DecideAccount"/>'s answer; this only carries it out.
    /// Said as a notice rather than asked: there is nothing to choose between, only something to know before
    /// the first message rather than from a seam drawn after it.</remarks>
    private void AdoptStoredAccount(SessionAccount? account, IAiAgent agent)
    {
        var runnable = _settings.Settings.AiAgentInstances.Where(row =>
            AiAgentCatalog.IsAvailable(row, _settings.Settings));
        var decision = StoredSessionPolicy.DecideAccount(account, AccountNow(agent), runnable);
        if (decision.InstanceToTake is { } stored) TakeInstance(stored, agent);
        if (decision.Notice is { } notice) SayStoredLoginMoves(notice);
    }

    /// <summary>Restores the model, mode and effort the conversation ran on, as far as
    /// <see cref="StoredSessionPolicy.SettingsToRestore"/> allows.</summary>
    /// <remarks>The model only where somebody picked it (<see cref="ConversationState.ChosenModel"/>, never the
    /// one the session reported running, which is the CLI's resolution of the instance's own answer) and only
    /// where the account it was spelled for is the one about to run: it is an id resolved against that account's
    /// provider, and laid over another it names a model nobody serves.
    /// </remarks>
    private void AdoptStoredSettings(ConversationState state, IAiAgent agent)
    {
        var modelStillFits = state.Account is null || state.Account.IsSameAs(AccountNow(agent));
        var stored = new SessionSettings(InstanceModel(state.ChosenModel), state.Mode, state.Effort);
        var restored = _overrides.With(
            StoredSessionPolicy.SettingsToRestore(stored, _overrides, Instance, modelStillFits));
        if (restored == _overrides) return;

        _overrides = restored;
        _requestSave?.Invoke();
        Chooser.Draw();
    }

    private void SayStoredLoginMoves(string notice)
    {
        _storedLoginNotice = notice;
        LaunchNotice = LaunchNotices.With(LaunchNotice, notice);
    }

    /// <summary>Takes down what <see cref="SayStoredLoginMoves"/> said, which is only ever true of the
    /// conversation and the start it was said for.</summary>
    /// <remarks>Asked at every adoption, so a start that finds the conversation already on the login it now
    /// runs as clears it, and on moving to another conversation, where it would describe one no longer on
    /// screen. Only this notice: the skills notice sharing the bar has its own reason to stay.</remarks>
    private void WithdrawStoredLoginNotice()
    {
        if (_storedLoginNotice is null) return;
        LaunchNotice = LaunchNotices.Without(LaunchNotice, _storedLoginNotice);
        _storedLoginNotice = null;
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
        _hostChanged = (state, _) => ScheduleDraw(state, host.ConversationId);
        host.Changed += _hostChanged;
        host.RestartRequested += OnRestartRequested;
        host.SettingsApplied += OnSettingsApplied;
        Draw(host.State, host.ConversationId);
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

    private Task<string> LoadDiffAsync(CheckpointEntry checkpoint, ChangedFile? file) =>
        _host?.DiffAsync(checkpoint, file, _lifetime.Token) ?? Task.FromResult("");

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

    /// <summary>Draws <paramref name="state"/> on the UI thread, folding every state raised before that
    /// draw runs into the latest of them.</summary>
    private void ScheduleDraw(ConversationState state, string? conversationId)
    {
        lock (_drawGate)
        {
            // Two threads can raise this at once; the state numbered later wins whichever arrives last.
            if (_waitingToDraw is null || state.LastSequence >= _waitingToDraw.LastSequence)
            {
                _waitingToDraw = state;
                _waitingToDrawConversation = conversationId;
            }

            if (_drawScheduled) return;
            _drawScheduled = true;
        }

        _post(() =>
        {
            ConversationState? latest;
            string? conversation;
            lock (_drawGate)
            {
                latest = _waitingToDraw;
                conversation = _waitingToDrawConversation;
                _drawScheduled = false;
            }

            if (!_disposed && latest is not null) Draw(latest, conversation);
        });
    }

    /// <summary>Brings every bound collection and property into step with one state of the conversation
    /// the tile is on.</summary>
    internal void Draw(ConversationState state) => Draw(state, ConversationId);

    /// <summary>Brings every bound collection and property into step with one state of
    /// <paramref name="conversationId"/>.</summary>
    /// <remarks>The conversation is named by the caller rather than read off <see cref="ConversationId"/>,
    /// which the picker moves on before the outgoing host has closed: a state that host raised meanwhile
    /// would otherwise be taken for the new conversation's first draw, and the transcript actually opened
    /// a moment later would arrive without <see cref="TranscriptOpened"/> — the reader left wherever the
    /// previous conversation had been scrolled to.</remarks>
    private void Draw(ConversationState state, string? conversationId)
    {
        var opened = _drawnConversation != conversationId;
        _drawnConversation = conversationId;

        TimelineSync.Sync(Timeline, state.Timeline, CreateItem);
        FollowTurn(state);
        MarkSeams(state);
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

        // After the timeline, so what the view is taken to the end of is this conversation and not the
        // last one still on screen.
        if (opened) TranscriptOpened?.Invoke();

        IsWorking = state.IsWorking;
        Model = state.Model ?? "";
        // The session that reported the options is the one that would do the compacting, so this goes down
        // with it: the options themselves survive a session ending, and a button offered over a stopped
        // agent is one that can only answer that nothing is running.
        CanCompact = state.Options?.CanCompact is true
                     && state.SessionState is AgentSessionState.Ready or AgentSessionState.Running
                         or AgentSessionState.WaitingForUser;
        DrawSettings(state);
        // The window the agent did not name, filled in the same way and in the same order as the terminal
        // agent tile's — see GaugeWindowSources. Claude Code's stream reports the tokens and never the limit, so
        // without this the commonest tile in the application counts against nothing and draws no bar.
        // The conversation reports the model it is actually running on, which on a subscription is the
        // only place that does — those instances carry no model, so the launch had nothing to ask the
        // account about — and after a model change mid-conversation the only place that is right.
        if (Instance.MaxContextTokens is null) _contextWindow.Follow(state.Model);
        var usage = WithAWindow(state.Usage);
        UsageText = UsageDisplay(usage);
        ContextPercent = ContextGauge.PercentUsed(usage);
        TurnStageText = TurnStage.For(state);
        (StatusText, StatusTone) = StatusOf(state);
        Activity = state.IsWaitingForUser
            ? TileActivity.Blocked
            : state.IsWorking
                ? TileActivity.Working
                : state.SessionState == AgentSessionState.Ready ? TileActivity.Idle : TileActivity.Unknown;

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsBoundToItsAgent));
        Chooser.DrawIfBindingChanged();
    }

    /// <summary>The work of the turn that is still going stays open; everything else is folded.</summary>
    /// <remarks>Only the conversation knows which turn is running, and a group cannot answer even
    /// whether it is busy: between two tools nothing is running, and a tool fast enough to start and
    /// finish between two draws never runs at all as far as any one draw can see. Which group is the
    /// live turn's is <see cref="LiveTurnWork"/>.</remarks>
    private void FollowTurn(ConversationState state)
    {
        foreach (var group in Timeline.OfType<WorkGroupItemViewModel>())
            group.FollowTurn(LiveTurnWork.IsLive(group.Source as WorkGroupEntry, state));
    }

    /// <summary>
    /// Writes the rule above every entry where the work moved to another agent or another login — never for another instance on the same login, which the switch does not ask about either.
    /// </summary>
    /// <remarks>
    /// <para><b>The seam is said rather than hidden.</b> A conversation drawn as one unbroken column across
    /// a change of account claims a continuity the model does not have: the transcript is ours and survives,
    /// the CLI's memory of it does not. So the entry that begins a new stretch carries the account's name on
    /// a rule, and everything above it stays legible as somebody else's work.</para>
    /// <para>Only from the <i>second</i> account onwards, and only where the account is actually named: a
    /// conversation that never moved carries no rule, and one recorded before any of this was stamped reads
    /// exactly as it always did rather than growing a seam at the first entry that knows who it was.</para>
    /// </remarks>
    private void MarkSeams(ConversationState state)
    {
        SessionAccount? running = null;
        for (var i = 0; i < state.Timeline.Count && i < Timeline.Count; i++)
        {
            var account = state.Timeline[i].Account;
            var moved = account is not null && running is not null && !account.SharesLoginWith(running);
            Timeline[i].Seam = moved ? StoredSessionPolicy.AccountLabel(account!) : null;
            if (account is not null) running = account;
        }
    }

    partial void OnModelChanged(string value)
    {
        OnPropertyChanged(nameof(HeaderNote));
        OnPropertyChanged(nameof(ModelLabel));
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
        CompactCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsStartingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        SendCommand.NotifyCanExecuteChanged();
        CompactCommand.NotifyCanExecuteChanged();
    }

    private TimelineItemViewModel CreateItem(TimelineEntry entry) => entry switch
    {
        MessageEntry message => new MessageItemViewModel(message),
        WorkGroupEntry group => new WorkGroupItemViewModel(group),
        ProposedPlanEntry plan => new PlanProposalItemViewModel(plan),
        QuestionsEntry round => new QuestionsRecordItemViewModel(round),
        CheckpointEntry checkpoint => new CheckpointItemViewModel(checkpoint, LoadDiffAsync, RestoreAsync),
        NoticeEntry notice => new NoticeItemViewModel(notice),
        HandoverEntry handover => new HandoverItemViewModel(handover),
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

    /// <summary>The agent's reading with a context window put under it, where it named none.</summary>
    /// <remarks>
    /// <para><b>Only codex and ACP name their own</b> (<c>modelContextWindow</c>, <c>size</c>). Claude
    /// Code's stream reports the tokens and nothing else, so the bar this tile is built around was drawn
    /// for two agents out of six — and never for the commonest configuration there is.</para>
    /// <para>The order is the terminal agent tile's, because it is the same question: the provider's
    /// figure is a fact about what is being served and this application hands it to the CLI anyway, and
    /// with no provider — a subscription has no catalogue to ask — what is left is what the CLI itself
    /// believes, which is where the run will stop.</para>
    /// <para>Applied here rather than in the reducer: the conversation's events are what the agent
    /// <em>said</em>, and a window nobody named is not something to write into them.</para>
    /// </remarks>
    private TokenUsage? WithAWindow(TokenUsage? usage)
    {
        if (usage is null || usage.ContextWindow is not null) return usage;
        if (GaugeWindow is not { } window) return usage;

        return usage with { ContextWindow = window };
    }

    /// <summary>The window of the model this conversation runs on now, settled when the session starts
    /// and followed when the model changes.</summary>
    private readonly ContextWindowFollower _contextWindow;

    /// <summary>How large a context <paramref name="model"/> is served with — Claude Code's stream never
    /// names a window itself.</summary>
    private Task<long?> ContextWindowOfAsync(string model, CancellationToken ct) =>
        GaugeWindowSources.LookupAsync(_settings.Settings, Agent, Instance, model, ct);

    /// <summary>Redraws against the state already on screen: the figures have not changed, only what
    /// they are being counted against.</summary>
    /// <remarks>Through the same scheduling every other change goes through, so nothing here has to know
    /// which properties a redraw touches.</remarks>
    private void RedrawAgainstTheWindow()
    {
        if (!_disposed && _host is { } live) ScheduleDraw(live.State, live.ConversationId);
    }

    /// <summary>What to count this conversation's tokens against, when the agent did not say — in the
    /// terminal agent tile's order, because it is the same question (<see cref="GaugeWindowSources"/>).
    /// </summary>
    private long? GaugeWindow => GaugeWindowSources.For(Instance, _contextWindow);

    /// <summary>"42.1k / 200k tokens · $0.31" — whatever of it the agent said.</summary>
    /// <remarks>The wording is <see cref="ContextGaugeViewModel"/>'s, because the terminal agent tile
    /// draws the same bar from a different source and two spellings of one figure is how a reader comes
    /// to think they are two different figures.</remarks>
    internal static string UsageDisplay(TokenUsage? usage) => ContextGaugeViewModel.Describe(usage);

    /// <summary>The status word and its colour, from one table, so a state added or reordered here moves both.</summary>
    private (string Text, AgentStatusTone Tone) StatusOf(ConversationState state) => state switch
    {
        { IsWaitingForUser: true } => ("Waiting for you", AgentStatusTone.Waiting),
        { IsWorking: true } => ("Working", AgentStatusTone.Working),
        { SessionState: AgentSessionState.Starting } => ("Starting", AgentStatusTone.Quiet),
        { SessionState: AgentSessionState.Ready } => ("Ready", AgentStatusTone.Ready),
        { SessionState: AgentSessionState.Failed } => ("Stopped with an error", AgentStatusTone.Failed),
        _ => (IsStarting ? "Starting" : "Not running", AgentStatusTone.Quiet),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_agentFiles is not null) _agentFiles.SkillsChanged -= OnSkillsChanged;
        OpenConversations.ReleaseAllOf(_tileId());
        Chooser.Dispose();
        FileMentions.Dispose();
        TurnClock.Dispose();
        if (_host is { } host)
        {
            if (_hostChanged is not null) host.Changed -= _hostChanged;
            host.RestartRequested -= OnRestartRequested;
            host.SettingsApplied -= OnSettingsApplied;
            ConversationClosings.Close(host);
        }
    }
}
