using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Providers;

namespace mTiles.ViewModels;

/// <summary>
/// The Goal tile.
/// </summary>
/// <remarks>
/// <para><b>Threading, resolved in one direction: this class runs on the UI thread.</b> Every workflow
/// path starts in a command, and the awaits inside it resume on Avalonia's synchronisation context, so
/// <see cref="Messages"/>, <see cref="Badges"/>, <c>InputText</c> and the rest are touched from one
/// thread and need no guard. The guards that do exist are not hedging — they mark the <em>three</em>
/// places something arrives from elsewhere, and there are only three:</para>
/// <list type="bullet">
/// <item>the debounce timer, which fires <c>SaveState</c> on a pool thread;</item>
/// <item><c>RediscoverAgentsAsync</c>, which walks PATH on a pool thread and marshals back before
/// touching <c>AvailableTools</c>;</item>
/// <item><c>RefreshDetectAvailability</c>, which asks git in the background and marshals back before
/// setting <c>HasUncommittedChanges</c>.</item>
/// </list>
/// <para>Anything reachable from those three checks the dispatcher; nothing else does, and nothing else
/// should start. Sprinkling the check more widely would be worse than useless — it would suggest the
/// workflow can be driven from anywhere, which is exactly the belief that makes a race look acceptable.
/// </para>
/// </remarks>
public partial class GoalTileViewModel
    : ObservableObject, IBusyTile, ITileActions, IProcessTile, IActivatableTile, IMaximizableTile
{
    /// <inheritdoc />
    public string KindId => TileKindIds.Goal;

    /// <summary>The AI tool this tile has running, if any.</summary>
    /// <remarks>
    /// <para>The tool is usually the heaviest thing in a workspace - heavier than the shell beside it -
    /// so a memory reading that skipped it would be lowest exactly where the user most needs it to be
    /// right. What it starts in turn hangs off this id and is found by whoever walks the process table.</para>
    /// <para>Zero rather than null in the field so the write is one atomic instruction: it is set on the
    /// thread that starts the run and read by the sampler, and an id kept past the run is a number the
    /// system is free to have handed to somebody else.</para>
    /// </remarks>
    public int? ChildProcessId => Volatile.Read(ref _childProcessId) is var id && id != 0 ? id : null;

    private int _childProcessId;

    /// <summary>The ids of the three things this tile offers outside its own view.</summary>
    public const string ContinueActionId = "continue";
    public const string PauseActionId = "pause";
    public const string CommitActionId = "commit";

    /// <summary>What this tile offers its header and a paired phone.</summary>
    /// <remarks>
    /// The three a run is driven by between attempts, which is exactly the case this exists for: a goal
    /// left working while the user is elsewhere. Nothing that starts a new goal or throws one away —
    /// those need the transcript on screen to mean anything.
    /// </remarks>
    public IReadOnlyList<TileAction> Actions =>
    [
        new(ContinueActionId, "Continue", "play", IsEnabled: CanContinue),
        new(PauseActionId, "Pause", "pause", IsEnabled: IsRunning && !IsPaused),
        new(CommitActionId, "Commit work", "check", IsEnabled: CanCommit),
    ];

    /// <inheritdoc />
    public async Task<TileActionResult> InvokeAsync(string id)
    {
        // Asked again rather than trusting the snapshot the caller acted on: a run moves through its
        // phases on its own, so a list a phone is holding is as old as the last state it was told about.
        if (Actions.FirstOrDefault(a => a.Id == id) is not { } action)
            return TileActionResult.Refused($"This tile has no '{id}'.");

        if (!action.IsEnabled)
            return TileActionResult.Refused($"{action.Label} is not available right now.");

        switch (id)
        {
            case ContinueActionId: await ContinueRun(); break;
            case PauseActionId: Pause(); break;
            case CommitActionId: await CommitWork(); break;
        }

        return TileActionResult.Ok;
    }

    private readonly string _workingDirectory;
    private readonly SettingsService _settingsService;
    private readonly GoalWorkflowEngine _engine = new();
    private readonly GoalStateStore _store;
    private readonly string _filePath;

    private CancellationTokenSource? _cts;

    /// <summary>
    /// Cancelled when the tile closes. The run's own token belongs to the run and is null between runs,
    /// so the background checks that outlive neither — the detect-availability probe — have nothing
    /// else to hang off, and a closed tile was still starting git processes.
    /// <para>Cancelled and <b>not disposed</b>, exactly as <c>_cts</c> is: the workflow keeps unwinding
    /// after the tile closes and its <c>finally</c> asks for another availability check, so the token
    /// is still being read afterwards — and reading <c>Token</c> on a disposed source throws. A
    /// cancelled source answers that question perfectly well for as long as anyone is still asking.
    /// </para>
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Set at the start of <see cref="Dispose"/>. The workflow keeps unwinding after the
    /// tile is closed and the store must not be asked for anything more.</summary>
    private bool _disposed;

    /// <summary>The agents this machine can run, as of the last scan. Rebuilt rather than kept in step:
    /// the scan itself is cached for half a minute in <c>AiAgentCatalog.Locate</c>.</summary>
    private IReadOnlyList<GoalAgentChoice> _availableAgents = [];

    [ObservableProperty] private string _inputText = "";

    /// <summary>
    /// Where the caret is in the composer, kept in step by the view's own two-way binding.
    /// </summary>
    /// <remarks>
    /// Here only so that a pasted image's marker lands where the user was typing rather than at the
    /// end of whatever they had written — the one thing this tile does to the composer's text that the
    /// user did not type themselves. A tile with no view leaves it at zero, which puts the marker at
    /// the front of an empty box, and that is the same place either way.
    /// </remarks>
    [ObservableProperty] private int _inputCaretIndex;

    [ObservableProperty] private GoalPhase _currentPhase = GoalPhase.Goal;
    [ObservableProperty] private bool _isRunning;

    private string _executionAgentInstanceId = "";
    private string _reviewAgentInstanceId = "";

    /// <summary>Which configured agent carries the goal out. An <c>AiAgentInstance.Id</c>, not a
    /// name.</summary>
    /// <remarks><b>A null is normalised to empty, and that is not defensive housekeeping.</b> Both this
    /// and <see cref="ReviewAgentInstanceId"/> are the target of a two-way <c>SelectedValue</c> binding,
    /// and clearing a combo box's <c>ItemsSource</c> — which <see cref="DetectAgents"/> does every time
    /// it looks again — drops the selection and makes the binding write <c>null</c> back here. A
    /// property initialiser is not a runtime contract, so the generated setter took it, the change
    /// notification re-read <see cref="AvailablePermissionModes"/> while the list was still empty, and
    /// <c>GoalAgents.WithId</c> dereferenced it. Hand-written for exactly that reason: this is the same
    /// rule the settings file keeps in its own setters, against a null arriving from the view instead
    /// of from the file.</remarks>
    public string ExecutionAgentInstanceId
    {
        get => _executionAgentInstanceId;
        set
        {
            var id = value ?? "";
            if (id == _executionAgentInstanceId) return;

            _executionAgentInstanceId = id;
            OnPropertyChanged();
            OnExecutionAgentInstanceIdChanged(id);
        }
    }

    /// <summary>Which configured agent reviews it, or empty for the one that did the work.</summary>
    /// <remarks>Empty is the default and a real answer, which is why the chooser spells it out rather
    /// than leaving a blank row: "the same agent" is what a goal does unless somebody asks for a second
    /// opinion. Null is normalised to it for the reason <see cref="ExecutionAgentInstanceId"/>
    /// gives.</remarks>
    public string ReviewAgentInstanceId
    {
        get => _reviewAgentInstanceId;
        set
        {
            var id = value ?? "";
            if (id == _reviewAgentInstanceId) return;

            _reviewAgentInstanceId = id;
            OnPropertyChanged();
            OnReviewAgentInstanceIdChanged(id);
        }
    }

    /// <summary>What the strip says, which for every idle state is nothing — see
    /// <c>GoalWorkflowEngine.GetPhaseLabel</c>. Empty is the correct initial value for the same reason
    /// it is the correct steady one: a tile that has just been built is waiting for a goal, and the
    /// composer under it says so.</summary>
    [ObservableProperty] private string _phaseLabel = "";
    [ObservableProperty] private bool _isPaused;

    /// <summary>The tile is working exactly while a run is in flight — no window and no smoothing,
    /// because unlike a terminal's output this is already the fact itself rather than a symptom of it.
    /// </summary>
    public bool IsBusy => IsRunning;

    /// <summary>Whether the completion-criteria panel is open. View state only — a panel left open is
    /// not something a restart should have to remember.</summary>
    [ObservableProperty] private bool _showCriteria;

    /// <summary>Whether this workspace has uncommitted changes to work a goal out of. Re-read whenever
    /// the tile goes idle, because the user is editing in the terminal tiles beside this one.</summary>
    [ObservableProperty] private bool _hasUncommittedChanges;

    /// <summary>
    /// Whether the two detect buttons are offered at all.
    /// <para>Only where a goal is what is wanted next — a tile waiting for one, or a finished one — and
    /// only when there is something to read. A button that reads the working tree has nothing to say
    /// about a clean one, and offering it there is offering a run that ends in "there are no changes".
    /// </para>
    /// </summary>
    public bool CanDetectGoal =>
        HasUncommittedChanges && !IsRunning && CurrentPhase is GoalPhase.Goal or GoalPhase.Summary;

    /// <summary>
    /// Whether the finished run can be carried on.
    /// </summary>
    /// <remarks>
    /// <para>One of the four stops. <b>BudgetSpent</b> is the plain one: the attempts ran out and more
    /// of them is exactly what is missing.</para>
    /// <para><b>NoChange is offered too, and used not to be.</b> The argument against it was that the
    /// tool wrote nothing, so another attempt would write nothing again, and neither of the two paths
    /// that reach this stop bears it out. Where the attempt was <em>refused</em>, the summary itself
    /// says to change the permission mode and try again — and this button is that retry, keeping the
    /// transcript instead of asking for the goal to be retyped. Where it was not,
    /// <see cref="ReviewUnchangedTreeAsync"/> reviews the unchanged tree before the summary is written
    /// and its findings go into the next implement prompt, so the next attempt is handed something this
    /// one was not. It is also the stop that most often arrives with the budget unspent — an empty
    /// attempt ends the loop whatever is left in it — so refusing here left a run with an unmet
    /// criterion, attempts still owed and no way at all to spend them. Whether the tool disagreed with
    /// the finding or simply missed it is not something this application can tell, and one button is a
    /// cheaper way to find out than starting again.</para>
    /// <para>The other two are not budgets and never will be: a met goal has nothing to continue
    /// towards, and a no-progress stop has just established that two reviews in a row found exactly
    /// the same things. Offering Continue there would sell AI runs whose outcome is already known.
    /// </para>
    /// <para>The ceiling is asked about too: <see cref="GoalCompletionPolicy.Attempts"/> clamps, so
    /// raising a budget that is already at the top would run the loop round to no next attempt and
    /// summarise again, having spent nothing and changed nothing but looking exactly like a button that
    /// does not work.</para>
    /// </remarks>
    public bool CanContinue =>
        !IsRunning
        && CurrentPhase == GoalPhase.Summary
        && _engine.IterationCount < GoalCompletionPolicy.MostAttempts
        && _engine.LastStopReason switch
        {
            GoalStopReason.BudgetSpent => true,

            // A review asked for on its own has done what it was asked and stopped; carrying on into
            // the loop is the obvious next thing to want, and it is what the button already does. The
            // arithmetic needs no case of its own: AttemptsContinueWouldAdd answers 0 while the budget
            // still has attempts in it, and after a review-only run none have been spent — so nothing
            // is added, the label stays a plain "Continue", and the loop runs the attempts the panel
            // defines.
            GoalStopReason.Reviewed => true,

            // An attempt that wrote nothing: either it was refused and this is the retry the summary
            // asks for, or the unchanged tree was reviewed on the way out and the next implementation
            // is handed findings this one never saw. See the remarks.
            GoalStopReason.NoChange => true,

            // Met has nothing to continue towards, and NoProgress has just established that two reviews
            // running found exactly the same things.
            _ => false,
        };

    /// <summary>
    /// The button says what pressing it costs the budget.
    /// </summary>
    /// <remarks>
    /// <para><c>Continue · +2</c> where the attempts really ran out: it reads the number out of the
    /// attempts field at the moment it is clicked, so somebody who wants two more can type 2 and see
    /// the button agree before pressing it.</para>
    /// <para><c>Continue · 5 left</c> where they did not, which used to be a bare <c>Continue</c>.
    /// Silence there was exactly backwards: the button said nothing when five attempts were waiting
    /// and named a number when none were, so the one reading available was "no number means no
    /// attempts". Both states now carry their figure and the <c>+</c> is what tells them apart —
    /// raising the ceiling, against spending what is already in it.</para>
    /// <para>The <c>left</c> branch can never say zero: it is reached only while
    /// <see cref="GoalWorkflowEngine.IterationCount"/> is below <see cref="GoalWorkflowEngine.MaxIter"/>,
    /// and <see cref="CanContinue"/> has already refused a run at the ceiling.</para>
    /// </remarks>
    public string ContinueLabel =>
        AttemptsContinueWouldAdd() is var added && added > 0
            ? $"Continue · +{added}"
            : $"Continue · {_engine.MaxIter - _engine.IterationCount} left";

    /// <summary>
    /// What Continue would really add.
    /// </summary>
    /// <remarks>
    /// <para>Nothing at all where the budget still has attempts in it: there are attempts left and
    /// Continue only has to let them happen, so adding the field on top would raise a ceiling the user
    /// set without the button ever saying it would.</para>
    /// <para>Otherwise the attempts field, unless the ceiling is nearer than that. The label used to
    /// read the field alone, so a goal 48 attempts in offered "+5" and added two.</para>
    /// </remarks>
    private int AttemptsContinueWouldAdd() =>
        _engine.IterationCount < _engine.MaxIter
            ? 0
            : Math.Min(_engine.IterationCount + GoalCompletionPolicy.Attempts(_engine.Criteria),
                       GoalCompletionPolicy.MostAttempts) - _engine.IterationCount;

    partial void OnHasUncommittedChangesChanged(bool value) => OnPropertyChanged(nameof(CanDetectGoal));

    /// <summary>Pausing and resuming move the button in the conversation, not only the header's glyph.
    /// </summary>
    partial void OnIsPausedChanged(bool value) => RefreshFinishedRunActions();

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanDetectGoal));
        OnPropertyChanged(nameof(CanContinue));
        RefreshFinishedRunActions();
        RefreshAsk();
    }

    partial void OnCurrentPhaseChanged(GoalPhase value)
    {
        OnPropertyChanged(nameof(RunStage));
        OnPropertyChanged(nameof(CanDetectGoal));
        OnPropertyChanged(nameof(CanContinue));
        RefreshFinishedRunActions();
        RefreshAsk();
    }

    /// <summary>The plan block's button reads the box, so it has to be told when the box changes.
    /// </summary>
    partial void OnInputTextChanged(string value) => OnPropertyChanged(nameof(ApprovalActionLabel));

    // ── What the tile is asking for right now ───────────

    /// <summary>
    /// The clarifying questions, each with its own box.
    /// </summary>
    /// <remarks>
    /// Rebuilt from the engine rather than added to, so there is one copy of the truth and a reload
    /// produces the same round a fresh one does.
    /// </remarks>
    public ObservableCollection<GoalQuestionAnswer> Questions { get; } = [];

    /// <summary>
    /// Wired once, from both constructors, because <see cref="ShowQuestions"/> is derived from the
    /// collection and a derived flag that only updates when somebody remembers to say so is a flag that
    /// is wrong the first time somebody does not.
    /// </summary>
    private void WatchQuestions() => Questions.CollectionChanged += (_, _) => RefreshAsk();

    /// <summary>
    /// Whether a round of questions is being asked.
    /// </summary>
    /// <remarks>
    /// The round replaces the composer rather than standing above it, because they would be two boxes
    /// asking for the same thing with different filing rules — and an answer typed into the wrong one
    /// is filed against the wrong question. While it is up the composer has nothing to send that the
    /// round cannot send better.
    /// </remarks>
    public bool ShowQuestions =>
        !IsRunning && Questions.Count > 0 && CurrentPhase == GoalPhase.Clarify;

    /// <summary>
    /// Whether the plan is waiting to be approved. Not while questions are up: a tool that asked
    /// something has not proposed anything yet.
    /// </summary>
    /// <remarks>
    /// <c>ProposedPlan</c> is nullable and starts null, so it is matched rather than measured — the
    /// engine's own idiom, and reading <c>.Length</c> off it threw on every tile that had not proposed
    /// one yet. Which is every tile, at the moment the binding first asks.
    /// </remarks>
    public bool ShowApproval =>
        !IsRunning && !ShowQuestions && CurrentPhase == GoalPhase.Plan
        && _engine.ProposedPlan is { Length: > 0 };

    /// <summary>
    /// Whether the plain composer is up.
    /// </summary>
    /// <remarks>
    /// <para>Not while the tool is working. The guard at the top of <c>Submit</c> returns while
    /// <c>IsRunning</c>, so a composer shown there is a box that accepts text and does nothing with it
    /// — and the one thing it did do, silently, was hold the text that a finishing detection then
    /// overwrote.</para>
    /// <para><b>And not in Implement or Review at all</b>, which is where a stopped run sits. There is
    /// nothing for it to send there — <c>Submit</c>'s own case for those two phases hands the text back
    /// and says the run is stopped — so it was a box that accepted typing and answered with a sentence.
    /// It was kept on the reasoning that a phase with no composer and no explanation is a tile that has
    /// stopped responding, and that reasoning was right about the explanation and wrong about the box:
    /// the explanation is now a labelled Resume under the transcript, which says the same thing and can
    /// be pressed.</para>
    /// </remarks>
    public bool ShowComposer =>
        !IsRunning && !ShowQuestions && !ShowApproval && !GoalWorkflowEngine.IsMidRun(CurrentPhase);

    /// <summary>What the plan block's one button does, which follows the box under it. A user who has typed
    /// a correction is not asking to approve, and a button labelled "Approve" that sends their
    /// correction instead — or throws it away — would be the same betrayal twice.</summary>
    public string ApprovalActionLabel =>
        InputText.Trim() is { Length: > 0 } typed && !GoalWorkflowEngine.IsApproval(typed)
            ? "Send changes"
            : "Approve plan";

    /// <summary>
    /// The count, above the questions.
    /// </summary>
    /// <remarks>
    /// All that is left of a line that used to say what was being asked as well: everything else it
    /// said was already on the status strip, in the placeholder and on the button. The count stays
    /// because a round is as tall as it is — three questions with their reasons run past the fold of a
    /// small tile, and "3 questions" at the head is how you know there is a third one down there. It
    /// used to be justified by the panel being capped and scrolling; the cap is gone and the reason is
    /// not the same one.
    /// </remarks>
    public string QuestionsTitle =>
        Questions.Count == 1 ? "1 question" : $"{Questions.Count} questions";

    /// <summary>
    /// Puts the engine's pending questions on screen.
    /// </summary>
    /// <remarks>
    /// Any answers already typed are lost, and that is correct: this runs only when the set of
    /// questions is replaced, and an answer to a question that is no longer being asked has nowhere to
    /// go.
    /// </remarks>
    private void SyncQuestions()
    {
        Questions.Clear();
        for (var i = 0; i < _engine.PendingQuestions.Count; i++)
        {
            // The engine's own question object, written straight back into. Saving is debounced by the
            // store, so a keystroke costs nothing here and the answers survive the tile being closed —
            // which is what persisting the questions was for.
            var question = _engine.PendingQuestions[i];
            Questions.Add(new GoalQuestionAnswer(i + 1, question, answer =>
            {
                question.Answer = answer;
                _store.SaveSoon();
            }));
        }

        RefreshAsk();
    }

    private void RefreshAsk()
    {
        OnPropertyChanged(nameof(ShowQuestions));
        OnPropertyChanged(nameof(ShowApproval));
        OnPropertyChanged(nameof(ShowComposer));
        OnPropertyChanged(nameof(QuestionsTitle));

        // Resume stands down while one of the blocks above is asking for something, so it moves with
        // them and not only with the run's own state.
        OnPropertyChanged(nameof(ShowResume));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(HasFinishedRunActions));
    }

    /// <summary>
    /// Sends the answers as one numbered message, the shape the next prompt reads.
    /// </summary>
    /// <remarks>
    /// <para>Unanswered questions are left out rather than sent empty. A blank line under a number
    /// says "none of your business" to a model that cannot tell it from a skipped one, and the round
    /// after it asks again.</para>
    /// <para>The round becomes one message, here, at the moment it is answered: the questions as they
    /// were asked with the answers under them, drawn as the same block the user has just filled in.
    /// That is the whole of the record, which is why <c>echoTyped</c> is off below — the answers are
    /// already in the row above, and a second copy of them as a turn of the user's own is the same text
    /// twice in a conversation whose whole argument is that it stays readable.</para>
    /// <para>Every question is snapshotted, not only the answered ones. They were all asked, and a
    /// round of three answered once is still a round of three.</para>
    /// </remarks>
    [RelayCommand]
    private async Task SendAnswers()
    {
        // The same question the block's own visibility asks, so a phase the tile has moved on from
        // cannot send answers to questions it is no longer asking.
        if (!ShowQuestions) return;

        var answered = Questions.Where(q => q.Answer.Trim().Length > 0).ToList();
        if (answered.Count == 0)
        {
            await SayOnceAsync("Answer at least one of the questions before sending.");
            return;
        }

        var text = string.Join("\n", answered.Select(q => $"{q.Marker} {q.Answer.Trim()}"));
        var round = Questions.Select(q => q.Snapshot()).ToList();

        // The record first, the pending set second: clearing is what takes the live block off the
        // screen, and doing it before the record is written is a moment in which the round exists
        // nowhere at all. Not markdown — this is composed here, and its columns are made of spaces.
        await AddMessageAsync(GoalMessageRole.Assistant, GoalTranscript.Answered(round),
            GoalPhase.Clarify, questions: round);
        ClearPendingQuestions();

        InputText = text;
        await SubmitCore(echoTyped: false);
    }

    /// <summary>Approves the plan, or sends the correction typed under it — whichever the box says.
    /// Both go through <c>Submit</c>, which is where every rule about phases, pauses and discarding a
    /// session already lives.</summary>
    [RelayCommand]
    private async Task ApproveOrChange()
    {
        // The same question the plan box's visibility asks. IsRunning alone let the command run in a
        // phase with no plan in it, where Submit's own "there is no plan to approve yet" is the only thing
        // that catches it.
        if (!ShowApproval) return;

        if (InputText.Trim().Length == 0)
            InputText = "ok";

        await Submit();
    }

    /// <summary>What a status line does when it finally reaches the dispatcher: nothing, if the run it
    /// belonged to has ended. Internal so the race can be stated in a test rather than raced in one.
    /// </summary>
    internal void SetActivityIfRunning(string doing)
    {
        if (IsRunning) Activity = doing;
    }

    private void ClearPendingQuestions()
    {
        if (_engine.PendingQuestions.Count == 0 && Questions.Count == 0) return;

        _engine.SetPendingQuestions(null);
        SyncQuestions();

        // Written, not left for whatever saves next. The pending set is persisted precisely so a closed
        // tile comes back still asking; a set cleared only in memory comes back asking questions that
        // were answered, or abandoned, before the application stopped.
        SaveStateNow();
    }

    /// <summary>
    /// What the tool is doing at this moment, in a few words, or empty.
    /// </summary>
    /// <remarks>
    /// <para>Shown beside the phase on the status strip and nowhere else. It is not transcript: a run
    /// touches dozens of files and every one of those lines would be in the way tomorrow, while the
    /// question it answers — "is this thing still doing something?" — is only ever asked about now.
    /// </para>
    /// <para>Cleared whenever a run ends, by <c>WorkingAsync</c>, so a finished tile never sits showing
    /// the last file the tool happened to open.</para>
    /// </remarks>
    [ObservableProperty] private string _activity = "";

    public ObservableCollection<GoalMessage> Messages { get; } = [];

    /// <summary>
    /// The completion-criteria panel. Its own object because editing seven settings is not this class's
    /// job — see <see cref="GoalCriteriaEditor"/>.
    /// </summary>
    public GoalCriteriaEditor Criteria { get; }

    /// <summary>What the last review found, one entry per severity that had anything — so the strip
    /// shows nothing at all after a clean review, and a severity added later needs no work here.
    /// </summary>
    public ObservableCollection<GoalBadge> Badges { get; } = [];

    /// <summary>
    /// The badge whose findings are being shown, or null when nothing is open.
    /// </summary>
    /// <remarks>
    /// One property rather than a flag beside a list: what is on screen and whether anything is on
    /// screen are the same question, and two properties answering it is one of them going stale.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingFindings))]
    private GoalBadge? _openBadge;

    /// <summary>Whether the findings dialog is up.</summary>
    public bool IsShowingFindings => OpenBadge is not null;

    /// <summary>Opens a badge's findings.</summary>
    /// <remarks>
    /// Refuses a badge with nothing in it rather than opening an empty dialog. That badge is also not
    /// hit-testable, so this is the second of two answers to the same question - deliberately, because
    /// the first lives in markup and the one that matters is the one a command cannot be talked out of.
    /// </remarks>
    [RelayCommand]
    private void OpenFindings(GoalBadge? badge)
    {
        if (badge?.HasFindings == true) OpenBadge = badge;
    }

    [RelayCommand]
    private void CloseFindings() => OpenBadge = null;
    /// <summary>The agents the strip offers to carry the goal out.</summary>
    /// <remarks>Only what this machine can actually run — an instance whose CLI is not installed, or
    /// whose provider has been deleted, is left out rather than shown greyed. Choosing something that
    /// cannot start is a run that fails after the prompt has been built.</remarks>
    public ObservableCollection<GoalAgentChoice> AvailableAgents { get; } = [];

    /// <summary>What the review chooser offers: the same agents, plus "the one doing the work".</summary>
    /// <remarks>Its own type rather than a null in the agent list: the empty id is a real option and the
    /// default one, and a null row in a bound list is an empty line the user reads as a broken entry.
    /// </remarks>
    public ObservableCollection<GoalReviewerChoice> ReviewAgentChoices { get; } = [];

    /// <summary>The permission modes the strip offers, as words.</summary>
    /// <remarks>
    /// <para><see cref="AiBehaviours.Headless"/> and not the full vocabulary: every run this strip
    /// governs is headless, and the three modes left out of that list are the three a run with nobody
    /// to ask cannot carry out — see the remarks there.</para>
    /// <para>Narrowed again by what the execution agent actually has, exactly as the chooser in
    /// Settings is. Offering opencode "auto" — a gate it has no flag for — stored a mode that
    /// <c>AiProcessRunner.Fit</c> rounds away to <see cref="AiBehaviour.ToolDefault"/>, so the run went
    /// out asking for permission nobody was there to give while the strip said it would not. The floor
    /// is meant to be reached by a stored value, never by a word somebody was offered.</para>
    /// </remarks>
    public IReadOnlyList<string> AvailablePermissionModes =>
        [.. OfferedBehaviours.Select(AiBehaviours.Label)];

    /// <summary>The modes behind <see cref="AvailablePermissionModes"/>.</summary>
    /// <remarks>Asked under the implementing phase, because that is the one phase whose permission
    /// comes from this strip at all: the phases that write nothing take theirs from the agent, by
    /// phase. An agent that supports none of the headless modes still gets
    /// <see cref="AiBehaviour.ToolDefault"/> — a chooser with no rows says nothing.</remarks>
    private IReadOnlyList<AiBehaviour> OfferedBehaviours
    {
        get
        {
            if (ExecutionAgent is not { } choice) return AiBehaviours.Headless;

            var supported = choice.Agent.SupportedBehaviours(
                choice.Instance, AiUsage.Headless(GoalPhase.Implement));
            var offered = AiBehaviours.Headless.Where(supported.Contains).ToList();
            return offered.Count > 0 ? offered : [AiBehaviour.ToolDefault];
        }
    }

    /// <summary>The effort levels the strip offers.</summary>
    public IReadOnlyList<string> AvailableEfforts => AiEfforts.Labels;

    /// <summary>
    /// How hard the tool is asked to think, as the strip shows it.
    /// </summary>
    /// <remarks>
    /// A setting rather than a per-goal criterion, beside the permission mode and for the same reasons:
    /// it is about this machine and this tool rather than about the branch, and a goal file travels with
    /// a branch. No confirmation of any kind — unlike <c>bypass</c>, the worst this can do is cost time
    /// and tokens, both of which are visible while they are being spent.
    /// </remarks>
    public string EffortLabel
    {
        get => AiEfforts.Label(_settingsService.Settings.GoalEffort);
        set
        {
            var effort = AiEfforts.FromLabel(value);
            if (effort == _settingsService.Settings.GoalEffort) return;

            _settingsService.Settings.GoalEffort = effort;
            _settingsService.DebouncedSave();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// How much the tool may do without asking, read from and written straight back to settings.
    /// </summary>
    /// <remarks>
    /// <para>Not an <c>[ObservableProperty]</c> over a field of its own: the value lives in
    /// <c>settings.json</c>, and a field beside it would be a second copy to keep in step — the tile is
    /// rebuilt from settings on every open, so the copy would be the one that is wrong.</para>
    /// <para>Written on selection rather than behind a Save button, like everything else on this strip.
    /// A second Goal tile already open keeps showing the old word until it is rebuilt, which is the one
    /// cost of there being a single setting; the run itself always reads settings, so no tile ever
    /// <em>runs</em> with the stale one.</para>
    /// </remarks>
    public string PermissionModeLabel
    {
        // Rounded into what the strip offers, so a mode stored by a newer build — or left behind by an
        // older one that offered "accept edits" — shows as the mode a run would actually use rather
        // than leaving the combo box blank on a word it does not contain.
        get => AiBehaviours.Label(
            AiBehaviours.RoundDown(_settingsService.Settings.GoalPermissionMode, OfferedBehaviours));
        set
        {
            var mode = AiBehaviours.FromLabel(value);
            if (mode == _settingsService.Settings.GoalPermissionMode) return;

            // One mode is asked about, once, and the asymmetry is the point: this tile already refuses
            // to run a shell command out of a goal file without a question, and "nothing is asked about
            // at all, on every Goal tile you have" is a larger grant than any single command. A combo
            // box is a fine control for a preference and a thin one for a decision with no undo — the
            // first unattended run is where it is discovered.
            //
            // Deliberately not a repeat: it is a setting, so once chosen it stays chosen. And the
            // question is asked *before* the write, so a refusal leaves the setting exactly as it was.
            if (mode == AiBehaviour.BypassPermissions)
            {
                // Discarded rather than awaited, because a property setter cannot await — and the task
                // catches everything itself, so nothing is left unobserved. The setting is written only
                // on a yes, and the notification at the end puts the strip back to what is stored,
                // which is how a refusal undoes the selection the combo box has already made.
                _ = ConfirmBypassThenApplyAsync();
                return;
            }

            _settingsService.Settings.GoalPermissionMode = mode;
            _settingsService.NotifyChanged();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Asks before turning every safeguard off, and writes the setting only on a yes.
    /// </summary>
    /// <remarks>
    /// <para>No dialog means <b>no</b>, as on the Settings dialog and for the same reason.
    /// An unanswered question is not a yes, and this is the largest single grant this
    /// application makes: it applies to every Goal tile, and the first place it is noticed is an
    /// unattended run that has already happened.</para>
    /// <para>The other three modes are not asked about. They are preferences with a floor under them;
    /// this one removes the floor.</para>
    /// </remarks>
    private async Task ConfirmBypassThenApplyAsync()
    {
        try
        {
            var agreed = ConfirmAction != null && await ConfirmAction(
                "Run the AI tool with no permission checks at all?\n\n" +
                "It will edit, create and delete files and run commands in this workspace without " +
                "asking. This applies to every Goal tile, until you change it back.");

            if (agreed)
            {
                _settingsService.Settings.GoalPermissionMode = AiBehaviour.BypassPermissions;
                _settingsService.NotifyChanged();
            }
        }
        catch (Exception ex)
        {
            // Swallowed to a warning rather than left to the dispatcher: this runs detached from any
            // caller, and an escape from here ends the process over a combo box.
            Trace.TraceWarning($"Asking about the permission mode failed: {ex.Message}");
        }
        finally
        {
            // Whatever happened, the strip is put back to what is actually stored — which is how a
            // refusal undoes the selection the combo box has already made on screen.
            OnPropertyChanged(nameof(PermissionModeLabel));
        }
    }

    /// <summary>
    /// How many tool calls the most recent AI run was refused permission for.
    /// <para>Kept for one question only: an implementation that changed no files is a dead end when the
    /// tool decided against the work and a misconfiguration when it was never allowed to do it, and the
    /// worktree looks identical either way. Not persisted — it describes the run that has just
    /// happened, and after a restart there is nothing left to explain.</para>
    /// </summary>
    private int _lastRunDenials;

    /// <summary>
    /// How many unasked retries against a dropped stream the run under way still has.
    /// <para>Granted at each user-initiated start (<see cref="WorkingAsync"/> — Resume, Submit, an
    /// answer, an approved plan), spent by the automatic retries, and never renewed inside the work:
    /// once the allowance is gone, a broken stream stops the loop and waits for the user, exactly as
    /// every other failure always has. The size of the allowance is <see cref="GoalTilePolicy
    /// .BrokenStreamRetries"/> — one today, raised by changing that number and nothing else. Not
    /// persisted: it describes the run under way, and after a restart the allowance is the next button
    /// press's to give.</para>
    /// </summary>
    private int _brokenStreamRetriesLeft;

    public Func<string, Task<bool>>? ConfirmAction { get; set; }


    /// <summary>
    /// How a prompt is actually run. Replaced by a test so the phase machine can be driven without a
    /// tool installed, a process spawned or a repository on disk.
    /// <para>The seam is the same one <c>TerminalControl.PtyFactory</c> gives the launch chain, and for
    /// the same reason: this loop is where the bugs kept landing, and every one of them needed a real
    /// AI process and a real worktree to reach. A static default rather than a constructor argument,
    /// because nothing in the application chooses it and a parameter every call site has to pass null
    /// for is a parameter that will be passed the wrong thing eventually.</para>
    /// </summary>
    /// <remarks>
    /// The stand-in answers with an <see cref="AiOutput"/> rather than a string so a test can say the
    /// tool <em>failed</em> — the path that pauses the run and prints what the tool managed to say.
    /// While this returned a string that path could only be reached at the level of the stream reader,
    /// which is the half that does not decide anything.
    /// </remarks>
    internal static Func<GoalAgentChoice, string, string, CancellationToken, Task<AiOutput>>? AiRunnerFactory { get; set; }

    public string FilePath => _filePath;

    public GoalTileViewModel(string workingDirectory, SettingsService settingsService,
        WorkspaceGitWatcher? gitWatcher = null)
    {
        _workingDirectory = workingDirectory;
        _settingsService = settingsService;

        FileMentions = NewFileMentions();

        var goalsDir = WorkspacePaths.Combine(workingDirectory, "goals");
        _filePath = Path.Combine(goalsDir, $"{Guid.NewGuid():N}.json");

        // The store before the editor, as in the other constructor. The editor's callbacks reach the
        // store, and although they are only invoked later, two constructors building the same object in
        // two orders is where that stops being true without anybody noticing.
        _store = NewStore();
        WatchQuestions();

        // The editor fills itself from the criteria in its own constructor, and on this path there is
        // nothing to load afterwards. The other constructor reloads because LoadState has replaced the
        // criteria underneath it since; doing it here as well suggested a symmetry that is not there.
        Criteria = NewCriteriaEditor();

        DetectAgents();
        WatchWorkingTree(gitWatcher);
        RefreshDetectAvailability();
    }

    public GoalTileViewModel(string filePath, string workingDirectory, SettingsService settingsService,
        WorkspaceGitWatcher? gitWatcher = null)
    {
        _workingDirectory = workingDirectory;
        _settingsService = settingsService;
        _filePath = filePath;
        FileMentions = NewFileMentions();
        _store = NewStore();
        WatchQuestions();
        Criteria = NewCriteriaEditor();

        DetectAgents();
        LoadState();
        Criteria.Reload();
        WatchWorkingTree(gitWatcher);
        RefreshDetectAvailability();
    }

    // ── Hearing about the changes the buttons are about ──

    /// <summary>The workspace's shared watcher, while this tile is listening to it.</summary>
    private WorkspaceGitWatcher.Subscription? _treeWatch;

    /// <summary>
    /// Follow the working tree, so the detect buttons are right without anybody having to click.
    /// </summary>
    /// <remarks>
    /// <para>The changes a goal is detected from are made in the tiles next door, or in an editor
    /// outside this application altogether, and <see cref="OnActivated"/> only closes that gap for
    /// somebody who then clicks <em>into</em> this tile — which a user reading a tile they are already
    /// focused on never does. The watcher is <see cref="WorkspaceGitWatcher"/>: one per workspace,
    /// shared with the git tile, so following the tree here costs no watcher of its own.</para>
    /// <para>What it triggers is the same cheap <c>git status</c> the named moments trigger, under the
    /// same two conditions, so a tile mid-run or mid-conversation spends nothing on an answer nobody can
    /// see. The notification arrives on the watcher's debounce timer, off the UI thread —
    /// <see cref="RefreshDetectAvailability"/> starts its work on the thread pool and marshals back for
    /// itself, so there is nothing to post here.</para>
    /// <para>Null where nothing hands one over: the tests build this tile directly, and a goal tile in
    /// a workspace that is not a repository has nothing to watch. Either way the named moments stay
    /// exactly as they were — this is a second route to the same refresh, never the only one.</para>
    /// </remarks>
    private void WatchWorkingTree(WorkspaceGitWatcher? gitWatcher)
    {
        if (gitWatcher is null) return;

        _treeWatch = gitWatcher.Subscribe(() =>
        {
            if (_disposed) return;
            if (IsRunning || CurrentPhase is not (GoalPhase.Goal or GoalPhase.Summary)) return;

            RefreshDetectAvailability();
        });
    }

    // ── Agent detection ─────────────────────────────────

    /// <summary>
    /// Rebuilds the list of agents this machine can run, and settles which two this goal uses.
    /// </summary>
    /// <remarks>
    /// <para>Called when the tile is built and when the user asks for another look. <b>Nothing is
    /// substituted mid-run</b>: a chosen agent that has since disappeared leaves the choice standing and
    /// the run says so, because the alternative — quietly moving the goal onto whatever else is
    /// installed — is what the tool list used to do, and it changed the model a plan was written for
    /// without saying a word.</para>
    /// </remarks>
    private void DetectAgents()
    {
        _availableAgents = GoalAgents.Available(_settingsService.Settings);

        AvailableAgents.Clear();
        foreach (var choice in _availableAgents)
            AvailableAgents.Add(choice);

        ReviewAgentChoices.Clear();
        // "The agent doing the work" first: it is the default, and a default below five other rows is
        // one the user has to go looking for.
        ReviewAgentChoices.Add(GoalReviewerChoice.SameAsExecution);
        foreach (var choice in _availableAgents)
            ReviewAgentChoices.Add(GoalReviewerChoice.For(choice));

        // Only when nothing has been chosen. A goal reopened on a machine where its agent is missing
        // keeps naming the agent it was planned with, which is what lets the tile say so.
        if (ExecutionAgentInstanceId.Length == 0 && _availableAgents.Count > 0)
            ExecutionAgentInstanceId = _availableAgents[0].InstanceId;

        OnPropertyChanged(nameof(ExecutionAgent));
        OnPropertyChanged(nameof(ReviewAgent));
        AnnounceOfferedBehaviours();
    }

    /// <summary>
    /// Looks again for the agents on this machine, without disturbing what the goal has chosen.
    /// </summary>
    /// <remarks>The scan walks <c>PATH</c> and several home directories per agent, so it goes to a
    /// background thread rather than stopping the one drawing the tile. Its answer is put back on the UI
    /// thread, because the two collections above are bound to combo boxes.</remarks>
    private async Task RediscoverAgentsAsync()
    {
        var settings = _settingsService.Settings;
        var found = await Task.Run(() => GoalAgents.Available(settings));

        await Post(() =>
        {
            _availableAgents = found;

            foreach (var choice in found.Where(c => AvailableAgents.All(a => a.InstanceId != c.InstanceId)))
            {
                AvailableAgents.Add(choice);
                ReviewAgentChoices.Add(GoalReviewerChoice.For(choice));
            }

            if (ExecutionAgentInstanceId.Length == 0 && found.Count > 0)
                ExecutionAgentInstanceId = found[0].InstanceId;

            OnPropertyChanged(nameof(ExecutionAgent));
            OnPropertyChanged(nameof(ReviewAgent));
            AnnounceOfferedBehaviours();
        });
    }

    /// <summary>The agent carrying the goal out, or null when the one it names is not here.</summary>
    public GoalAgentChoice? ExecutionAgent => GoalAgents.WithId(_availableAgents, ExecutionAgentInstanceId);

    /// <summary>The agent reviewing the work: the one chosen for it, or the one doing the work.</summary>
    /// <remarks>Falling back to the execution agent rather than to nothing is what makes an empty
    /// <see cref="ReviewAgentInstanceId"/> mean "the same agent" everywhere, including in the message a
    /// failure prints — the one place two agents most need telling apart. It is the fallback for an
    /// empty id and for nothing else: an id naming a reviewer that is no longer available answers null,
    /// so the run stops and says so, rather than quietly handing the review to the agent whose own work
    /// is being reviewed.</remarks>
    public GoalAgentChoice? ReviewAgent =>
        ReviewAgentInstanceId.Length == 0
            ? ExecutionAgent
            : GoalAgents.WithId(_availableAgents, ReviewAgentInstanceId);

    /// <summary>
    /// Which agent runs this phase.
    /// </summary>
    /// <remarks>Only the review is somebody else's job. Clarifying, planning, implementing and
    /// summarising are one train of thought and splitting them across two models would mean a plan
    /// written by one agent being carried out by another that never saw the questions.</remarks>
    private GoalAgentChoice? AgentFor(GoalPhase phase) =>
        phase == GoalPhase.Review ? ReviewAgent : ExecutionAgent;

    /// <summary>What the transcript says when the agent a phase needs is not here.</summary>
    /// <remarks>Two agents, two absences: a missing review agent is a setting the user chose and can
    /// change in the strip, while a missing execution agent on a machine with none at all is an install.
    /// Told apart because the way out is different.</remarks>
    private string MissingAgentMessage(GoalPhase phase) =>
        _availableAgents.Count == 0
            ? "No AI agent available. Install Claude Code, codex, opencode, pi or agy, then "
              + TryAgain()
            : phase == GoalPhase.Review && ReviewAgentInstanceId.Length > 0
                ? "The agent chosen to review this work is not available. Pick another reviewer in the "
                  + "strip above, then " + TryAgain()
                : "The agent chosen for this goal is not available. Pick another one in the strip "
                  + "above, then " + TryAgain();

    /// <summary>What a message calls the agent that just failed.</summary>
    /// <remarks>Two agents mean two ways to fail, and "the AI tool reported a failure" over a run split
    /// between two of them names neither.</remarks>
    private string NameOf(GoalPhase phase) =>
        AgentFor(phase) is { } choice
            ? phase == GoalPhase.Review && ReviewAgentInstanceId.Length > 0
                ? $"The review agent ({choice.Label})"
                : choice.Label
            : "The AI tool";

    private void OnExecutionAgentInstanceIdChanged(string value)
    {
        OnPropertyChanged(nameof(ExecutionAgent));
        OnPropertyChanged(nameof(ReviewAgent));
        AnnounceOfferedBehaviours();
        SaveStateSoon();
    }

    /// <summary>Puts the permission strip back in step with the agent now carrying the goal out.</summary>
    /// <remarks>The label as well as the list: a mode the previous agent had and this one has not must
    /// not stay selected on a chooser that no longer offers it — the same rule the Settings form
    /// follows. Nothing is written, because the setting is shared by every Goal tile and this tile
    /// changing agent is not the user changing their mind about permission.</remarks>
    private void AnnounceOfferedBehaviours()
    {
        OnPropertyChanged(nameof(AvailablePermissionModes));
        OnPropertyChanged(nameof(PermissionModeLabel));
    }

    private void OnReviewAgentInstanceIdChanged(string value)
    {
        OnPropertyChanged(nameof(ReviewAgent));
        SaveStateSoon();
    }


    // ── Completion criteria ─────────────────────────────

    /// <summary>Whether this goal has already been told why it is not getting more questions. Reset
    /// with the goal, not with the tile.</summary>
    private bool _clarifyBudgetReported;

    private GoalCriteriaEditor NewCriteriaEditor() => new(
        () => _engine.Criteria,
        criteria =>
        {
            // Read before the assignment: what it says about the state the engine is still holding.
            var attemptsChanged = _engine.Criteria.MaxIterations != criteria.MaxIterations;

            _engine.Criteria = criteria;

            // Moving the field by hand makes the number theirs again. AttemptsBeforeExtension exists so
            // the next goal starts from what the user chose rather than from what Continue wrote — and
            // once they have chosen again, the remembered value is the stale one: 5, Continue to 10,
            // then 8 typed in, and the next goal started at 5. Only reached from the panel; Continue
            // writes through the engine and reloads with _filling set, so it never comes through here.
            if (attemptsChanged)
                _engine.AttemptsBeforeExtension = null;

            // The Continue button names the number it will add, and that number is this field.
            OnPropertyChanged(nameof(ContinueLabel));
            OnPropertyChanged(nameof(CanContinue));
            OnPropertyChanged(nameof(HasFinishedRunActions));

            // Messages alone, and no File.Exists: this runs on every keystroke in a text box, and a
            // tile with no messages is one with no goal in it — which is exactly the tile that must not
            // be given a session file. A saved tile always has messages.
            if (Messages.Count > 0)
                SaveStateSoon();
        });

    [RelayCommand]
    private void ToggleCriteria() => ShowCriteria = !ShowCriteria;

    /// <summary>
    /// The catch of last resort for all four ways into a run, and the pause that goes with it.
    /// </summary>
    /// <remarks>
    /// <para>Saying what went wrong was only half of it. The four callers all reach a working phase and
    /// then hand off to the loop, and an exception coming back out of one left the tile in
    /// <c>Implement</c> with nothing implementing, not running and <b>not paused</b> — so
    /// <see cref="ShowResume"/> was false, the composer stands down in that phase because it has
    /// nothing to send, and the finished-run actions all want <c>Summary</c>. Nothing on the tile could
    /// be pressed but <b>+</b>, which throws the goal away. An approved plan and an hour of transcript
    /// behind one unexpected exception.</para>
    /// <para>Pausing is what every <em>expected</em> failure already does — see
    /// <c>HandleNonAnswerAsync</c>, where a tool that could not be launched or answered with nothing
    /// pauses so the user can fix it and click Resume — and there is no reason an unexpected one should
    /// leave the tile in a state the expected ones are careful to avoid.</para>
    /// <para>Only where Resume has something to run (<see cref="GoalTilePolicy.CanResume"/>). A pause
    /// in <c>Goal</c> or <c>Summary</c> is the bug the other way round: the strip would label the tile
    /// "Paused. Click Resume to continue." over a Resume with no case to enter, and keep saying it
    /// after a restart. Those two phases have the composer, which is the way on.</para>
    /// </remarks>
    private async Task StoppedByErrorAsync(string what, Exception ex)
    {
        Trace.TraceWarning($"{what}: {ex.Message}");
        await AddMessageAsync(GoalMessageRole.System, $"Unexpected error: {ex.Message}", CurrentPhase);

        if (GoalTilePolicy.CanResume(CurrentPhase))
            PauseAndWait();
    }

    /// <summary>
    /// Gives a run that ran out of attempts as many more as the attempts field says, and carries on.
    /// <para>Re-entered at the <b>implementation</b>, not at the review. The last thing this run did was
    /// review, and starting there again would spend an AI run re-judging a working tree nothing has
    /// touched since — the same verdict, twice in the transcript, for the price of a run. The findings
    /// it produced are still in the engine, so the implementation that follows is handed them.</para>
    /// <para>The transcript is kept, and that is the whole point of the button. Everything this session
    /// worked out about the goal is in it, and the alternative before this existed was retyping the goal
    /// into a fresh tile and paying for the clarification round again.</para>
    /// </summary>
    [RelayCommand]
    private async Task ContinueRun()
    {
        if (!CanContinue) return;

        // The same arithmetic the label showed, so the button cannot do something other than what it
        // said. The ceiling is applied here rather than left to Attempts to clamp later, so what the
        // panel shows afterwards is the budget really in force: a field reading 60 while the loop uses
        // 50 is one field saying two things.
        var added = AttemptsContinueWouldAdd();

        if (added > 0)
        {
            // Remembered once, on the first Continue of this goal, so a second one does not record the
            // raised value as the one the user chose.
            _engine.AttemptsBeforeExtension ??= _engine.Criteria.MaxIterations;

            _engine.Criteria.MaxIterations = _engine.IterationCount + added;
            Criteria.Reload();
        }

        // Cleared before the loop rather than after it: this is no longer a finished run, and a crash
        // between here and the next summary must not leave a Summary offering Continue over a goal that
        // is mid-implementation.
        _engine.LastStopReason = null;

        // This run is not over, so what it has committed so far is not the whole of it. The scope is
        // worked out from the goal's baseline and filtered by what still differs from HEAD, so the
        // batch already committed drops out of it on its own — but the offer has to come back, or a
        // continued run's second half is never offered at all.
        _committed = false;

        _engine.CurrentPhase = GoalPhase.Implement;
        SyncFromEngine();
        OnPropertyChanged(nameof(CanContinue));

        await AddMessageAsync(GoalMessageRole.System,
            added > 0
                ? $"Continuing for {added} more attempt{(added == 1 ? "" : "s")} " +
                  $"(up to {_engine.Criteria.MaxIterations} in total)."
                : $"Continuing with the {_engine.MaxIter - _engine.IterationCount} attempts still left.",
            GoalPhase.Implement);

        // The same catch of last resort the other three ways into this loop have — Submit, Resume and
        // the detect path. Without it an exception here comes out of an async command with nobody to
        // receive it: the button appears to do nothing at all, which is the one outcome a button must
        // never have, and the tile is left in Implement with nothing implementing.
        try
        {
            await WorkingAsync(() => RunImplementReviewLoopAsync());
        }
        catch (Exception ex)
        {
            await StoppedByErrorAsync("Goal continue error", ex);
        }
    }

    // ── Detecting a goal from the working tree ──────────

    /// <summary>
    /// Somebody has just made this the active tile, so what it offers is about to be read.
    /// </summary>
    /// <remarks>
    /// <para>The changes a goal is detected from are made in the tiles next door, and nothing here hears
    /// about them. Asking on the way in is one <c>git status</c> at the moment the answer starts
    /// mattering, which is what keeps the two detect buttons from being missing over a tree that has
    /// since acquired changes, or offered over one that has since been committed.</para>
    /// <para>Only where the answer is on screen — the same two conditions <see cref="CanDetectGoal"/>
    /// asks besides the tree itself. A tile mid-run, or mid-conversation in a phase that offers no
    /// detect button, would spend a git call on a question nobody can see the answer to; the workflow
    /// refreshes this itself as it goes.</para>
    /// </remarks>
    public void OnActivated()
    {
        if (!IsRunning && CurrentPhase is GoalPhase.Goal or GoalPhase.Summary)
            RefreshDetectAvailability();
    }

    /// <summary>
    /// Asks git whether there is anything to detect a goal from, without making anybody wait for it.
    /// <para>Fire and forget on purpose: the answer only decides whether two buttons are shown, and the
    /// user is not blocked on either.</para>
    /// <para><b>It is asked at moments, and the moments are named rather than implied</b>: when the
    /// tile is built, when a run ends, when a detection finds nothing, when a fresh goal is started,
    /// when the tile becomes the active one, and when the working tree changes
    /// (<see cref="WatchWorkingTree"/>). The run itself still re-reads the tree rather than trusting
    /// this — what is cached here decides two buttons and nothing else.</para>
    /// <para>The last of those moments is the one that took a correction. What was rejected, and still
    /// is, is a filesystem watcher <em>per Goal tile</em> for a button's visibility; the tree is watched
    /// once per workspace and shared with the git tile, so a second goal beside the first costs
    /// nothing. Before it, the answer was refreshed only by clicking into the tile, which never happens
    /// to somebody reading a tile they are already focused on: they changed two files next door, looked
    /// at the tile, and the detect buttons it should have been offering were not there.</para>
    /// </summary>
    private void RefreshDetectAvailability()
    {
        // The workflow keeps unwinding after Dispose and its finally asks for one of these. There is
        // nothing left to show the answer to.
        if (_disposed) return;

        // One check at a time: a burst of writes in the tree — a checkout, a code generator, a build —
        // arrives as several notifications, and each of these is a `git status` of its own. Without
        // this, whichever process happened to finish last wrote the answer, which is not necessarily
        // the one that asked last. Linked to the tile's lifetime so closing the tile still ends it.
        // Exchanged atomically because the notifications arrive on the watcher's timer thread rather
        // than the UI one.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var previous = Interlocked.Exchange(ref _detectAvailabilityCheck, cts);
        previous?.Cancel();
        previous?.Dispose();

        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var has = await NewWorktreeReader().HasChangesAsync(token);
                if (token.IsCancellationRequested) return;

                await Post(() => HasUncommittedChanges = has);
            }
            catch (OperationCanceledException)
            {
                // The tile closed while git was answering. Not worth a line in the log.
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Could not check for uncommitted changes: {ex.Message}");
            }
        });
    }

    /// <summary>The availability check in flight, cancelled by the next one and by <c>Dispose</c>.</summary>
    private CancellationTokenSource? _detectAvailabilityCheck;

    [RelayCommand]
    private Task DetectGoalAsync() => DetectAsync(andRun: false);

    /// <summary>Work out the goal and go straight into the fix loop, without a plan and without a
    /// clarification round — the "I know what I am doing, finish it" path.</summary>
    [RelayCommand]
    private Task DetectGoalAndRunAsync() => DetectAsync(andRun: true);

    /// <summary>Work the goal out and judge the working tree against it, once, changing nothing.</summary>
    [RelayCommand]
    private Task DetectGoalAndReviewAsync() => DetectAsync(andRun: false, andReview: true);

    /// <summary>
    /// Judge the working tree again against the goal already set — the second button a review-only run
    /// leaves behind.
    /// </summary>
    /// <remarks>
    /// Deliberately does <b>not</b> detect the goal again. The reason to press this is that something
    /// has just been fixed by hand in the terminal tile next door, and re-deriving the goal from a tree
    /// that has changed would answer a different question every time. The goal stands; only the verdict
    /// moves.
    /// Named for what it returns, like every other command here that returns a <c>Task</c>. The
    /// generator strips the suffix, so the binding stays <c>ReReviewCommand</c> and the markup does not
    /// move. <c>CommitWork</c> is the one that cannot follow: <c>CommitWorkAsync</c> is already the
    /// method it calls, and the two would collide.
    /// </remarks>
    [RelayCommand]
    private Task ReReviewAsync() =>
        CanReReview ? WorkingAsync(async () =>
        {
            // Typed beside the button, the composer's words narrow THIS review — soft as a guideline,
            // hard where they name @ paths. Both travel as parameters, never onto the goal's scope: a
            // Continue pressed afterwards implements for the goal as it was typed, not for the one
            // look the user narrowed down. A composer left without mentions narrows nothing here —
            // null, so the goal's own scope still applies rather than an empty list overriding it.
            var (guideline, paths) = ReadScopeFromComposer();
            var ran = await RunReviewOnlyAsync(guideline,
                paths.Count > 0 ? paths : null);

            // Cleared after, not before: the words went into a review that actually ran. One that was
            // paused before it started leaves the draft in the box — the only copy of those words
            // there is.
            if (ran) InputText = "";
        }) : Task.CompletedTask;

    /// <summary>
    /// Whether the bar of finished-run actions has anything in it.
    /// </summary>
    /// <remarks>
    /// One bar rather than one per action: three strips of prose stacked over the composer is what this
    /// replaced, and each of them restated the summary printed directly above. The bar itself has to
    /// disappear when empty, or an idle tile carries a band of padding under its transcript for no
    /// reason.
    /// </remarks>
    public bool HasFinishedRunActions => ShowResume || CanReReview || CanCommit || CanContinue;

    /// <summary>Whether Resume is on screen.</summary>
    /// <remarks>
    /// <para><b>In the conversation, and only there, because that is where everything else this tile
    /// asks for is answered.</b> The transcript ends with "This run is stopped. Click Resume to
    /// continue it", and the only Resume on screen was a 13px play glyph in a strip of six at the far
    /// end of the tile, while the questions, the plan, Continue and Commit are all labelled buttons in
    /// the flow. The glyph is gone rather than kept as a second route: an unlabelled duplicate at the
    /// opposite end of the tile is a thing to explain, not a fallback. Pause stays in the header,
    /// because what it interrupts is happening now and must be reachable however far the transcript is
    /// scrolled.</para>
    /// <para><b>The phase is part of the question.</b> The header asked <c>IsPaused</c> on its own,
    /// which is true in a phase <see cref="ResumeAsync"/> has nothing to run: closing a tile
    /// mid-detection pauses it in <c>Goal</c>, so the reopened tile showed an enabled ▶ whose only
    /// effect was to clear the pause. That is what one property for one command is for.</para>
    /// <para>Not while a round of questions or a plan is up. Resume re-runs the phase, which in Clarify
    /// or Plan means asking again — beside an unanswered round that would be a second button doing
    /// something different to the one the block is for, and answering resumes anyway
    /// (<see cref="GoalTilePolicy.AnsweringResumes"/>). A stale pause in <c>Goal</c> is cleared the
    /// same way: by the next thing typed, which is what that phase is waiting for anyway.</para>
    /// </remarks>
    public bool ShowResume =>
        IsPaused && !ShowQuestions && !ShowApproval && GoalTilePolicy.CanResume(CurrentPhase);

    /// <summary>
    /// Whether the button is usable, as against on screen.
    /// </summary>
    /// <remarks>
    /// Shown as soon as the run is paused but disabled until it has actually stopped: cancelling takes
    /// as long as the tool takes to die, and a click in that window used to start a second loop
    /// alongside the one still unwinding. Disabled rather than hidden, because a button that vanishes
    /// for a second or two and comes back is a button the user is chasing.
    /// </remarks>
    public bool CanResume => ShowResume && !IsRunning;

    /// <summary>Whether there is a goal to judge the tree against again.</summary>
    /// <remarks>
    /// <c>Met</c> as well as <c>Reviewed</c>. The reason to press this is that something has been
    /// changed by hand since the verdict — and a goal that was met is the state a user is *most* likely
    /// to go on editing, with nothing else on the bar to ask for a fresh opinion. The other endings
    /// offer Continue, which is the loop and does more than look.
    /// </remarks>
    public bool CanReReview =>
        !IsRunning
        && CurrentPhase == GoalPhase.Summary
        && _engine.LastStopReason is GoalStopReason.Reviewed or GoalStopReason.Met
        && _engine.OriginalGoal.Length > 0;

    private async Task DetectAsync(bool andRun, bool andReview = false)
    {
        // Deliberately *not* guarded on CanDetectGoal, which ContinueRun does guard on itself. The
        // difference is HasUncommittedChanges: it is a cached answer from a git call that ran at one of
        // four named moments, so it is routinely stale, and refusing here on the strength of it would
        // turn "the button was shown a second too early" into a click that does nothing at all. The run
        // re-reads the tree and says what it actually found — which is the honest version of the same
        // refusal.
        if (IsRunning) return;

        // The same question the composer asks, and for the same reason: this replaces the transcript.
        if (!await ConfirmDiscardAsync())
        {
            if (ConfirmAction == null)
                await SayOnceAsync("This tile cannot ask whether to discard the current goal, so it " +
                                   "has kept it.");
            return;
        }

        // The same catch of last resort Submit and Resume have. Without it an exception anywhere in
        // the detection came out of an async command with nobody to receive it: the button appeared to
        // do nothing at all, which is the one outcome a button must never have.
        try
        {
            await WorkingAsync(() => RunDetectAsync(andRun, andReview));
        }
        catch (Exception ex)
        {
            await StoppedByErrorAsync("Goal detection error", ex);
        }

        RefreshDetectAvailability();
    }

    private async Task RunDetectAsync(bool andRun, bool andReview = false)
    {
        // The tree is read *before* the transcript is cleared, and that order is the whole of it. The
        // button is shown on the strength of `git status`, which is a different command from the one
        // that builds the prompt: a commit made between the two, an exclusion the two apply
        // differently, a repository with no HEAD — any of them ends with nothing to detect from, and
        // clearing first meant the user had paid for that with their session.
        Working("Reading the working tree...");

        // Read before anything else: the paths named here filter the very read below, and the words
        // around them go into the detection prompt as the narrowing block. Nothing is cleared yet — a
        // detection that ends without a goal (an unreadable tree, a tool that named none) leaves the
        // draft in the composer, which is the only copy of those words there is. They are consumed
        // below, where the goal is adopted.
        //
        // The fresh scope is passed explicitly, and an empty list with it: the goal being replaced had
        // a scope of its own, and a detection reading through that one — or falling back to it where
        // the new text named none — would answer a question the user did not ask.
        var (guideline, scopePaths) = ReadScopeFromComposer();

        WorktreeSnapshot tree;
        try
        {
            tree = await ReadWorktreeAsync(onlyPaths: scopePaths);
        }
        catch (OperationCanceledException)
        {
            // The label has to come back, or the strip keeps saying the tile is reading a working
            // tree it stopped reading.
            PhaseLabel = _engine.GetPhaseLabel();
            return;
        }

        // Unreadable counts as nothing to detect from. Compose still produces text in that case — the
        // note saying git could not be read — so without this the tool was handed a working tree
        // consisting of an apology and asked what it was for.
        if (tree.Text == null || !tree.Readable)
        {
            // Nothing was cleared, so this is a note on top of whatever was already here — and it says
            // which of the two it is, because "nothing to do" and "I could not look" send the user to
            // different places.
            await AddMessageAsync(GoalMessageRole.System,
                tree.Readable
                    ? "There are no uncommitted changes to work a goal out of."
                    : "The working tree could not be read — this workspace may not be a git repository.",
                CurrentPhase);
            PhaseLabel = _engine.GetPhaseLabel();

            // The buttons go away because that is now the truth — asked for by DetectAsync on the way
            // out, for every path through here. Asking again from this one spent a second git process
            // on the same click for the same answer.
            return;
        }

        // Nothing is cleared until there is something to put in its place. The tool has to be run
        // first, and a tool that fails, returns nothing or names no goal is common enough that the
        // alternative — an empty tile where a session used to be — is the ordinary outcome rather than
        // the unlucky one.
        Working("Working out the goal from your changes...");
        var run = await RunAiAsync(_engine.BuildDetectGoalPrompt(tree.Text!, PromptBudget(), guideline));

        if (run.Verdict != GoalRunVerdict.Answered)
        {
            // Every other verdict has already put its own explanation in the transcript. Nothing is
            // paused: there is no new goal yet, so there is nothing to resume — the buttons are still
            // there and clicking one again is the whole of "try that again".
            PhaseLabel = _engine.GetPhaseLabel();
            return;
        }

        var goal = GoalResponseParser.ParseDetectedGoal(run.Text);
        if (goal.Length == 0)
        {
            await AddMessageAsync(GoalMessageRole.System,
                "The tool did not name a goal. Try again, or type one yourself.", CurrentPhase);
            PhaseLabel = _engine.GetPhaseLabel();
            return;
        }

        // Adopted, not offered. Both buttons now do the same first thing — work the goal out and make
        // it the goal — and differ only in what comes next: a conversation, or the loop.
        //
        // This replaces a draft parked in the composer, waiting for Send. The argument for that was
        // that a detected goal is the tool's reading of half-finished work and the user should edit it
        // before anything acts on it. What it cost was a click and a phase: the sentence sat in a box
        // doing nothing while the tile said it was waiting for a goal it had already written, and the
        // careful rules around never overwriting what the user had typed existed only to protect a
        // draft nobody had asked to keep.
        //
        // Editing has not gone away, it has moved to where it works better. Clarify is a conversation:
        // the tool asks what it cannot decide, the user answers, and an answer that says "no, what I am
        // really doing is X" is folded into the plan. The one thing that does *not* move is
        // OriginalGoal, which is fixed here and carried by every later prompt — so a badly detected
        // goal is corrected by starting over with +, not by talking it round.
        //
        // Here is also where the composer's words are finally consumed — now, and not before the read,
        // so every failure above left the draft where the user typed it. The scope belongs to the goal
        // that has just been adopted: every later tree read filters the same way, and on the Detect &
        // Review path the words go to the one review this button produces, which would otherwise lose
        // them — the detection that shaped them was a means, not the result.
        //
        // Consumed, but cleared only where something takes the composer over. On this button the
        // detection's answer goes to the transcript and the loop does not start, so a draft that was
        // there before the click stays a draft — pinned by A_detection_over_a_composer_the_user_had_
        // already_filled_keeps_it.
        //
        // Set after StartFreshGoal, which clears the scope of the goal being replaced: the paths named
        // here belong to the one that has just started.
        StartFreshGoal(goal);
        _engine.ScopePaths = LivePaths(scopePaths);
        if (andRun || andReview) InputText = "";
        SyncFromEngine(save: File.Exists(_filePath));
        await AddMessageAsync(GoalMessageRole.User, goal, GoalPhase.Goal);
        await CaptureBaselineAsync();

        if (andReview)
        {
            // The same fact the run path records, and for the same reason: what is being judged here
            // is the work that was already in the tree, so every diff from now on measures from HEAD.
            // Without it the review itself came out right — it reads the tree unscoped — and Continue
            // afterwards did not: the loop scoped to a baseline taken over those very changes, judged a
            // fraction of them, and could report the goal met over work it had never seen.
            _engine.ReviewsExistingWork = true;

            // The tree has just been read to work the goal out, and nothing has touched it since — so
            // it is read again rather than kept, for one reason: that read was capped for the *detect*
            // prompt, and this one is capped for the review's. The narrowing travels: this review is
            // the whole of what Detect & Review produces, and the goal's scope — just adopted below —
            // is the hard half of it.
            await RunReviewOnlyAsync(guideline);
            return;
        }

        if (!andRun)
        {
            // Still inside goal-setting: the tool may have questions, and the user can still change
            // what this is about before a line of code is written. RunClarifyAsync rather than a
            // WorkingAsync around it — DetectAsync already holds one, and nesting a second would
            // dispose this run's own CancellationTokenSource underneath it.
            await RunClarifyAsync();
            return;
        }

        // Straight into the loop, starting at the review. The changes are already on disk, so the first
        // thing owed is a judgement of them, not another implementation — the same reasoning, and the
        // same argument, as resuming a run that was interrupted after its implementation finished.
        //
        // ApprovedPlan is deliberately left empty. It used to be set to the detected goal, which put
        // the same sentence into the implement prompt twice — once under "the goal" and once under
        // "Approved implementation plan" — where the second copy is worse than redundant: it claims
        // the user approved a plan they were never shown, and it spends prompt budget saying so. No
        // plan phase ran on this path, so there is no plan, and the prompt is right to say nothing.
        // The changes to judge are the ones already on disk, so this run measures from HEAD rather
        // than from the baseline taken over them a moment ago — see DiffBase. The baseline itself is
        // kept: the tool is about to edit the user's uncommitted work, which is exactly when a way back
        // is worth having.
        _engine.ReviewsExistingWork = true;

        _engine.CurrentPhase = GoalPhase.Review;
        SyncFromEngine();

        await RunImplementReviewLoopAsync(startAtReview: true);
    }

    // ── Pasted images ───────────────────────────────────

    /// <summary>
    /// Where images pasted into this tile are written. Built on the first paste rather than in the
    /// constructors: it is the same object either way, and asking for it moves the workspace's state
    /// directory into place — work a tile nobody pastes into has no reason to do.
    /// </summary>
    private GoalImageStore? _imageStore;

    private GoalImageStore ImageStore => _imageStore ??= new GoalImageStore(_workingDirectory);

    /// <summary>
    /// Takes an image the user pasted, and leaves its marker where their caret was.
    /// </summary>
    /// <remarks>
    /// <para>The bytes rather than the clipboard, because a clipboard belongs to a window — the same
    /// argument that keeps copying a message in the view. What arrives here is already encoded, so
    /// this knows nothing about clipboards, formats or windows and a test can hand it a file.</para>
    /// <para>A failure to write is said in the transcript and <b>no marker is inserted</b>. The other
    /// way round is the trap: a marker in the goal whose file was never written is one the tool is
    /// told to open, and the run spends an attempt on a picture that does not exist.</para>
    /// </remarks>
    [RelayCommand]
    private async Task AttachImage(byte[]? image)
    {
        if (image is not { Length: > 0 }) return;

        string path;
        try
        {
            path = ImageStore.SavePng(image);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Saving a pasted image failed: {ex.Message}");
            await SayOnceAsync($"The pasted image could not be saved: {ex.Message}");
            return;
        }

        // A trailing space, so the next word the user types is not welded onto the marker — which
        // would leave the goal saying [Image #1]make instead of naming an image at all.
        InsertIntoComposer(_engine.AttachImage(path) + " ");
        SaveStateSoon();
    }

    /// <summary>Puts text into the composer where the caret is, and leaves the caret after it.</summary>
    private void InsertIntoComposer(string text)
    {
        var at = Math.Clamp(InputCaretIndex, 0, InputText.Length);

        InputText = InputText.Insert(at, text);
        InputCaretIndex = at + text.Length;
    }

    // ── Phase dispatch ──────────────────────────────────

    [RelayCommand]
    private Task Submit() => SubmitCore(echoTyped: true);

    /// <param name="echoTyped">Whether what is being sent is also written into the transcript as a turn
    /// of the user's own. Off for the answers to a round of questions, which have just been recorded
    /// under the questions they answer — see <see cref="SendAnswers"/>. It gates the transcript and
    /// nothing else: what goes to the tool, and the clarification history it is remembered in, are the
    /// same either way.</param>
    private async Task SubmitCore(bool echoTyped)
    {
        var text = InputText.Trim();
        if (string.IsNullOrEmpty(text) || IsRunning) return;
        InputText = "";

        // Asked before anything is changed. Typing into a finished tile used to clear the transcript on
        // the spot; the + button asks before doing exactly that, and there is no reason the same act
        // should be silent because it arrived through the composer. It comes first because the pause is
        // cleared and written below, and a user who answers "no" must be left exactly as they were.
        if (CurrentPhase is GoalPhase.Goal or GoalPhase.Summary && !await ConfirmDiscardAsync())
        {
            InputText = text;

            // Said, rather than returned in silence. The refusal is usually the user answering "no",
            // which needs no explanation — but it is also what happens when there is no dialog to ask
            // in, and then nothing at all happened for no visible reason.
            if (ConfirmAction == null)
                await SayOnceAsync("This tile cannot ask whether to discard the current goal, so it " +
                                   "has kept it. Use + to start a new one.");
            return;
        }

        // Checked before the pause is touched, and that is the point of it being here rather than in
        // the switch below. An answer that is only the numbering is not going to start anything, so
        // clearing the pause for it left a stopped tile unpaused with nothing running — no Resume, no
        // run, and the only way on a second answer nobody had been asked for.
        if (CurrentPhase == GoalPhase.Clarify && GoalTranscript.IsBlankAnswer(text))
        {
            InputText = text;
            await SayOnceAsync("Answer at least one of the questions, or say what you want to change, " +
                               "before sending.");
            return;
        }

        // Answering is resuming — everywhere the composer has something to send. Leaving the pause
        // standing meant the run happened and was then thrown away at the first hand-over that asks
        // about it: a whole implementation spent on nothing.
        if (GoalTilePolicy.AnsweringResumes(CurrentPhase) && _engine.IsPaused)
        {
            _engine.IsPaused = false;
            SyncFromEngine();
        }

        try
        {
            switch (CurrentPhase)
            {
                case GoalPhase.Goal:
                case GoalPhase.Summary:
                    StartFreshGoal(text);
                    // The typed goal is its own narrowing: @ paths in it scope every tree read of the
                    // goal that starts here. After StartFreshGoal, which clears the replaced goal's
                    // scope — these belong to the one that has just started.
                    _engine.ScopePaths = LivePaths(GoalScopeFilter.Mentions(text));
                    SyncFromEngine();
                    await AddMessageAsync(GoalMessageRole.User, text, GoalPhase.Goal);

                    // Inside WorkingAsync, not before it. That is what holds IsRunning and the run's
                    // one CancellationTokenSource: outside it the tile showed the composer while git
                    // worked, and the snapshot's own timeout had no token to be cancelled through, so
                    // Pause during it did nothing.
                    await WorkingAsync(async () =>
                    {
                        await CaptureBaselineAsync();
                        await RunClarifyAsync();
                    });
                    break;

                case GoalPhase.Clarify:
                    // The answer goes back to the tool as another clarification round rather than
                    // straight to a plan. The tool decides when it has enough — it either comes back
                    // with more questions or says it needs none, at which point RunClarifyAsync plans
                    // by itself — and RunClarifyAsync stops it asking for ever.
                    //
                    // Staying in Clarify is also what makes an interrupted answer resumable: the file
                    // now says Clarify with the user's message last, which WasInterrupted reads as a
                    // run that was cut off, and what Resume then does — ask the tool again with the
                    // answer in hand — is exactly what was owed.
                    _engine.RecordClarification(text);
                    if (echoTyped)
                        await AddMessageAsync(GoalMessageRole.User, text, GoalPhase.Clarify);
                    await WorkingAsync(RunClarifyAsync);
                    break;

                case GoalPhase.Plan:
                    await AddMessageAsync(GoalMessageRole.User, text, GoalPhase.Plan);
                    if (GoalWorkflowEngine.IsApproval(text))
                    {
                        // Approved from what the tool proposed, which the engine remembers. There may be
                        // nothing to approve — an empty or failed run leaves this phase paused with no
                        // plan in it — and it used to be dug out of the transcript as "the last assistant
                        // message, whatever that was", which in that case is the clarifying questions.
                        if (!_engine.ApprovePlan())
                        {
                            await SayOnceAsync("There is no plan to approve yet. Click Resume to ask " +
                                               "for one, or describe what you want changed.");

                            // The pause was cleared on the way in, on the assumption that answering is
                            // resuming. Nothing was resumed, so it goes back — otherwise the sentence
                            // above points at a Resume button that is no longer there.
                            PauseAndWait();
                            break;
                        }

                        // The phase moves before the run starts: approving first means a crash here
                        // resumes with a plan, and moving the phase means it resumes into the
                        // implementation rather than asking to have the plan approved a second time.
                        _engine.CurrentPhase = GoalPhase.Implement;
                        SyncFromEngine();

                        await WorkingAsync(() => RunImplementReviewLoopAsync());
                    }
                    else
                    {
                        _engine.RecordClarification(text);
                        await WorkingAsync(RunClarifyAsync);
                    }
                    break;

                case GoalPhase.Implement:
                case GoalPhase.Review:
                    // Reaching here means the tile is in a working phase with nothing working: the
                    // guard at the top of this method returns while IsRunning, so the branch that said
                    // "AI is working, please wait" was unreachable — and would have been false in the
                    // one case that does arrive here. The text is handed back rather than swallowed:
                    // there is nothing here to send it to, and losing what somebody typed is its own
                    // small betrayal.
                    InputText = text;
                    await SayOnceAsync("This run is stopped. Click Resume to continue it, or + to start a new goal.");
                    break;
            }
        }
        catch (Exception ex)
        {
            await StoppedByErrorAsync("Goal workflow error", ex);
        }
    }

    // ── Workflow phases ─────────────────────────────────

    /// <summary>
    /// One clarification round, or the decision to stop having them.
    /// <para>The budget is the reason this is a method and not one line. Questions now beget questions:
    /// an answer goes back for another round, so a tool that keeps finding one more thing to ask about
    /// would keep the user answering for ever. Three rounds in, the tile plans with what it has — a
    /// plan the user can still reject, which is a better place to argue from than a fourth question.
    /// </para>
    /// </summary>
    private async Task RunClarifyAsync()
    {
        // First, before the budget is even looked at. Whatever is on screen is about to be answered by
        // a fresh set, by none, or by the tile giving up and planning — and that last path returns
        // below without ever reaching the round. Clearing after the check left a tile in Plan still
        // showing the old questions, with the plan box suppressed behind them (ShowApproval
        // stands down while ShowQuestions is up), so the only thing on screen was a set of questions
        // nobody was going to read and no way to approve the plan they had been abandoned for.
        ClearPendingQuestions();

        if (_engine.ClarifyRounds >= GoalWorkflowEngine.MaxClarifyRounds)
        {
            // Once per goal. Every rejected plan comes back through here, and the explanation is the
            // same one every time — after the second telling it is not information, it is nagging.
            if (!_clarifyBudgetReported)
            {
                _clarifyBudgetReported = true;
                await AddMessageAsync(GoalMessageRole.System,
                    $"That is {GoalWorkflowEngine.MaxClarifyRounds} rounds of questions. Planning with " +
                    "what we have — you can still reject the plan.", GoalPhase.Clarify);
            }

            _engine.CurrentPhase = GoalPhase.Plan;
            SyncFromEngine();
            await RunPlanAsync();
            return;
        }

        await RunPhaseAsync(GoalPhase.Clarify, "AI is checking the goal...",
            _engine.BuildClarifyPrompt(PromptBudget()), OnClarifyAnsweredAsync);
    }

    /// <summary>
    /// What a clarification round produced: questions to answer, or permission to get on with it.
    /// <para>Two things here are new, and both are answers to the same complaint — that the tile always
    /// asked, always exactly once, and always made the user reply before anything could be planned.
    /// The tool can now say it has nothing to ask, and the tile believes it; and a goal that is still
    /// vague can be asked about again on the next round rather than only after a plan is rejected.
    /// </para>
    /// </summary>
    private async Task OnClarifyAnsweredAsync(string answer)
    {
        var clarify = GoalResponseParser.ParseClarify(answer);
        _engine.ClarifyRounds++;

        // Prose is a legitimate answer: a tool that ignored the schema still asked something, and the
        // old behaviour — show it, wait for a reply — is exactly right for it.
        if (clarify.WasStructured && !clarify.NeedsClarification)
        {
            // Whatever it said on its way past. A round that decides the goal is clear often says why,
            // or what it is assuming, and that is the last chance to disagree before a plan is written
            // against it — so it goes in as the tool's own message, above the tile's note.
            if (GoalTranscript.Aside(clarify) is { Length: > 0 } aside)
                await AddMessageAsync(GoalMessageRole.Assistant, aside, GoalPhase.Clarify, markdown: true);

            await AddMessageAsync(GoalMessageRole.System,
                "No questions to answer — planning now.", GoalPhase.Clarify);

            _engine.CurrentPhase = GoalPhase.Plan;
            SyncFromEngine();
            await RunPlanAsync();
            return;
        }

        var questions = GoalTranscript.Questions(clarify);

        // An answer made of nothing but fence markers leaves nothing to show: the prose is empty, and
        // so is the text with the fences stripped out. It is not blank enough for the loop's own
        // "said nothing" check, which sees the backticks — so without this the transcript got an empty
        // bubble from the assistant and the history got the bare label "Tool asked: ", which the next
        // round would then be handed as a question.
        if (questions.Length == 0)
        {
            await AddMessageAsync(GoalMessageRole.System,
                $"The tool answered with nothing readable. {Capitalised(TryAgain(GoalPhase.Clarify))}",
                GoalPhase.Clarify);
            PauseAndWait();
            return;
        }

        // Into the history as well as onto the screen. The next round and the plan both read this list,
        // and without the questions in it they were handed a set of numbered answers to questions
        // nobody had written down.
        _engine.RecordClarification(questions, fromUser: false);

        // Structured questions become a block in the conversation with a box per question — so they are
        // deliberately *not* put in the transcript here as well. The block is replaced by the record of
        // itself when it is answered: one message carrying the same questions in the same order with
        // the answers under them, at the point they were asked. Writing them here too would put the
        // round on screen twice, which on a small tile is most of it.
        //
        // Prose is the other half of the rule and keeps the behaviour it always had: there is nothing
        // to build a block from, so it is a message and the composer answers it.
        if (clarify.WasStructured && clarify.Questions.Count > 0)
        {
            _engine.SetPendingQuestions(clarify.Questions);
            SyncQuestions();
            SaveStateNow();
            return;
        }

        // Prose, straight from the tool: the structured path never reaches here.
        await AddMessageAsync(GoalMessageRole.Assistant, questions, GoalPhase.Clarify, markdown: true);
    }

    private async Task RunPlanAsync() =>
        await RunPhaseAsync(GoalPhase.Plan, "AI is creating a plan...",
            _engine.BuildPlanPrompt(PromptBudget()));

    /// <summary>
    /// Runs one of the two phases the user answers, and leaves the tile saying where it now stands.
    /// <para>What each phase says when it is waiting comes from <see cref="GoalWorkflowEngine
    /// .GetPhaseLabel"/> alone. It used to be passed in as a success label and a fallback label as well,
    /// so the same four sentences existed in three places — here, in the engine, and in the summary —
    /// with nothing keeping them in step.</para>
    /// </summary>
    /// <param name="onAnswered">
    /// What to do with an answer, when adding it to the transcript is not the whole of it. Clarify uses
    /// it to read the answer, decide whether any questions actually came back, and move straight to the
    /// plan when none did. Null means the default: the answer goes in the transcript and the tile waits
    /// for the user.
    /// </param>
    private async Task RunPhaseAsync(GoalPhase phase, string runningLabel, string prompt,
        Func<string, Task>? onAnswered = null)
    {
        // The same guard the implement/review loop has, and it was missing here for the same reason it
        // was missing there: nothing between two phases asks. A pause taken after a clarification round
        // answered and before the plan is asked for arrives here with the tool about to be launched
        // anyway — the run the user just stopped, started one line later. The label goes back, because
        // this is before the tool runs and there is no cancelled run for anything else to describe.
        if (PauseRequested)
        {
            PhaseLabel = _engine.GetPhaseLabel();
            return;
        }

        // A new planning run forgets the last proposal before it starts. Without this, a plan the user
        // rejected outlived the rejection: back through Clarify to Plan, a second run that produced
        // nothing left the old plan standing, and "ok" approved the one they had just turned down.
        if (phase == GoalPhase.Plan)
            _engine.RecordProposedPlan(null);

        _engine.CurrentPhase = phase;
        SyncFromEngine();
        Working(runningLabel);

        var run = await RunAiAsync(prompt);
        switch (run.Verdict)
        {
            case GoalRunVerdict.Answered:
                if (phase == GoalPhase.Plan)
                    _engine.RecordProposedPlan(run.Text!);

                    // Said here rather than left to IsRunning going false a moment later. ProposedPlan
                    // is a plain field that notifies nobody, and the phase does not move when a plan
                    // arrives, so the plan box appeared by luck — luck that runs out the day
                    // anything else sets a plan.
                    RefreshAsk();

                if (onAnswered != null)
                    await onAnswered(run.Text!);
                else
                    await AddMessageAsync(GoalMessageRole.Assistant, run.Text!, phase, markdown: true);

                // Read from the engine afterwards, not from the phase this method was called with: the
                // handler above may have moved the tile on, and labelling it with the phase it has just
                // left would say "answer the questions above" over a plan.
                PhaseLabel = _engine.GetPhaseLabel();
                break;

            default:
                await HandleNonAnswerAsync(run.Verdict, phase);
                break;
        }
        // No save here: every branch above has already written, through AddMessageAsync or
        // SyncFromEngine, and PhaseLabel is not part of the state.
    }

    /// <param name="finishInterruptedIteration">
    /// Start by finishing the attempt that was already under way rather than opening a new one. Set
    /// when resuming: the interrupted attempt has already been paid for out of the budget of
    /// <see cref="GoalWorkflowEngine.MaxIter"/>, and charging for it twice would mean a run that is
    /// stopped and continued a few times gives up while the user still has attempts left.
    /// </param>
    /// <param name="startAtReview">
    /// Skip straight to the review on the first lap. Set when resuming a run that was interrupted after
    /// its implementation had finished: the tool's answer is already in the transcript and its changes
    /// are already on disk, so running the implementation again asks the tool to redo work it can see
    /// it has done — usually a no-op, sometimes a duplicate, and always in the user's own worktree.
    /// </param>
    private async Task RunImplementReviewLoopAsync(bool finishInterruptedIteration = false, bool startAtReview = false)
    {
        try
        {
            var finishing = finishInterruptedIteration;
            var skipImplement = startAtReview;

            // Falling out of the while is the budget running out, so that is what this starts as. Every
            // other way out of the loop sets it on the way past.
            var stopReason = GoalStopReason.BudgetSpent;

            // What the last review left standing between this goal and "met", carried out of the loop so
            // the summary can say it. It used to be worked out only where there was a next attempt to
            // announce, so the one summary that most needs it — the budget running out — was the one
            // place it was never computed.
            string? outstanding = null;

            // How many tool calls the *implementation* was refused, carried beside the stop for the same
            // reason: the summary's NoChange sentence names the attempt, and the unchanged-tree path runs
            // a review (and a salvage) after the implementation before the summary is built — each of
            // those runs rewrites _lastRunDenials with its own refusals, so reading the field at summary
            // time could put the permission sentence under a review that was refused its build over a
            // tree that already held the work.
            var implementationDenials = 0;

            while (GoalLoopPolicy.NextAttempt(_engine.IterationCount, _engine.MaxIter, finishing) is { } attempt)
            {
                _engine.IterationCount = attempt;
                OnPropertyChanged(nameof(RunStage));
                finishing = false;

                // Read fresh on every lap, deliberately: what the panel says is what is in force, from
                // the next question the loop asks. Half of this used to be captured before the loop
                // while the attempt budget was read live from the engine, so raising the attempts
                // mid-run worked and raising the tolerated warnings did nothing — the same panel, two
                // answers. Nothing is decided mid-lap, so a change never lands between an
                // implementation and the review that judges it.
                var criteria = _engine.Criteria;

                var treeBeforeImplement = WorktreeSnapshot.Unreadable;

                // The working tree goes into both prompts, and into every implement prompt rather than
                // only into those following a review: it is the only state that survives the tool's
                // process, so on a resume after an interrupted implementation it is the one thing
                // telling the tool that half of its own work is already applied.
                if (skipImplement)
                {
                    skipImplement = false;
                }
                else
                {
                    // What this implementation was told before it ran. The no-change stop below turns
                    // on it: an attempt that wrote nothing is a dead end only when it had the review's
                    // findings in front of it and wrote nothing anyway.
                    var feedbackBeforeImplement = _engine.LastReviewFeedback;

                    var impl = await RunLoopPhaseAsync(
                        GoalPhase.Implement,
                        $"AI is implementing (attempt {attempt}/{_engine.MaxIter})...",
                        (tree, _) => _engine.BuildImplementPrompt(tree, PromptBudget()));

                    if (impl is not { } done) return;
                    treeBeforeImplement = done.Tree;

                    // Filed before anything else can go wrong with this lap. The tool's closing lines
                    // say what it changed and what it decided against, and until now they went into the
                    // transcript and died there — prompts are built from the engine's fields alone, and
                    // the transcript never comes back. So attempt 2 rediscovered the dead end attempt 1
                    // had backed out of, and paid an attempt for it.
                    _engine.RecordAttempt(attempt, done.Text);

                    // The implementation is done and only the review is owed, so the phase moves —
                    // unconditionally, and here rather than inside the pause below. Everything from
                    // this point on can be interrupted, and leaving Implement standing through it
                    // meant Resume ran the whole implementation again over a worktree that already had
                    // its changes.
                    _engine.CurrentPhase = GoalPhase.Review;
                    SyncFromEngine();

                    if (PauseRequested)
                    {
                        PhaseLabel = _engine.GetPhaseLabel();
                        return;
                    }

                    // Two short git processes against a lap that costs minutes of AI, and they pay for
                    // themselves the moment this fires: there is no sense reviewing a change that was
                    // never made — but the stop is not the verdict. Two runs leave the same empty
                    // worktree and want different sentences: the tool refused the work and was never
                    // allowed to touch a file (the summary below names the permission mode), or it did
                    // the work's own arithmetic and concluded the tree already held it — measured live
                    // 2026-09-01, a run answered "everything the plan asked for is already here,
                    // odrzucam przepisywanie od zera", changed nothing, and the tile stopped with a
                    // sentence about a dead end over an account of a goal that was done. So the empty
                    // worktree goes to the reviewer once, to arbitrate what the attempt only claimed:
                    // met, and the run says so; not met, and the dead-end sentence carries what the
                    // reviewer found outstanding. A refused run skips the call entirely, because a
                    // review of work nobody was allowed to do answers what the summary already says.
                    //
                    // Whether the loop ends here is then the question the block below the review asks:
                    // it does, unless that review has just produced findings the implementation was
                    // never given, in which case the next attempt is a different question and is worth
                    // an attempt rather than a button.
                    if (await ImplementationChangedNothingAsync(treeBeforeImplement))
                    {
                        // Captured before anything can overwrite it: the review below runs its own AI
                        // call, and the summary's NoChange sentence is about the implementation.
                        implementationDenials = _lastRunDenials;

                        if (implementationDenials > 0)
                        {
                            stopReason = GoalStopReason.NoChange;
                            break;
                        }

                        var verdict = await ReviewUnchangedTreeAsync(criteria);
                        if (verdict is null) return;
                        (stopReason, outstanding) = verdict.Value;

                        // Unless that review has just said something the implementation never heard.
                        //
                        // The whole argument for stopping here is that the same prompt over the same
                        // tree gets the same nothing — and it holds only while the prompt *is* the
                        // same. The commonest way into this stop is an attempt that opened over a tree
                        // already holding the work (a goal detected from uncommitted changes, or a plan
                        // written against them): the tool reads the tree, answers "this is already
                        // done", writes nothing, and the review that follows is the first thing in the
                        // run to name a defect. Stopping there put a button in front of the user whose
                        // only job was to say "yes, carry on" — measured twice, on two unrelated goals,
                        // and Continue fixed the finding on the next attempt both times.
                        //
                        // So: new findings and budget left means the next attempt is a different
                        // question, and the loop asks it. Bounded by construction — an attempt that
                        // again writes nothing comes back here with the feedback it was given, which is
                        // now the feedback it has, and stops as the dead end it is.
                        // Structured only, the rule RepeatsPrevious follows and for the same reason:
                        // an unstructured review's "feedback" is its own prose, which differs from the
                        // last one by a comma and would hand the loop a fresh question every lap.
                        if (stopReason == GoalStopReason.NoChange
                            && _engine.LastReviewFingerprint is not null
                            && _engine.LastReviewFeedback is { Length: > 0 } freshFeedback
                            && freshFeedback != feedbackBeforeImplement
                            && GoalLoopPolicy.NextAttempt(_engine.IterationCount, _engine.MaxIter, false)
                                is { } afterNoChange)
                        {
                            // The pause is honoured the way the loop's own review honours it: what is
                            // owed next is an implementation, so the phase is moved to it and the run
                            // is left resumable rather than summarised over a stop the user interrupted.
                            if (PauseRequested)
                            {
                                _engine.IterationCount = afterNoChange;
                                _engine.CurrentPhase = GoalPhase.Implement;
                                SyncFromEngine();
                                PhaseLabel = _engine.GetPhaseLabel();
                                return;
                            }

                            await AddMessageAsync(GoalMessageRole.System,
                                $"The attempt changed no files, and the review found {outstanding}. " +
                                $"Re-implementing with those findings (attempt {afterNoChange})...",
                                GoalPhase.Review);
                            continue;
                        }

                        break;
                    }
                }

                var reviewRun = await RunLoopPhaseAsync(
                    GoalPhase.Review,
                    "AI is reviewing changes...",
                    // `isScoped` rather than `scoped`: the latter is a C# modifier keyword in a lambda
                    // parameter list and will not compile there.
                    (tree, isScoped) => _engine.BuildReviewPrompt(tree, isScoped, PromptBudget()),
                    // What the tree is measured from decides what the prompt may call it, so both come
                    // from DiffBase and neither is tracked separately. A local flag set from
                    // startAtReview said the same thing for one lap of one call: it was right on the
                    // first review of a detect-and-run and wrong on every review after a Resume, where
                    // the loop is entered afresh with startAtReview false while the goal is still about
                    // work that predates the baseline. The prompt would then have headed a HEAD-wide
                    // diff "the changes that were just made", which is the lie this pair exists to
                    // prevent.
                    scoped: DiffBase != null,
                    // The raw answer is not what goes in the transcript. It is read first and written
                    // back as a list of findings, because the prose around a JSON block says the same
                    // things at greater length and printing both means reading every review twice.
                    addMessage: false);

                if (reviewRun is not { } reviewed) return;

                var review = await SalvagedReviewAsync(GoalResponseParser.ParseReview(reviewed.Text));
                ShowFindings(review);
                // The head as text, the findings as findings. They used to be one string, so the one
                // part of the transcript arranged to be scanned was also the one part with no colour
                // in it: a blocker and a suggestion were the same grey, three lines apart.
                await AddMessageAsync(GoalMessageRole.Assistant,
                    GoalTranscript.ReviewHead(review, criteria.RequireGoalMet), GoalPhase.Review,
                    findings: GoalTranscript.InOrder(review.Findings));

                if (GoalCompletionPolicy.IsMet(review, criteria))
                {
                    _engine.ClearReviewFeedback();
                    stopReason = GoalStopReason.Met;
                    break;
                }

                // Only the errors and warnings go back, and only as findings. The whole review used to,
                // nits and prose included, so an attempt could be spent renaming a variable while the
                // null dereference above it stayed exactly where it was.
                _engine.RecordReviewFeedback(GoalTranscript.Feedback(review));
                outstanding = GoalCompletionPolicy.WhyNotMet(review, criteria);

                var repeatedItself = GoalCompletionPolicy.RepeatsPrevious(
                    review, _engine.LastReviewFingerprint);
                _engine.LastReviewFingerprint = review.WasStructured ? review.Fingerprint() : null;
                if (repeatedItself)
                {
                    stopReason = GoalStopReason.NoProgress;
                    break;
                }

                if (PauseRequested)
                {
                    // The review has run and asked for another pass, so what is owed is the next
                    // implementation. Stopping while still in Review had Resume re-run the review it
                    // had just recorded: an AI run spent on an unchanged tree, and the same verdict
                    // twice in the transcript.
                    //
                    // With the budget spent there is no next attempt to move to, and nothing left to
                    // pause either — this review was the last thing the run had to do. It is summarised
                    // rather than left paused, because a Resume there could only re-run the review it
                    // had just finished, for one AI run and the same verdict twice, before summarising
                    // anyway.
                    if (GoalLoopPolicy.NextAttempt(_engine.IterationCount, _engine.MaxIter, false) is not { } pending)
                    {
                        await ShowSummaryAsync(GoalStopReason.BudgetSpent, outstanding);
                        return;
                    }

                    _engine.IterationCount = pending;
                    _engine.CurrentPhase = GoalPhase.Implement;
                    SyncFromEngine();
                    PhaseLabel = _engine.GetPhaseLabel();
                    return;
                }

                // The next attempt's number comes from the same rule that decides whether there is one,
                // rather than from a second copy of the arithmetic that could disagree with it. And it
                // now says what is actually outstanding: "review found issues" was equally true of a run
                // blocked by one warning and of one blocked by nine errors and a failing build.
                if (GoalLoopPolicy.NextAttempt(_engine.IterationCount, _engine.MaxIter, false) is { } next)
                    await AddMessageAsync(GoalMessageRole.System,
                        $"Not done: {outstanding}. Re-implementing (attempt {next})...", GoalPhase.Review);
            }

            await ShowSummaryAsync(stopReason, outstanding, implementationDenials);
        }
        finally
        {
            SaveStateNow();

            // The tree has almost certainly moved, and the detect buttons are about to be offered again
            // in Summary. Asking now is what stops them being offered over a tree that no longer has
            // anything in it, or hidden over one that does.
            RefreshDetectAvailability();
        }
    }

    /// <summary>
    /// Whether the implementation left the working tree exactly as it found it — the tool did nothing,
    /// and the same prompt against the same tree gets the same nothing.
    /// <para>A cancellation answers <c>false</c>: a pause is not evidence about the implementation,
    /// and the pause is handled at the next hand-over anyway.</para>
    /// </summary>
    private async Task<bool> ImplementationChangedNothingAsync(WorktreeSnapshot treeBeforeImplement)
    {
        try
        {
            // Both reads have to have worked. Comparing the *text* alone answered yes for a workspace
            // that is not a repository at all — where every read produces the same nothing — so every
            // goal ended after one attempt, told the user the implementation had changed nothing, and
            // was confidently wrong.
            return (await ReadWorktreeForComparisonAsync()).ProvablyUnchangedFrom(treeBeforeImplement);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Everything that has to happen when this tile starts over on a new goal.
    /// <para>One method because there are three ways in — the composer in Goal or Summary, the + button,
    /// and detecting a goal from the working tree — and they were four identical lines each. That is
    /// how <c>_clarifyBudgetReported</c> came to be reset in two of them and not the third, so a fresh
    /// goal started with + never explained why it was not being asked anything.</para>
    /// </summary>
    private void StartFreshGoal(string goal)
    {
        Messages.Clear();
        _clarifyBudgetReported = false;

        // The flag belongs to the goal that has just been replaced. Left standing, a tile that committed
        // once never offered again for the rest of its life.
        _committed = false;

        // StartNewGoal empties the counts; this only has to take the badges down afterwards. Clearing
        // them here as well was the same reset written twice, in two files, with nothing keeping the
        // pair in step.
        _engine.StartNewGoal(goal);

        // StartNewGoal keeps only the images the new goal still refers to, so a marker the user pasted
        // and then left in the composer — + pressed, or a goal detected from the working tree — now
        // stands for nothing. It goes with them: sent as it is, the tool is handed [Image #1] with no
        // path anywhere in the prompt, which is exactly what AttachImage refuses to do when a save
        // fails. Submit has already emptied the composer by the time it gets here, so this is the two
        // routes that have not.
        InputText = GoalImageMarker.DropMarkersExcept(
            InputText, [.._engine.AttachedImages.Select(image => image.Index)]);

        // The panel is redrawn because StartNewGoal may have put the attempts field back to what the
        // user set, undoing what Continue wrote for the goal that has just been replaced.
        Criteria.Reload();
        ShowBadges();

        // StartNewGoal drops the pending questions; this takes them off the screen. They belonged to
        // the goal that has just been replaced, and answering them would file an answer against a
        // question this tile is no longer asking.
        SyncQuestions();
    }

    /// <summary>The severities in declaration order, which is the order the saved counts are stored in.
    /// Read once: <c>Enum.GetValues</c> allocates.</summary>
    private static readonly GoalSeverity[] GoalSeverities = Enum.GetValues<GoalSeverity>();

    /// <summary>The badges in the status strip. Set on every review, including one that found nothing,
    /// so a clean attempt does not keep showing the counts from the one before it.</summary>
    private void ShowFindings(GoalReviewResult review)
    {
        // Counted into the engine rather than straight onto the strip, so the numbers are saved with
        // everything else and come back with the tile. A run paused mid-review returns with that review
        // still standing in the transcript, and the strip summarising it used to go blank.
        // By position in Enum.GetValues, and read back the same way — never by casting to int. The
        // array is written to disk, and a severity given an explicit value later would make the cast
        // an index into somewhere else entirely.
        _engine.LastReviewCounts = GoalSeverities.Select(review.Count).ToArray();
        ShowBadges(review.Findings);
    }

    /// <param name="findings">
    /// The review the counts came from, when there is one in hand. There is not on a restore or after
    /// a new goal, and the review that is still standing in the transcript is then the one they belong
    /// to — which is also why this cannot simply always read the transcript: the strip is set before
    /// the review is added to it, so doing that would show every badge over the findings of the review
    /// before.
    /// </param>
    private void ShowBadges(IReadOnlyList<GoalFinding>? findings = null)
    {
        // Whatever was open belonged to the review being replaced. Leaving it up would have the dialog
        // outlive its own badge: the strip would say 2E from the new review and the dialog would still
        // be listing the errors of the one before it, with no way for the reader to tell.
        CloseFindings();
        Badges.Clear();

        // Positional, and only trusted at exactly the right length. The array is saved to disk, so a
        // file written before a severity existed would otherwise be read against the new ordinals and
        // put every count beside the wrong letter — a blocker reported as an error. A length that does
        // not match is from a different version of this enum: drop it, and the strip is blank until the
        // next review, which is a truth rather than a wrong number.
        var counts = _engine.LastReviewCounts;
        if (counts.Length != GoalSeverities.Length) return;

        findings ??= Messages.LastOrDefault(m => m.HasFindings)?.Findings;

        for (var i = 0; i < GoalSeverities.Length; i++)
            if (counts[i] > 0)
                Badges.Add(new GoalBadge
                {
                    Severity = GoalSeverities[i],
                    Count = counts[i],
                    // Filtered here rather than trusted to arrive grouped: the saved counts and a
                    // transcript from an older build are two records of one review, and a mismatch
                    // between them must come out as a badge that shows less than it counts, never as
                    // one severity's popup listing another's findings.
                    Findings = findings?.Where(f => f.Severity == GoalSeverities[i]).ToArray() ?? [],
                });
    }

    /// <param name="autoCommit">
    /// Whether the "commit the work when done" switch applies to this ending.
    /// <para>False for a review on its own. That button says it judges the tree and changes nothing,
    /// and a promise like that cannot have commits behind it — least of all on the detect paths, where
    /// what would be committed is the whole of the uncommitted tree. The switch means "when the run is
    /// done", and an inspection is not a run. The Commit button is still in the summary, where pressing
    /// it is the consent this path does not have.</para>
    /// </param>
    /// <param name="wroteChanges">
    /// False for a run that judged the tree and changed nothing, which is what decides whether the
    /// closing snapshot <em>moves</em>. It does not decide whether one is taken at all: a run with no
    /// end recorded yet takes one either way, or the first review of a goal detected from the working
    /// tree is left with no upper bound at all.
    /// <para><b>Not the same question as <paramref name="autoCommit"/>, and conflating them is the bug
    /// this parameter exists for.</b> That one is about consent; this one is about what happened. A
    /// Re-review taken through the shared summary moved the run's upper end onto the tree <em>as it is
    /// now</em> — and the reason anybody presses Re-review, said in <see cref="ReReviewAsync"/>'s own
    /// remarks, is that something has just been fixed by hand next door, so that tree is known to hold
    /// somebody else's work. The run then claimed it: with the end moved past the other tile's commit,
    /// its files landed in <c>changed</c> and nothing at all in <c>touchedSince</c>. That is the
    /// "three tiles, one commit" failure the closing snapshot was added to prevent, reintroduced by
    /// putting the snapshot on the one path that writes nothing.</para>
    /// <para>Leaving the end where the last implementation put it is also what makes the dialog right
    /// afterwards: the other tile's files fall into <c>touchedSince</c>, are held back, and are named
    /// under the reason they were held.</para>
    /// </param>
    /// <param name="implementationDenials">
    /// How many tool calls the run's last <em>implementation</em> was refused — captured where the stop
    /// was decided, not read from <c>_lastRunDenials</c> here. The unchanged-tree path runs a review
    /// (and a salvage) after the implementation and before this summary, and every AI run rewrites that
    /// field with its own refusals; the permission sentence is about the attempt, so it is handed the
    /// attempt's count. Only the NoChange sentence reads it.
    /// </param>
    private async Task ShowSummaryAsync(GoalStopReason reason, string? outstanding = null,
        int implementationDenials = 0, bool autoCommit = true, bool wroteChanges = true)
    {
        // Kept with the goal, not merely said in the transcript: the Summary offers Continue for one of
        // the four reasons only, and a tile reopened tomorrow has nothing to read that back out of.
        _engine.LastStopReason = reason;

        // The pause goes with it. Every route into a summary except a clean finish arrives with a
        // pause outstanding — the budget spent at a pause, a run stopped and then summarised — and a
        // Summary that still calls itself paused labels the tile "Paused. Click Resume to continue."
        // over a Resume that has nothing to do, and keeps saying so after a restart.
        _engine.IsPaused = false;
        _engine.CurrentPhase = GoalPhase.Summary;
        SyncFromEngine();

        // Before the summary is written and before anything offers to commit, because it is the upper
        // end of what this run may claim: the tree as *this* run left it. Here rather than at the call
        // sites, so that every route into a summary bounds the run — a Continue that takes three more
        // attempts moves the end on with them, and a run that stopped early still knows where it
        // stopped, which is exactly when the user reaches for Commit.
        //
        // Except a run that wrote nothing: see `wroteChanges`. Moving the end there hands the run
        // whatever anybody else has done since the last implementation.
        //
        // **Unless there is no end to preserve.** `RunReviewOnlyAsync` serves two buttons: Re-review,
        // which follows an implementation and must keep that implementation's end, and Detect & Review,
        // where nothing ran before it and `EndRef` is still null. Refusing on both left the second one
        // unbounded — `end` falls back to the tree as it is *now*, `touchedSince` compares that tree
        // with itself and comes out empty, and on the detect path `LeftAlone` is empty by design — so
        // every dirty file in the workspace, another tile's included, was offered as this goal's work
        // with nothing held back. The two conditions are different questions: `wroteChanges` asks
        // whether to *move* the end, and this asks whether there is one at all.
        if (wroteChanges || _engine.EndRef is null) await CaptureEndAsync();

        // The label too, not only whether the button is there: it is the ceiling less the attempts
        // already spent, and both have just moved.
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(ContinueLabel));

        PhaseLabel = _engine.GetPhaseLabel();

        // The denials go with it only where they explain something: every other stop happened for a
        // reason of its own, and mentioning a refused tool call beside "the criteria were met" would
        // read as a problem with a run that had none.
        var summary = GoalCompletionPolicy.Summarise(
                          reason, _engine.IterationCount, outstanding,
                          reason == GoalStopReason.NoChange ? implementationDenials : 0)
                      + "\nType a new goal, or start a fresh one with +.";

        await AddMessageAsync(GoalMessageRole.System, summary, GoalPhase.Summary,
            isRunSummary: true);

        RefreshFinishedRunActions();

        // Only where the user has said so. Otherwise CanCommit puts the button in the summary and the
        // same path runs when it is pressed — the switch decides who starts this, never whether the
        // commit is confirmed, which it always is.
        if (autoCommit && _engine.Criteria.CommitWhenDone && MayCommit)
            await CommitWorkAsync();
    }

    // ── One review, on its own ──────────────────────────

    /// <summary>
    /// Reviews the working tree once after an implementation that changed no files, so the stop carries
    /// a verdict instead of only a dead end.
    /// </summary>
    /// <remarks>
    /// <para>The tree is read <b>unscoped</b>, for the reason <see cref="RunReviewOnlyAsync"/> states:
    /// the work being judged predates this run — the attempt just ended changed nothing, so scoping to
    /// a baseline taken moments ago would answer with an empty diff.</para>
    /// <para>Null means the review was paused, and the caller must leave the pause standing rather than
    /// summarise over it — the same contract the loop's own review runs under.</para>
    /// </remarks>
    private async Task<(GoalStopReason Reason, string? Outstanding)?> ReviewUnchangedTreeAsync(
        GoalCompletionCriteria criteria)
    {
        var reviewRun = await RunLoopPhaseAsync(
            GoalPhase.Review,
            "AI is reviewing the working tree...",
            (tree, isScoped) => _engine.BuildReviewPrompt(tree, isScoped, PromptBudget()),
            addMessage: false,
            scoped: false);

        if (reviewRun is not { } reviewed) return null;

        var review = await SalvagedReviewAsync(GoalResponseParser.ParseReview(reviewed.Text));
        ShowFindings(review);
        await AddMessageAsync(GoalMessageRole.Assistant,
            GoalTranscript.ReviewHead(review, criteria.RequireGoalMet), GoalPhase.Review,
            findings: GoalTranscript.InOrder(review.Findings));

        if (GoalCompletionPolicy.IsMet(review, criteria))
            return (GoalStopReason.Met, null);

        // Carried for Continue, exactly as RunReviewOnlyAsync carries it and for the same reason:
        // without it the implementation Continue starts would begin over a tree that has just been
        // reviewed, knowing nothing of what was found — and this is the one path where that review is
        // the only new thing there is, since the tree itself did not move.
        //
        // The fingerprint goes with it so that a continued attempt which *does* write something and is
        // then reviewed to the same conclusion stops as NoProgress. It does not close the other door:
        // a continued attempt that again writes nothing comes back here, where nothing asks
        // RepeatsPrevious, and stops as NoChange a second time with Continue still on offer. Measured,
        // not assumed. Left that way deliberately — the escalation would have to tell "this stop
        // repeats the last one" from "this review repeats the last one", and the fingerprint alone
        // cannot: on the *first* no-change stop it usually matches the previous lap's review already,
        // since the tree did not move, so escalating on it would replace the one fact the user needs
        // ("the agent changed no files") with a sentence about reviews. Each repeat is a press of a
        // button, in front of the user, against a sentence identical to the one above it.
        _engine.RecordReviewFeedback(GoalTranscript.Feedback(review));
        _engine.LastReviewFingerprint = review.WasStructured ? review.Fingerprint() : null;

        return (GoalStopReason.NoChange, GoalCompletionPolicy.WhyNotMet(review, criteria));
    }

    /// <summary>
    /// Judges the working tree against the goal, once, and changes nothing.
    /// </summary>
    /// <remarks>
    /// <para><b>Deliberately not the loop with a flag on it.</b>
    /// <see cref="RunImplementReviewLoopAsync"/> is the most bug-dense thing in this tile and every
    /// flag added to it has been added after its own failure; all of its complexity — the attempt
    /// budget, the two mechanical stops, the attempt log, resuming — is about <em>iterating</em>, and a
    /// single review has none of it. What it does share is the prompt, which is where the SOLID
    /// switches and the health checks live, so this is held to exactly the rules a review inside the
    /// loop is.</para>
    /// <para>The tree is read <b>unscoped</b>. The work being judged predates this run — that is the
    /// whole point — so scoping it to a baseline taken moments ago would answer with an empty diff.
    /// </para>
    /// <para>The feedback is recorded even though nothing will read it here. Continue is offered
    /// afterwards, and without this its first implement prompt would start over a tree that had just
    /// been reviewed with no idea what the review said.</para>
    /// </remarks>
    private async Task<bool> RunReviewOnlyAsync(string? guideline = null,
        IReadOnlyList<string>? onlyPaths = null)
    {
        var criteria = _engine.Criteria;

        // What was committed was committed against the tree as it was then. This is a new look at a
        // tree that has changed since — that is the whole reason the button exists — so the offer is
        // open again. If there is genuinely nothing left, the scope comes back empty and says so.
        _committed = false;

        _engine.CurrentPhase = GoalPhase.Review;
        SyncFromEngine();

        var run = await RunLoopPhaseAsync(
            GoalPhase.Review,
            "AI is reviewing the working tree...",
            (tree, isScoped) => _engine.BuildReviewPrompt(tree, isScoped, PromptBudget(), guideline),
            addMessage: false,
            scoped: false,
            // Scoped to this one review, never to the session: a narrowing typed beside Re-review is
            // the words of one look at the tree, and a Continue that came after must not inherit it.
            onlyPaths: onlyPaths);

        // False is a review that never ran — paused before it started — and the one answer a caller
        // needs to know whether the words it handed over were spent.
        if (run is not { } reviewed) return false;

        var review = await SalvagedReviewAsync(GoalResponseParser.ParseReview(reviewed.Text));
        ShowFindings(review);
        await AddMessageAsync(GoalMessageRole.Assistant,
            GoalTranscript.ReviewHead(review, criteria.RequireGoalMet), GoalPhase.Review,
            findings: GoalTranscript.InOrder(review.Findings));

        var met = GoalCompletionPolicy.IsMet(review, criteria);
        if (met)
        {
            _engine.ClearReviewFeedback();
        }
        else
        {
            // Carried for Continue, which is offered next: without it the first implementation would
            // start over a tree that has just been reviewed, knowing nothing of what was found.
            _engine.RecordReviewFeedback(GoalTranscript.Feedback(review));
            _engine.LastReviewFingerprint = review.WasStructured ? review.Fingerprint() : null;
        }

        await ShowSummaryAsync(
            met ? GoalStopReason.Met : GoalStopReason.Reviewed,
            met ? null : GoalCompletionPolicy.WhyNotMet(review, criteria),
            autoCommit: false,
            // This button judges the tree and changes nothing, so the run's upper end stays where the
            // last implementation left it. Both flags are false here for different reasons, which is
            // why they are two flags.
            wroteChanges: false);

        RefreshFinishedRunActions();
        return true;
    }

    /// <summary>
    /// Tells the summary's bar of actions to look again.
    /// </summary>
    /// <remarks>
    /// The three move together — every one of them is derived from the same phase, the same review
    /// counts and the same baseline — and they were raised as a block in five places, in two different
    /// orders and, in one of them, at two different indentations. A fourth action added to that bar
    /// would have had to find all five.
    /// </remarks>
    private void RefreshFinishedRunActions()
    {
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(CanReReview));
        OnPropertyChanged(nameof(ShowResume));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(HasFinishedRunActions));
    }

    // ── Committing the run's own work ───────────────────

    /// <summary>Set once the commits are made, so the offer does not stand over a repository that has
    /// already taken them. Not persisted: a reopened tile reads it from the tree, where a run whose
    /// work is committed has nothing left to commit and the scope comes back empty.</summary>
    private bool _committed;

    /// <summary>
    /// Whether there is anything to offer committing.
    /// </summary>
    /// <remarks>
    /// <para><b>Zero blockers and zero errors</b>, rather than the goal being met. A run can stop with
    /// the budget spent over three warnings and still have produced work worth keeping, and the
    /// alternative — offering this only for a clean finish — hides the button in exactly the case where
    /// the user most wants to look at what happened and decide for themselves. Warnings and suggestions
    /// do not stand in the way; they are named in the dialog instead, which is where a judgement about
    /// them belongs.</para>
    /// <para>A review has to have <em>run</em>. With no counts at all nothing has looked at this work,
    /// and "no errors" would be a statement about an examination that never happened.</para>
    /// <para>A baseline is required, and that is not a technicality: without one there is no way to
    /// tell this run's changes from the user's, and committing on a guess is the failure the whole of
    /// <see cref="GoalBaseline"/> exists to make impossible.</para>
    /// </remarks>
    public bool CanCommit => MayCommit && !IsRunning;

    /// <summary>
    /// The conditions themselves, without asking whether the tile is busy.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="CanCommit"/> because the two callers ask different questions and one of
    /// them cannot use the other's answer. The automatic path runs from inside
    /// <see cref="ShowSummaryAsync"/>, which is itself inside the loop's <c>WorkingAsync</c> — so
    /// <c>IsRunning</c> is true there by construction, and gating on it made the whole feature
    /// unreachable: the switch never fired, and the button was evaluated once at that same moment and
    /// never asked again. The button keeps the busy check, because a control offered mid-run is one that
    /// starts a second AI call over a working tree the first is still writing to.
    /// </remarks>
    private bool MayCommit =>
        CurrentPhase == GoalPhase.Summary
        && !_committed
        && _engine.BaselineRef is { Length: > 0 }
        && _engine.LastReviewCounts.Length == GoalSeverities.Length
        && Count(GoalSeverity.Blocker) == 0
        && Count(GoalSeverity.Error) == 0;

    private int Count(GoalSeverity severity) =>
        _engine.LastReviewCounts.Length == GoalSeverities.Length
            ? _engine.LastReviewCounts[Array.IndexOf(GoalSeverities, severity)]
            : 0;

    /// <summary>
    /// The button's way in, which is the only one that has to start a run of its own.
    /// </summary>
    /// <remarks>
    /// <see cref="CommitWorkAsync"/> deliberately does <b>not</b> wrap itself in <c>WorkingAsync</c>:
    /// the automatic path is already inside the loop's, and nesting one would dispose the outer run's
    /// <c>CancellationTokenSource</c> and clear <c>IsRunning</c> underneath it — leaving the loop that
    /// called it running with a dead token and a tile claiming to be idle.
    /// </remarks>
    [RelayCommand]
    private Task CommitWork() => CanCommit ? WorkingAsync(CommitWorkAsync) : Task.CompletedTask;

    /// <summary>
    /// Asks the tool how to divide this run's work into commits, asks the user whether to make them,
    /// and makes them.
    /// </summary>
    /// <remarks>
    /// <b>Wrapped to the end.</b> The inner catches cover the commits themselves; this one covers
    /// everything around them — the scope, the AI call, the dialog, a message that could not be
    /// written. Reached from two places, and neither has anything behind it: the button's
    /// <c>WorkingAsync</c> has a <c>finally</c> and no <c>catch</c>, and the automatic path runs inside
    /// the loop, where an escaping exception ends a run that had already finished its work. Both then
    /// arrive at the dispatcher's unhandled hook, which is a crash rather than a sentence — over an
    /// offer to commit.
    /// </remarks>
    private async Task CommitWorkAsync()
    {
        if (_engine.BaselineRef is not { Length: > 0 } baseline) return;

        try
        {
            Working("Working out what this run changed...");

            var committer = new GoalCommitter(_workingDirectory, GitPath());
            var scope = await committer.ScopeAsync(baseline, _engine.EndRef,
                _cts?.Token ?? CancellationToken.None, _engine.ReviewsExistingWork);

            if (!scope.Readable)
            {
                // Not the same as an empty scope, and said differently: this is git failing to answer,
                // which the user can look into. Reported as "nothing to commit" it would read as a
                // statement about their work.
                await AddMessageAsync(GoalMessageRole.System,
                    "Git could not say what this run changed, so nothing was committed. The log has "
                    + "what it printed.", GoalPhase.Summary);
                return;
            }

            if (!scope.HasWork)
            {
                // Through the plan, so the sentence names the files it held back and under which of the
                // two reasons. Said flat, it reads as a claim about the user's work rather than an
                // account of what happened to the run's.
                await AddMessageAsync(GoalMessageRole.System,
                    GoalCommitPlan.Nothing(scope), GoalPhase.Summary);
                return;
            }

            Working("Working out how to divide the changes into commits...");

            var run = await RunAiAsync(_engine.BuildCommitPlanPrompt(scope.Files, PromptBudget()));

            // A tool that could not answer does not end this. The work has just been reviewed and is
            // sitting in the tree; one honest commit of all of it is worth more than silence, and it is
            // the outcome the sweep in GoalCommitPlan already produces for the files a plan forgot.
            // Not on a cancellation, which is the user saying stop — the one verdict that must not be
            // answered by doing something they did not ask for.
            if (run.Verdict == GoalRunVerdict.Cancelled) return;

            // Held against the scope rather than trusted. A path the tool invented — or one it copied
            // out of the diff from the user's own parallel work — would put somebody else's change
            // into a commit claiming to be about this goal.
            var planned = run.Verdict == GoalRunVerdict.Answered
                ? GoalResponseParser.ParseCommitPlan(run.Text)
                : [];
            var commits = GoalCommitPlan.Sound(planned, scope);

            // The plan was unusable — no answer, or nothing in it this run may touch. Everything in
            // scope, in one commit, rather than nothing: this is also the way out of a file list too
            // long for the tool's command line, which no amount of asking again will shorten.
            if (commits.Count == 0)
                commits = GoalCommitPlan.SweepAll(scope);

            if (commits.Count == 0)
            {
                await AddMessageAsync(GoalMessageRole.System,
                    "The tool did not come back with a usable set of commits, so nothing was committed.",
                    GoalPhase.Summary);
                return;
            }

            // Always, and never skipped by the switch on the panel. The switch says who starts this;
            // this is the last point at which a person sees what is about to enter their history, and
            // it is where the warnings nobody fixed get said out loud — the run stopped with them
            // outstanding, and a commit is exactly the moment to decide whether that is all right.
            var confirm = ConfirmAction;
            if (confirm == null)
            {
                await SayOnceAsync("This tile cannot ask whether to commit, so it has not.");
                return;
            }

            if (!await confirm(GoalCommitPlan.Describe(commits, scope, Count(GoalSeverity.Warning),
                    Count(GoalSeverity.Suggestion), _engine.ReviewsExistingWork)))
                return;

            Working("Committing...");

            try
            {
                var made = await committer.CommitAsync(commits, _cts?.Token ?? CancellationToken.None);
                _committed = made > 0;
                await AddMessageAsync(GoalMessageRole.System,
                    GoalCommitPlan.Made(commits, made, scope), GoalPhase.Summary);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoalCommitFailure stopped)
            {
                // Half of the plan is in the history and half is not, which is the one commit outcome
                // the user has to be able to see in full: what went in, what to undo it with, and why
                // it stopped. Said in that order, because the first two are about their repository and
                // the third is about this run.
                _committed = stopped.Made > 0;
                await AddMessageAsync(GoalMessageRole.System,
                    $"{GoalCommitPlan.Made(commits, stopped.Made, scope)}\n\n{stopped.Message}",
                    GoalPhase.Summary);
            }
            catch (Exception ex)
            {
                // Said, not logged. A pre-commit hook that rejected this work is the repository saying
                // no, and the user is the only one who can answer it.
                _committed = false;
                await AddMessageAsync(GoalMessageRole.System, ex.Message, GoalPhase.Summary);
            }
            finally
            {
                RefreshDetectAvailability();
            }
        }
        catch (OperationCanceledException)
        {
            // The user pausing, or the tile closing. The loop's own handler says what happened.
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Committing this run's work failed: {ex}");
            await AddMessageAsync(GoalMessageRole.System,
                $"Nothing was committed: {ex.Message}", GoalPhase.Summary);
        }
        finally
        {
            RefreshFinishedRunActions();
            PhaseLabel = _engine.GetPhaseLabel();
        }
    }

    // ── AI process execution ────────────────────────────

    /// <summary>
    /// One salvage round for a review that arrived as the JSON this tile asked for and broke on the way.
    /// </summary>
    /// <remarks>
    /// <para>Measured live, 2026-09-01: a reviewer answering in Polish put a C# interpolation with its
    /// own unescaped quotes inside a finding's detail, the block died for every reader this tile has —
    /// fenced, balanced, span — and a review whose JSON said the goal was met fell through to the prose
    /// verdict, which said the opposite. The repair belongs to the tool that wrote the block, so the
    /// answer goes back to it <b>alone</b> — no goal, no diff — for one re-send; a second AI call of a
    /// few hundred characters instead of a review re-run over the whole working tree.</para>
    /// <para>The round fires only when the text still <em>looks</em> like the requested shape
    /// (<see cref="GoalResponseParser.LooksLikeJson"/>): a prose answer earns no second call. When the
    /// re-send fails too, today's behaviour stands — the soup is shown and the prose verdict answers —
    /// which is why the original review, not the salvage attempt, is what returns.</para>
    /// </remarks>
    private async Task<GoalReviewResult> SalvagedReviewAsync(GoalReviewResult review)
    {
        if (review.WasStructured || !GoalResponseParser.LooksLikeJson(review.RawText)) return review;

        await AddMessageAsync(GoalMessageRole.System,
            "The review came back as JSON this tile could not parse — asking the tool to re-send the " +
            "same block in a valid form.", CurrentPhase);

        var salvaged = await RunAiAsync(_engine.BuildJsonSalvagePrompt(review.RawText), announceFailure: false);
        var second = GoalResponseParser.ParseReview(salvaged.Text);
        return second.WasStructured ? second : review;
    }

    /// <summary>
    /// One AI run: what it produced, and what happened to it.
    /// <para>The two travel together because they are one answer. They were three fields the caller had
    /// to remember to pass to <see cref="GoalLoopPolicy.Judge"/>, and each was added separately after
    /// its own bug — cancellation, then a missing tool, then a failed process — with a call site
    /// somewhere forgetting the newest one every time.</para>
    /// </summary>
    private readonly record struct AiRun(GoalRunVerdict Verdict, string? Text);

    /// <summary>
    /// What the agent is being asked to do right now: the phase, and whether the criteria oblige it to
    /// establish the project's health itself.
    /// </summary>
    /// <remarks>One property rather than two <c>AiUsage.Headless(CurrentPhase)</c> calls, because the
    /// second of them decides which flag a failure is blamed on and a run judged under a usage it was
    /// not launched with names the wrong one. The health half is what keeps the review out of a
    /// read-only sandbox: it is told to run this project's own build and tests, and a build writes.
    /// </remarks>
    private AiUsage CurrentUsage =>
        AiUsage.Headless(CurrentPhase,
            _engine.Criteria.RequireBuild || _engine.Criteria.RequireTestsPass);

    /// <summary>
    /// The agent's environment, after whatever it needs on disk has been made.
    /// </summary>
    /// <remarks>A headless run does not go through <c>TileLauncher</c>, so the preparation the tile gets
    /// from <c>PrepareForLaunchAsync</c> has to be asked for here too — otherwise an opencode instance on
    /// a local server would run from a config file that exists only when a tile has been opened on the
    /// same instance first.</remarks>
    private static IReadOnlyDictionary<string, string?> PreparedEnvironment(
        IAiAgent agent, AgentRuntime runtime)
    {
        agent.PrepareToLaunch(runtime);
        return agent.EnvFor(runtime);
    }

    private async Task<AiRun> RunAiAsync(string prompt, bool announceFailure = true)
    {
        var phase = CurrentPhase;

        // Looked for again before giving up. Detection runs once, when the tile is built, so an agent
        // installed after that stayed invisible for the life of the tile — and the message telling the
        // user to install it and click Resume then sent them round the same loop for ever.
        if (AgentFor(phase) is null)
            await RediscoverAgentsAsync();

        if (AgentFor(phase) is not { } chosen)
        {
            await AddMessageAsync(GoalMessageRole.System,
                MissingAgentMessage(phase), phase);
            return new AiRun(GoalLoopPolicy.Judge(null, cancelled: false, toolMissing: true), null);
        }

        // The token belongs to WorkingAsync, which holds one for the whole of a phase or a loop, so it
        // is not made here. Making one per AI call left it null in the gaps between calls — where Pause
        // had nothing to cancel — and left the git commands before each call uncancellable, so a pause
        // taken while the working tree was being read waited for both processes to finish.
        var token = _cts?.Token ?? CancellationToken.None;

        // Before the run, not after it. The assignment below is past an await, so a cancellation or an
        // unexpected exception leaves the *previous* run's count standing in a field whose whole
        // meaning is "the run that has just happened" — and the NoChange summary reads it to decide
        // whether to blame the permission mode. Neither path reaches that summary today; the field is
        // one line of housekeeping away from not depending on that being true.
        _lastRunDenials = 0;

        try
        {
            // The instance, the provider and the model it runs on, worked out once for this call: the
            // environment and the command line both need it, and asking twice is two answers waiting to
            // disagree.
            //
            // The model is *resolved* here, by the same rule the agent tile launches under
            // (AgentModelResolver): an instance asking for the first loaded model would otherwise run
            // with no model at all while the environment still pointed at the local server, and a model
            // named on an agent that cannot be told one would be dropped without a word. Both are the
            // silent substitution the sentinel exists to prevent, so both refuse the run and say which
            // agent it was.
            var (resolvedModel, modelProblem) = await AgentModelResolver.ResolveAsync(
                _settingsService.Settings, chosen.Agent, chosen.Instance, token);

            if (modelProblem is not null)
            {
                await AddMessageAsync(GoalMessageRole.System,
                    $"{chosen.Label} was not run: {modelProblem}", phase);
                return new AiRun(GoalLoopPolicy.Judge(null, cancelled: false, failed: true), null);
            }

            // Both windows at once: the compact window at 80%, and the assumed window —
            // CLAUDE_CODE_MAX_CONTEXT_TOKENS's answer — at 100%. The run is where a headless agent
            // most needs the correction: a context nobody named runs to the CLI's own 200k
            // assumption and stops there, mid-goal.
            var windows = await ModelContextWindow.ResolveAsync(
                _settingsService.Settings, chosen.Agent, chosen.Instance, resolvedModel ?? "");

            var runtime =
                AgentRuntime.For(_settingsService.Settings, chosen.Instance, resolvedModel,
                    chosen.Agent,
                    windows?.AutoCompactWindow, windows?.MaxContextTokens);

            var result = AiRunnerFactory is { } run
                ? await run(chosen, prompt, _workingDirectory, token)
                : await AiProcessRunner.RunPlainAsync(
                    chosen.ExecutablePath,
                    prompt,
                    _workingDirectory,
                    chosen.Agent,
                    // Which phase, not just "headless". It is what lets the agent run the phases that
                    // write nothing read-only whatever the strip says — decision 9 — which is the only
                    // thing standing between a review agent and the worktree it is judging.
                    CurrentUsage,
                    // Read at the moment of the run, not when the tile was built: a user who changes
                    // the mode because a run was refused expects the next attempt to use the new one.
                    _settingsService.Settings.GoalPermissionMode,
                    // Read at the moment of the run for the same reason the mode is.
                    _settingsService.Settings.GoalEffort,
                    // Refused once the tile is disposed, and that guard is here rather than implied:
                    // PostFireAndForget does not drop anything — it posts, and a post that lands after
                    // Dispose sets a property on a view model nobody is looking at. Harmless, and the
                    // comment that used to be here claimed it was prevented, which is worse than not
                    // saying so. It is still a race by a few microseconds; what it buys is that a run
                    // being torn down stops queueing work rather than queueing it to the end.
                    onActivity: doing =>
                    {
                        if (_disposed) return;

                        // Checked again on the dispatcher, not only here. This runs on the thread
                        // draining the child, so a line posted a moment before the run ends lands after
                        // the finally that clears Activity — leaving an idle tile naming the last file
                        // the tool happened to open, for the rest of the session.
                        PostFireAndForget(() => SetActivityIfRunning(doing));
                    },
                    onStarted: processId => Volatile.Write(ref _childProcessId, processId),
                    // The instance's own provider, key and model — the whole reason a goal can be run
                    // by "Claude Code on GLM 5.3 via OpenRouter" rather than by whatever the CLI is
                    // logged in as. A null value unsets, which is what stands between an inherited
                    // global key and a run on somebody else's account.
                    environment: PreparedEnvironment(chosen.Agent, runtime),
                    instance: chosen.Instance,
                    // The same instance's model, on the command line for the four agents that are told
                    // one that way - and spelled the way that agent wants it, which for opencode and pi
                    // means `provider/model`. Asked of the agent rather than taken from the runtime, so
                    // a headless run and a tile cannot spell the same instance differently; never the
                    // sentinel, since `RequestedModel` underneath is empty where the choice has not been
                    // resolved.
                    model: chosen.Agent.QualifiedModel(runtime),
                    ct: token);

            _lastRunDenials = result.PermissionDenials;

            // The failure the tool reported about itself, carried beside its words rather than read out
            // of them. A run that ended in a turn limit or a refused key comes back with text in it —
            // an apology, a half-finished note — and judged on the text alone that was Answered: the
            // failure adopted as the plan, or as the review, and acted on.
            //
            // The text still goes into the message, because a failed implementation has usually already
            // written files and this is the only account of what is now in the worktree.
            if (result.Failed)
            {
                // A gateway that drops the stream mid-run is a network fault, not the tool refusing —
                // and the run it interrupted was one the user had already asked for. The allowance is
                // spent here without asking; once it is gone, a broken stream — like every other
                // failure — posts the message below and waits.
                if (_brokenStreamRetriesLeft > 0 && GoalTilePolicy.LooksLikeBrokenStream(result.Text))
                {
                    _brokenStreamRetriesLeft--;
                    await AddMessageAsync(GoalMessageRole.System,
                        "The provider dropped the stream mid-run — retrying this run on its own.",
                        phase);
                    // The quiet contract travels with the retry: it is still the same run — the
                    // salvage round's re-send, say — and a retry that fails too must stay as quiet as
                    // the run it stands in for, or the failure message names a phase that never asked
                    // to be loud.
                    return await RunAiAsync(prompt, announceFailure);
                }

                // One recognisable cause gets named, because it is the one that fails *every* run on
                // the default setting while saying nothing about itself: a tool too old for the mode
                // this tile asked for rejects the flag, and "the AI tool reported a failure" over a
                // usage message about a flag the user never typed sends nobody anywhere.
                // Two flags, two ways out, asked in the order they were added to the command line.
                // Either one is a tool too old for something this tile asks for by default, and both
                // fail *every* run on that machine while saying nothing a user can act on.
                // Asked of the runner that built the command line, not of a constant. The flags are
                // the tools' own words — Claude Code's `--effort` is pi's `--thinking` — so a matcher
                // holding one spelling recognised one tool's refusal and left every other tool's as a
                // bare failure over a usage message about a flag the user never typed.
                var agent = chosen.Agent;

                // The settings this run was launched with, and the phase it ran in, so the question is
                // "was the flag we passed refused" and not "does this tool have such a flag at all".
                // Every agent adds its flags conditionally — a read-only phase passes a different mode
                // from the one the strip shows — and a matcher told about one that was never on the
                // command line reads any usage message as a refusal of it.
                var usage = CurrentUsage;

                // Fitted the same way the run was, or the question is asked about a mode this agent
                // never had: an agent that rounds "auto" down to no flag at all passes nothing, and a
                // matcher told to look for `--permission-mode` would read its next usage message as a
                // refusal of a flag that was never on the command line.
                var (behaviour, effort) = AiProcessRunner.Fit(agent, usage,
                    _settingsService.Settings.GoalPermissionMode,
                    _settingsService.Settings.GoalEffort,
                    chosen.Instance);

                var effortFlag = agent.EffortFlagFor(effort, usage);
                var permissionFlag = agent.BehaviourFlagFor(behaviour, usage);

                // The model refusal is asked first, because it happens before any flag could matter:
                // a CLI that will not start on the model never got far enough to refuse a mode or an
                // effort, and its failure text carries neither — only its own words about the model.
                var cause = UnrecognizedModel.Named(result.Text) ? $"{UnrecognizedModel.Advice} "
                    : AiBehaviours.LooksLikeRejectedMode(
                        result.Text, permissionFlag, effortFlag)
                        ? $"{AiBehaviours.RejectedModeAdvice} "
                        : AiEfforts.LooksLikeRejectedEffort(
                            result.Text, effortFlag, permissionFlag)
                            ? $"{AiEfforts.RejectedEffortAdvice} "
                            : "";

                // Quiet is the salvage round's answer: the run it retries succeeded — only the
                // re-send of its broken JSON failed — and "review reported a failure" over that would
                // name a failure the phase did not have.
                if (announceFailure)
                    await AddMessageAsync(GoalMessageRole.System,
                        $"{NameOf(CurrentPhase)} reported a failure. {cause}"
                        + $"{Capitalised(TryAgain())}\n\n{result.Text}",
                        CurrentPhase);
                return new AiRun(GoalLoopPolicy.Judge(null, cancelled: false, failed: true), null);
            }

            return new AiRun(GoalLoopPolicy.Judge(result.Text, cancelled: false), result.Text);
        }
        catch (OperationCanceledException)
        {
            // Every cancellation now has a pause recorded before it — Pause and Dispose both set it
            // first — so there is one message rather than a branch, and the branch that said "Operation
            // cancelled." is gone with the window that made it reachable.
            await AddMessageAsync(GoalMessageRole.System,
                GoalTilePolicy.CanResume(CurrentPhase) ? "Paused. Click Resume to continue." : "Stopped.",
                CurrentPhase);
            return new AiRun(GoalLoopPolicy.Judge(null, cancelled: true), null);
        }
        catch (Exception ex)
        {
            await AddMessageAsync(GoalMessageRole.System,
                $"{NameOf(CurrentPhase)} failed: {ex.Message}. {Capitalised(TryAgain())}", CurrentPhase);
            return new AiRun(GoalLoopPolicy.Judge(null, cancelled: false, failed: true), null);
        }
        finally
        {
            // Every way out of the run, cancellation included: the process is gone by now, and an id
            // left standing would keep adding somebody else's memory to this workspace's row for as
            // long as the tile is open.
            Volatile.Write(ref _childProcessId, 0);
        }
    }

    /// <summary>What one lap of the loop got out of the tool, and the tree it was asked about.</summary>
    /// <param name="Tree">What the working tree looked like when the prompt was built — null on a clean
    /// one. Returned rather than kept inside because the loop compares the tree an implementation
    /// started from with the tree the review was handed, which is how it notices a tool that did
    /// nothing at all.</param>
    private readonly record struct LoopAnswer(string Text, WorktreeSnapshot Tree);

    /// <summary>
    /// One phase of the implement/review loop: move into it, read the working tree, ask the tool, and
    /// decide what its answer means. Returns the answer, or <c>null</c> when the loop must stop — every
    /// reason to stop having already been acted on here.
    /// <para>The two phases differed in a name, a label and which prompt to build, and were otherwise
    /// the same twenty lines twice. Which mattered: the NoTool case was added to both by hand, and the
    /// cancelled case was fixed in one of them first.</para>
    /// </summary>
    private async Task<LoopAnswer?> RunLoopPhaseAsync(
        GoalPhase phase, string runningLabel, Func<string?, bool, string> buildPrompt,
        bool addMessage = true, bool scoped = true,
        IReadOnlyList<string>? onlyPaths = null)
    {
        if (PauseRequested) return Stopped();

        _engine.CurrentPhase = phase;
        SyncFromEngine();
        Working(runningLabel);

        // Asked again after the working tree is read and before the tool is launched. Reading the tree
        // is two short git processes, but the run after it is minutes, and a pause arriving in that
        // window used to be paid for with the whole of it.
        WorktreeSnapshot tree;
        try
        {
            tree = await ReadWorktreeAsync(scoped, onlyPaths);
        }
        catch (OperationCanceledException)
        {
            // A pause taken while the tree was being read. WorktreeReader rethrows rather than
            // answering with an empty tree — which would be a prompt claiming nothing had changed —
            // and left uncaught it came out of the loop as "Unexpected error: The operation was
            // canceled", so stopping looked like breaking.
            return Stopped();
        }

        if (PauseRequested) return Stopped();

        // The prompt is told *how* the tree was read, not only what it says. A block read against the
        // goal's baseline is what happened during the run and needs no warning about the user's older
        // work; one read against HEAD does, and that warning has a cost of its own — see
        // GoalPromptBuilder.OtherPeoplesWorkInReview.
        var run = await RunAiAsync(buildPrompt(tree.Text, tree.Scoped));

        // The verdict, not the text. A tool that answers with whitespace returns a string that is not
        // null, so asking about the string put an empty assistant bubble in the transcript and then
        // summarised the run underneath it.
        if (run.Verdict == GoalRunVerdict.Answered && addMessage)
            await AddMessageAsync(GoalMessageRole.Assistant, run.Text!, phase, markdown: true);

        if (run.Verdict == GoalRunVerdict.Answered)
            return new LoopAnswer(run.Text!, tree);

        await HandleNonAnswerAsync(run.Verdict, phase);
        return null;

        // Every way out of here that is not an answer has to put the label back. These three returns
        // happen *before* the tool is launched, so there is no cancelled run for HandleNonAnswerAsync
        // to describe and nothing else touches it — the strip went on saying "AI is implementing
        // (attempt 1/5)…" over a tile that had stopped, until something else happened to write to it.
        LoopAnswer? Stopped()
        {
            PhaseLabel = _engine.GetPhaseLabel();
            return null;
        }
    }

    /// <summary>
    /// What a run that did not answer means, for both the phases the user answers and the phases inside
    /// the loop.
    /// <para>One copy rather than two. They were the same four cases twice, and that is how the NoTool
    /// case came to be added to each by hand and the cancelled case fixed in one of them first.</para>
    /// </summary>
    private async Task HandleNonAnswerAsync(GoalRunVerdict verdict, GoalPhase phase)
    {
        switch (verdict)
        {
            case GoalRunVerdict.NoTool:
            case GoalRunVerdict.Failed:
                // Both are things the user can do something about — install it, click Resume — so the
                // tile waits rather than ending the goal and throwing away an approved plan.
                PauseAndWait();
                break;

            // A cancelled run is not a finished one. Summarising it moved the tile into Summary, which
            // Resume has no case for and WasInterrupted does not recognise — so pausing an
            // implementation was a one-way door, both in the session and after a restart. Stopping
            // where it stands leaves the phase as it is, which both understand.
            case GoalRunVerdict.Cancelled:
                PhaseLabel = _engine.GetPhaseLabel();
                break;

            case GoalRunVerdict.Empty:
                // Paused, not summarised: a tool that returned nothing once may answer the next time,
                // which is the argument that has Failed pause rather than end the goal. Falling back a
                // phase was worse than it looked — from Clarify it landed in Goal, where the next thing
                // sent clears the transcript, so one empty reply put the session a keystroke from being
                // thrown away.
                await AddMessageAsync(GoalMessageRole.System,
                    $"The tool returned nothing. {Capitalised(TryAgain(phase))}", phase);
                PauseAndWait();
                break;

            // Named rather than defaulted: a verdict added later would otherwise be silently treated as
            // an empty answer, which is the one outcome that pauses and explains itself.
            case GoalRunVerdict.Answered:
            default:
                throw new UnreachableException($"Unhandled verdict {verdict} for {phase}.");
        }
    }

    /// <summary>
    /// Runs a piece of workflow with the tile marked as working for the whole of it.
    /// <para><see cref="IsRunning"/> used to be set by <see cref="RunAiAsync"/>, so it went false in the
    /// gaps between an implementation ending and the review starting — and in those gaps the Pause
    /// button, which is bound to it, disappeared from a loop that was very much alive, while Submit
    /// told the user the run was stopped. It belongs to the workflow, which is the thing that is
    /// actually running.</para>
    /// </summary>
    private async Task WorkingAsync(Func<Task> work)
    {
        IsRunning = true;
        StartElapsed();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        // A small allowance of unasked retries against a dropped stream, per start the user asked for.
        // Here rather than in ResumeAsync alone, because Submit, an answer and an approved plan all
        // start work the same way — and the retry below never re-enters this method, so the allowance
        // is never renewed mid-work.
        _brokenStreamRetriesLeft = GoalTilePolicy.BrokenStreamRetries;

        try { await work(); }
        finally
        {
            IsRunning = false;
            StopElapsed();

            // Here rather than at each call site: a run ends five ways — finished, paused, cancelled,
            // failed, thrown — and a tile left showing the last file the tool happened to open is one
            // that looks busy while it waits for you.
            Activity = "";

            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Which stage the run is in, in the two or three words the waiting row has room for.
    /// </summary>
    /// <remarks>
    /// Derived rather than assigned beside the strip's own sentence at each phase's call site: the two
    /// would then be two answers to one question, and the one that goes stale is always the one nobody
    /// is looking at when it does. Raised from the two things it reads — the phase, and the attempt as
    /// the loop moves it on.
    /// </remarks>
    public string RunStage => GoalStageDisplay.Short(CurrentPhase, _engine.IterationCount, _engine.MaxIter);

    /// <summary>How long the current run has been going, as the waiting row writes it.</summary>
    /// <remarks>
    /// Empty between runs, which is also when the row that shows it is hidden — one property saying one
    /// thing, rather than a stale "4:07" kept alive underneath an invisible control waiting to be shown
    /// again at the start of the next run.
    /// </remarks>
    [ObservableProperty] private string _elapsed = "";

    /// <summary>
    /// Ticks the elapsed label while a run is going, and only then.
    /// </summary>
    /// <remarks>
    /// Built on first use rather than in a constructor because there are two constructors and a timer
    /// created in one of them is a timer the other tile does not have. Kept afterwards: a tile runs many
    /// times and a new timer per run is a subscription per run to get wrong.
    /// <para><see cref="DispatcherPriority.Background"/> deliberately — this is a label, and a second's
    /// lateness in it costs nothing, while a timer at input priority competes once a second with the
    /// transcript that is being appended to.</para>
    /// </remarks>
    private DispatcherTimer? _elapsedTimer;

    /// <summary>
    /// Measures the run. A <see cref="Stopwatch"/> and not two <see cref="DateTime"/>s: the wall clock
    /// moves — daylight saving, an NTP correction, a laptop waking up — and a label that answers
    /// "-1:00" or jumps an hour is worse than no label.
    /// </summary>
    private readonly Stopwatch _runClock = new();

    private void StartElapsed()
    {
        _runClock.Restart();
        Elapsed = ElapsedDisplay.Format(TimeSpan.Zero);

        _elapsedTimer ??= new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => Elapsed = ElapsedDisplay.Format(_runClock.Elapsed));

        _elapsedTimer.Start();
    }

    private void StopElapsed()
    {
        _runClock.Stop();
        _elapsedTimer?.Stop();
        Elapsed = "";
    }

    /// <summary>
    /// Whether a pause is outstanding, asked at each hand-over between two AI calls.
    /// <para>Belt and braces since the token became the whole run's rather than one call's: the loop
    /// used to carry straight on into the next call because there was no token to cancel in between.
    /// Kept because it stops the next phase being entered at all, rather than entered and then torn
    /// down.</para>
    /// </summary>
    private bool PauseRequested => _engine.IsPaused;

    /// <summary>
    /// Stops without summarising and leaves the tile resumable, for the outcomes the user can do
    /// something about: a tool that is not installed can be installed, and one that crashed can be
    /// tried again. Summarising instead put the tile in Summary, where the only way on is to type a new
    /// goal — so a missing binary, or one bad process launch, cost an approved plan and a transcript.
    /// </summary>
    private void PauseAndWait()
    {
        _engine.IsPaused = true;
        SyncFromEngine();
        PhaseLabel = _engine.GetPhaseLabel();
    }

    /// <summary>
    /// How many characters of prompt the chosen tool can be handed on a command line, or null when
    /// there is no such limit.
    /// <para>Asked before every prompt is built rather than once, because the tool can change under a
    /// run — <c>RediscoverAgentsAsync</c> can find one mid-loop — and because the answer depends
    /// on the executable's own path length. Null when a test has replaced the runner: there is no
    /// command line in that case either.</para>
    /// </summary>
    private int? PromptBudget()
    {
        // A test has replaced the runner: there is no command line to fit.
        if (AiRunnerFactory != null) return null;

        if (AgentFor(CurrentPhase) is { } chosen)
            // The instance too, because its own extra arguments go on the same command line: a prompt
            // fitted to the whole of it is refused by the guard the moment `--add-dir <long path>` is
            // set on the row, and refused identically on every Resume.
            return AiProcessRunner.PromptBudget(chosen.ExecutablePath, chosen.Agent, chosen.Instance);

        // No tool resolved *yet* — and RunAiAsync scans again before giving up, so one may well be found
        // a moment from now. The prompt is already built by then and cannot be rebuilt, so answering
        // "no limit" here meant a prompt fitted to nothing being handed to a .cmd shim, refused by the
        // guard, and reproduced identically on every Resume. The tightest of the two Windows limits is
        // the safe assumption: it costs some context in a case that may not even arise, where the other
        // answer costs the run.
        return CommandLineLength.Tightest();
    }

    /// <summary>
    /// The working tree, as the prompts see it and cut to what the tool about to receive it can be
    /// handed. Read through <see cref="WorktreeReader"/>, which owns the git commands and the seam that
    /// lets a test do without them.
    /// </summary>
    /// <remarks>
    /// The caps follow the transport rather than being constants, because they always were: 6 000
    /// characters is what a Windows command line allows, and Claude Code takes its prompt on stdin,
    /// where nothing is allowed less than everything. Charging the command line's limit on a channel
    /// without one showed the tool four per cent of a real working tree.
    /// </remarks>
    /// <param name="scoped">
    /// Whether to read the tree as what has changed <em>since this goal started</em>, from its baseline
    /// snapshot. True for the implement/review loop, where the block is headed "the changes that were
    /// just made" and that had better be near enough true.
    /// <para>False for detection, and not as a default that happens to suit it: detection asks what the
    /// user is in the middle of, which is precisely their uncommitted work against HEAD, and it runs
    /// before a goal exists — so the only baseline in reach belongs to the goal being replaced.</para>
    /// </param>
    private Task<WorktreeSnapshot> ReadWorktreeAsync(bool scoped = false,
        IReadOnlyList<string>? onlyPaths = null) =>
        NewWorktreeReader().ReadAsync(
            _cts?.Token ?? CancellationToken.None, GoalDiffContext.CapsFor(PromptBudget()),
            scoped ? DiffBase : null, onlyPaths ?? _engine.ScopePaths);

    /// <summary>
    /// The scope the composer names, read off it — nothing cleared, nothing stored.
    /// </summary>
    /// <remarks>
    /// <para>The paths arrive as <c>@</c> mentions and are the hard half (<see cref="GoalScopeFilter"/>:
    /// the tree read for this goal is filtered to them); the words around them come back as the first
    /// half of the pair and go into the prompt as the narrowing block. Image markers go no further — the
    /// images they stood for were pasted for a goal this composer text is now steering, not for this
    /// one.</para>
    /// <para><b>Nothing is consumed here, on purpose.</b> The caller clears the composer only once the
    /// text has been acted on — a detection that ends without a goal must leave the draft in the box,
    /// because that draft is the only copy of the user's words there is.</para>
    /// </remarks>
    private (string Guideline, IReadOnlyList<string> Paths) ReadScopeFromComposer()
    {
        var text = GoalImageMarker.DropMarkersExcept(InputText, []).Trim();
        return (text, LivePaths(GoalScopeFilter.Mentions(text)));
    }

    /// <summary>
    /// The named mentions that name something in this workspace.
    /// </summary>
    /// <remarks>
    /// <para>A completion never offers a file that is not there, but the composer holds anything a
    /// user typed — and a token that only looks like a path (<c>john.doe</c> out of an email-style
    /// word) passes the syntax rule and, left in, filters the whole tree to nothing over a typo. A
    /// scope naming nothing is the most expensive way a letter can be wrong: the diff is gone and the
    /// note saying so does not bring it back. Words that name nothing stay in the soft half — the
    /// guideline sentence still carries them.</para>
    /// </remarks>
    private IReadOnlyList<string> LivePaths(IReadOnlyList<string> paths) =>
        paths.Where(path =>
                File.Exists(Path.Combine(_workingDirectory, path))
                || Directory.Exists(Path.Combine(_workingDirectory, path)))
            .ToList();

    /// <summary>
    /// What a scoped read measures from — the goal's baseline, or <c>HEAD</c> where the work being
    /// judged is the work the baseline photographed.
    /// </summary>
    /// <remarks>
    /// <para>Null means <c>HEAD</c>, which is what the reader falls back to. That is the right answer
    /// on the <em>Detect &amp; run</em> path and only there: the goal was written from the uncommitted
    /// work, the baseline was taken over that same work a moment later, and the first thing the loop
    /// does is review it. Measured from the baseline, "the changes that were just made" was empty — the
    /// path whose entire purpose is to judge existing changes could not see them, and the no-change
    /// stop ended the run on the first lap.</para>
    /// <para>One property rather than the same condition at two call sites: the loop's reader and the
    /// no-change check must agree about the base or the check compares digests of two different
    /// questions, which is a bug this tile has already had once.</para>
    /// </remarks>
    private string? DiffBase => _engine.ReviewsExistingWork ? null : _engine.BaselineRef;

    /// <summary>
    /// The working tree read for its <see cref="WorktreeSnapshot.Fingerprint"/> alone — the no-change
    /// check, which never looks at the text.
    /// </summary>
    /// <remarks>
    /// The file summary is asked for with a cap of zero, which is how the reader is told not to run the
    /// command at all. It is deliberately outside the fingerprint, so a summary gathered here could not
    /// change the answer by any route: it was a git process per check, twice a lap, in the user's own
    /// repository, spent on something nothing would read.
    /// </remarks>
    private Task<WorktreeSnapshot> ReadWorktreeForComparisonAsync() =>
        NewWorktreeReader().ReadAsync(
            _cts?.Token ?? CancellationToken.None,
            GoalDiffContext.CapsFor(PromptBudget()) with { Summary = 0 },
            // Scoped, because the tree it is compared against was. A digest of `diff baseline..now` and
            // a digest of `diff HEAD` are answers to two different questions and can never be equal, so
            // reading only one of them this way silently retired the no-change stop — the run carried
            // on spending attempts on a tool that had written nothing.
            DiffBase,
            // The scope rides along so every read of this goal reports the same answer about itself.
            // The fingerprint does not need it — it is taken from the raw diff, not from the filtered
            // block — but a read of a narrowed goal that silently claimed the whole tree would be one
            // answer in the transcript and another in the check.
            _engine.ScopePaths);

    private WorktreeReader NewWorktreeReader() =>
        new(_workingDirectory, GitPath());

    /// <summary>
    /// The <c>@</c> file suggestions every text box in this tile shares.
    /// </summary>
    /// <remarks>
    /// One for the tile rather than one per box: the boxes never hold the keyboard at the same time, so
    /// there is only ever one list on screen, and sharing it shares the reading of the working tree —
    /// the only part of this that costs anything.
    /// </remarks>
    public FileMentionsViewModel FileMentions { get; }

    private FileMentionsViewModel NewFileMentions() =>
        new(new WorkspaceFileMentionSource(_workingDirectory, GitPath()));

    private string GitPath() =>
        _settingsService.Settings.GitPath is { Length: > 0 } p ? p : "git";

    /// <summary>How long the closing snapshot may take before the summary goes on without it.</summary>
    /// <remarks>Generous for four local git commands and short enough that a hung one is a missing
    /// boundary rather than a tile stuck in a phase with no text under it.</remarks>
    private static readonly TimeSpan EndSnapshotTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Photographs the working tree as this run finishes, so a commit can tell where the run ended.
    /// </summary>
    /// <remarks>
    /// <para><b>The upper end of the boundary, and it was missing.</b> A baseline alone says when the
    /// run started, so "what this run changed" was read as "everything that has changed since" — which
    /// is the same thing only while the tile is the sole thing writing to the workspace. Three Goal
    /// tiles were run in one workspace, all three finished, Commit was pressed in the first, and it put
    /// all three runs into the history under the first one's messages. The closing snapshot is what
    /// bounds the run; what moves after it belongs to whoever moved it.</para>
    /// <para><b>Quiet, unlike the baseline's.</b> That one says what it wrote because it is a way back
    /// the user may need to type; this one is only ever read by the commit that comes next, and a line
    /// about a ref in the middle of a finished run's summary explains nothing to anybody.</para>
    /// <para>Fails soft, like everything here. Without it the commit falls back to bounding at the tree
    /// as it is now — the old behaviour — and says so in the dialog rather than quietly claiming work
    /// it cannot account for.</para>
    /// </remarks>
    private async Task CaptureEndAsync()
    {
        // Nothing to bound: a run with no baseline cannot be committed at all, so a closing snapshot
        // for it would be a git command run for nobody.
        if (_engine.BaselineRef is not { Length: > 0 }) return;

        try
        {
            // Deliberately **not** the run's token. A summary is reached inside `WorkingAsync`, so on
            // every route but a clean finish `_cts` is already cancelled — Stop cancels it and then the
            // loop unwinds into here — and a snapshot taken on it would throw before git was started,
            // leaving `EndRef` null on exactly the runs whose end most needs recording. A stopped run
            // is the one the user is most likely to commit.
            //
            // Bounded by a timeout of its own instead, because the alternative to the run's token is
            // not "no limit": this is awaited before the summary is written, and a git that hangs on a
            // network drive would leave the tile in a phase with no text under it and no way out.
            using var bound = new CancellationTokenSource(EndSnapshotTimeout);

            // The tile's own file name, with no timestamp on it. `update-ref` overwrites, so this is
            // one ref per goal rather than one per summary — and the difference is not tidiness: the
            // namespace is pruned to its newest twenty, `EndRef` only ever holds the latest anyway, and
            // a goal taken through three Continues was writing four refs into a window sized for
            // twenty. That is what evicts the *other* tiles' ends, which is the failure the check in
            // `GoalCommitter.ScopeAsync` now degrades rather than reports.
            //
            // The baseline keeps its timestamp, and that asymmetry is the point: a baseline is a way
            // back the user may still need from a goal that has since been replaced, so each is worth
            // its own slot. An end is read only by the commit that follows it.
            var result = await new GoalBaseline(_workingDirectory, GitPath())
                .CaptureEndAsync(Path.GetFileNameWithoutExtension(_filePath), bound.Token);

            // Only when there is one. A failed capture must not clear the snapshot an earlier summary
            // took: bounding at the previous end is wrong by however much this run added, while
            // bounding at *now* is wrong by whatever every other tile in the workspace has done.
            if (result.Ref is { Length: > 0 } taken) _engine.EndRef = taken;
        }
        catch (OperationCanceledException)
        {
            // **The timeout, not the user.** This deliberately does not take the run's token, so the
            // only thing that cancels it is `EndSnapshotTimeout` — a git that did not come back. Left
            // silent, as it was while the comment here still said "the user pressed Stop", a hung git
            // on a network drive produced an unbounded scope and nothing anywhere saying why.
            Trace.TraceWarning(
                "Taking the goal's closing snapshot timed out after "
                + $"{EndSnapshotTimeout.TotalSeconds:0} s; the commit will not be bounded at its end.");
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Taking the goal's closing snapshot failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Photographs the working tree before the tool is let near it, and says what came of it.
    /// </summary>
    /// <remarks>
    /// <para>Taken as the goal starts rather than before each attempt: what the user wants back is what
    /// they had before any of this ran, and a per-attempt snapshot would quietly re-baseline over the
    /// damage the attempt before it did.</para>
    /// <para>The only outcome worth a sentence is the one the user can act on. A workspace with no
    /// repository has <b>no</b> way back from a deleted file — not even <c>git checkout HEAD</c>, which
    /// is what everyone reaches for — and the workspaces panel already offers to create one. Every other
    /// failure is a snapshot that did not happen where git still works; saying so would be a line of
    /// apology about a safety net nobody asked for, in a transcript about something else. It is logged.
    /// </para>
    /// <para>Nothing here can stop the goal. The whole call is wrapped, because a backup that prevents
    /// the work is worse than no backup: this is the one place in the run where a failure would be
    /// costing the user the very session it exists to protect.</para>
    /// </remarks>
    private async Task CaptureBaselineAsync()
    {
        GoalBaselineResult result;
        try
        {
            result = await new GoalBaseline(_workingDirectory, GitPath())
                .CaptureAsync($"{Path.GetFileNameWithoutExtension(_filePath)}-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    _cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Taking the goal baseline failed: {ex.Message}");
            return;
        }

        _engine.BaselineRef = result.Ref;

        if (result.Ref is { } saved)
        {
            await AddMessageAsync(GoalMessageRole.System,
                "Saved the working tree as it is now, in case something here has to be undone:\n" +
                $"git checkout {saved} -- <path>", GoalPhase.Goal);
            return;
        }

        if (result.NoRepository)
            await AddMessageAsync(GoalMessageRole.System,
                "This workspace is not a git repository, so nothing can undo what the tool changes or " +
                "deletes here. Create one from the workspaces panel to get that back.", GoalPhase.Goal);
    }

    // ── Commands ────────────────────────────────────────

    /// <summary>
    /// The strip's Pause button: asks first, because what it interrupts is not the click's to spend.
    /// </summary>
    /// <remarks>
    /// <para>Pausing cancels the token, which kills the tool mid-answer: the AI call in flight is paid
    /// for and thrown away, and Resume starts that phase again from the beginning rather than from
    /// where it stopped. The glyph is thirteen pixels wide and sits between two buttons that cost
    /// nothing — the criteria flyout and New goal — so the click that lands on it by accident is the
    /// expensive one on the strip. That is the same case <c>Restart shell</c> is guarded for, and the
    /// question is asked in the same place: on the command the button is bound to, not inside
    /// <see cref="Pause"/>, so the one path that has a screen to ask on is the one that asks.</para>
    /// <para><b>The phone is deliberately still given it unasked</b> (<see cref="InvokeAsync"/> reaches
    /// <see cref="Pause"/> directly). Pause is one of the three actions a run is driven by from another
    /// room, and the reason a destructive action is <em>withheld</em> from a phone rather than confirmed
    /// there is that confirming what you cannot see is theatre. Marking it
    /// <see cref="TileAction.IsDestructive"/> would take it off that screen altogether, which is the
    /// opposite of what it is on it for.</para>
    /// <para><b>Nothing wired means it goes ahead</b>, which is the tiles' own default and not Settings'.
    /// The other direction would leave a user with no window to answer in unable to stop a running
    /// agent, and an interruption they cannot reach is worse than one they did not mean.</para>
    /// </remarks>
    [RelayCommand]
    private async Task PauseAsync()
    {
        // Asked before the state is touched, and re-tested after: a dialog is open for as long as the
        // user takes, and a run that finished while it stood there must not be "paused" into a tile
        // that comes back claiming to have been interrupted.
        if (!IsRunning) return;

        if (ConfirmAction is { } confirm
            && !await confirm("Pause the run?\n\n"
                + "The tool is stopped mid-answer and that answer is lost. "
                + "Everything already written down is kept, and Resume starts this step again."))
            return;

        Pause();
    }

    private void Pause()
    {
        // The button is only shown while something is running, but a command can be reached by other
        // means, and pausing an idle tile is the same bug Dispose had: it comes back claiming to be
        // interrupted and Resume re-runs a phase that already finished.
        if (!IsRunning) return;

        // Before the cancellation, as in Dispose. Cancelling first left a window in which the run threw
        // while IsPaused was still false, and the handler below then wrote "Operation cancelled." into
        // the transcript — a system message as the last line, which is exactly what makes WasInterrupted
        // read an unanswered Clarify as one that has its answer.
        _engine.IsPaused = true;
        IsPaused = true;

        _cts?.Cancel();
        SaveStateNow();
    }

    [RelayCommand]
    private async Task ResumeAsync()
    {
        // IsRunning as well as IsPaused: pausing cancels the token but the loop takes as long to unwind
        // as the tool takes to die, and Resume is offered the whole time. Without this, clicking it in
        // that window started a second implement/review loop alongside the one still shutting down —
        // two AI processes on one working tree, both writing the same file.
        if (!IsPaused || IsRunning) return;
        _engine.IsPaused = false;
        IsPaused = false;

        try
        {
            switch (CurrentPhase)
            {
                case GoalPhase.Implement:
                case GoalPhase.Review:
                    await AddMessageAsync(GoalMessageRole.System, "Resuming implementation...", CurrentPhase);
                    await WorkingAsync(() => RunImplementReviewLoopAsync(
                        finishInterruptedIteration: true,
                        startAtReview: GoalTilePolicy.ResumesAtReview(CurrentPhase)));
                    break;
                case GoalPhase.Clarify:
                    await WorkingAsync(RunClarifyAsync);
                    break;
                case GoalPhase.Plan:
                    await WorkingAsync(RunPlanAsync);
                    break;

                default:
                    // Nothing to resume in Goal or Summary — but the pause has just been cleared in
                    // memory, and without this nothing would write that. The file kept saying paused,
                    // so the button came back after every restart and did nothing every time.
                    SaveStateNow();
                    PhaseLabel = _engine.GetPhaseLabel();
                    break;
            }
        }
        catch (Exception ex)
        {
            await StoppedByErrorAsync("Goal resume error", ex);
        }
    }

    [RelayCommand]
    private async Task NewGoalAsync()
    {
        if (IsRunning) return;

        if (!await ConfirmDiscardAsync())
        {
            // The same explanation Submit gives. Usually this is the user answering "no", which needs
            // none — but it is also what happens when there is no dialog to ask in, and then the button
            // simply did nothing.
            if (ConfirmAction == null)
                await SayOnceAsync("This tile cannot ask whether to discard the current goal, so it " +
                                   "has kept it.");
            return;
        }

        // Only over a file that already exists. Clicking + on a tile nobody has used yet otherwise
        // created an empty session in .mtiles/goals/, which is the thing the guard in Dispose is
        // there to prevent and which nothing ever prunes.
        var hadFile = File.Exists(_filePath);

        StartFreshGoal("");
        SyncFromEngine(save: hadFile);
        PhaseLabel = _engine.GetPhaseLabel();

        // About to want the buttons, and the tree may well have moved since the last time anybody
        // asked — this tile has been sitting on a finished goal.
        RefreshDetectAvailability();
    }

    // ── Engine ↔ ViewModel sync ────────────────────────

    /// <summary>The label a phase runs under, and the end of whatever the last phase was doing. The
    /// clear belongs with the label because they change together: without it the strip named a file
    /// from the implementation for the whole of the review that followed it — a tile looking busy
    /// about something that finished.</summary>
    private void Working(string label)
    {
        PhaseLabel = label;
        Activity = "";
    }

    /// <summary>
    /// The engine has moved; the view model and the file both follow it.
    /// <para>The save is here rather than at each call site because a phase change is exactly the thing
    /// a restart needs to have seen. Saving only with each message was not enough: approving a plan
    /// moves the engine into Implement and then waits on the tool for minutes, and for all of that the
    /// file still said Plan with no approved plan in it — so the whole first implementation was lost
    /// and the tile came back asking to have the plan approved again.</para>
    /// </summary>
    /// <param name="save">
    /// False only where the write is the thing being avoided. Starting a fresh goal on a tile nobody
    /// has used yet must leave no file behind, and the guard that says so was unreachable while this
    /// method wrote unconditionally — the file was created here, a line before anything asked whether
    /// it should be.
    /// </param>
    private void SyncFromEngine(bool save = true)
    {
        CurrentPhase = _engine.CurrentPhase;
        IsPaused = _engine.IsPaused;

        // ShowApproval reads the engine's proposed plan, which is a plain field and notifies nobody.
        // The phase does not move when a plan arrives — it was set to Plan before the run started —
        // so without this the panel waited on IsRunning going false to be asked again, which happens to
        // work and would stop the day anything else set the plan.
        RefreshAsk();

        if (save)
            SaveStateNow();
    }

    // ── UI helpers ──────────────────────────────────────

    /// <summary>
    /// Adds one message and writes the state out with it.
    /// <para>The save is the point. The implement/review loop used to save only in its <c>finally</c>,
    /// so closing the application between approving a plan and the summary left the file holding the
    /// state from before the approval: no <c>ApprovedPlan</c>, iteration 0, and none of the tool's
    /// answers — the expensive half of a session, gone without a trace of it having happened. A message
    /// is written once per answer, so this is a handful of small writes over a run, and after it every
    /// point the transcript can be interrupted at is a point it can be resumed from.</para>
    /// </summary>
    /// <param name="markdown">Text the tool wrote in its own words — see
    /// <see cref="GoalMessage.Markdown"/>. Off by default, so anything this application composed is
    /// shown as written: its columns are made of spaces, and markdown does not keep spaces.</param>
    /// <param name="findings">A review's findings, drawn as rows under the text rather than flattened
    /// into it. Ordered by the caller: this list is what goes in the file, and what comes back out of it
    /// is bound straight into an items control that sorts nothing.</param>
    /// <param name="questions">A clarification round and its answers, drawn as the questions they were
    /// rather than as the numbered paragraph they flatten to. Snapshots, not the engine's own objects —
    /// see <see cref="GoalQuestionAnswer.Snapshot"/>.</param>
    private async Task AddMessageAsync(GoalMessageRole role, string text, GoalPhase phase,
        bool markdown = false, IReadOnlyList<GoalFinding>? findings = null,
        IReadOnlyList<GoalQuestion>? questions = null, bool isRunSummary = false)
    {
        // Built once and added on whichever thread this is. The two branches used to hold a copy of the
        // initialiser each, which is how the third property was added to one of them.
        var message = new GoalMessage
        {
            Role = role, Text = text, Phase = phase, Markdown = markdown,
            IsRunSummary = isRunSummary,
            Findings = findings is null ? [] : [..findings],
            Questions = questions is null ? [] : [..questions],
        };

        if (Dispatcher.UIThread.CheckAccess())
            Messages.Add(message);
        else
            await Dispatcher.UIThread.InvokeAsync(() => Messages.Add(message));

        SaveStateSoon();
    }

    // ── Persistence ─────────────────────────────────────

    /// <summary>
    /// The tile's file. Everything about <em>when</em> to write, and when to refuse, is
    /// <see cref="GoalStateStore"/>'s; what stays here is the one thing only this class can supply —
    /// the snapshot, taken on the UI thread.
    /// </summary>
    private GoalStateStore NewStore() => new(_filePath, new GoalStatePersistence())
    {
        // The whole snapshot, engine included, on the UI thread. Copying only the messages there left
        // ToState enumerating ClarificationHistory on a pool thread while the workflow added to it —
        // "collection was modified", and a transient race then lit the permanent "this tile could not
        // save its state" for a tile that saves perfectly well.
        Snapshot = () => Dispatcher.UIThread.CheckAccess()
            ? _engine.ToState([..Messages], ExecutionAgentInstanceId, ReviewAgentInstanceId)
            : Dispatcher.UIThread.Invoke(
                () => _engine.ToState([..Messages], ExecutionAgentInstanceId, ReviewAgentInstanceId)),

        // Into the transcript, from whichever thread the write failed on.
        Report = text => PostFireAndForget(() => Say(text)),
    };

    private void SaveStateSoon() => _store.SaveSoon();

    private void SaveStateNow() => _store.SaveNow();

    private void LoadState()
    {
        try
        {
            var state = _store.Load();
            if (state == null) return;

            // LoadFrom is what turns an interrupted run into a pause — the whole of "continue where
            // you left off", since the Resume button is bound to IsPaused and ResumeAsync already
            // knows how to pick the implement/review loop back up.
            _engine.LoadFrom(state);

            CurrentPhase = state.CurrentPhase;
            IsPaused = _engine.IsPaused;

            // The agents this goal was chosen to run on, kept whether or not they are here: a goal that
            // names a missing agent must say so rather than be moved onto whatever else is installed,
            // which is exactly what the tool list used to do.
            var savedTool = state.SelectedToolName;
            var restored = state.ExecutionAgentInstanceId.Length > 0
                ? state.ExecutionAgentInstanceId
                // A goal file written before agents had instances, and one that will keep arriving: a
                // goal travels with a branch, so this is read for ever rather than migrated once.
                : GoalAgents.MatchingToolName(_availableAgents, savedTool)?.InstanceId ?? "";

            if (restored.Length > 0)
                ExecutionAgentInstanceId = restored;

            ReviewAgentInstanceId = state.ReviewAgentInstanceId;

            var savedAgentIsGone = restored.Length > 0 && ExecutionAgent is null;

            foreach (var m in state.Messages)
                Messages.Add(m);

            ShowBadges();

            // The questions a closed tile was waiting on. This is what the pending set is persisted
            // for — a panel built from a parsed answer would not survive the tile being closed, and the
            // goal would come back waiting for an answer to questions nobody could see any more.
            SyncQuestions();

            // After the transcript, not before it: a note about this session belongs at the end of the
            // session, and said first it sat above everything the user had ever typed into this tile.
            // Said, and nothing swapped. Naming a substitute here was the old behaviour and it is the
            // thing this stage removes: a goal planned by one model being carried out by another,
            // announced once in a transcript nobody rereads.
            if (savedAgentIsGone)
                Say(_availableAgents.Count > 0
                    ? "The agent this goal was using is not available. Choose another one in the strip "
                      + "above, then click Resume."
                    : "The agent this goal was using is not available, and no other agent was found. "
                      + "Install one and click Resume.",
                    aboutThisSession: true);

            PhaseLabel = _engine.GetPhaseLabel();
        }
        catch (GoalStateUnreadableException ex)
        {
            // Said out loud, in the tile, because the next thing this tile does is save over the file:
            // a warning in the log would be the only trace that a session had ever existed.
            Trace.TraceWarning($"Damaged goal state: {ex.InnerException?.Message}");
            Say(ex.KeptAt is { } kept
                ? $"This tile's saved goal is damaged. The file was kept as {Path.GetFileName(kept)}, and the tile has started empty."
                : "This tile's saved goal is damaged, and the file could not be moved aside either. The tile has started empty.");
        }
        catch (GoalStateUnavailableException ex)
        {
            // Nothing was touched and nothing will be — the store has already refused, because that
            // consequence belongs with the file rather than with whoever happens to catch this.
            Trace.TraceWarning($"Unreadable goal file: {ex.InnerException?.Message}");
            Say($"This tile's saved goal could not be opened ({ex.InnerException?.Message}). " +
                "The file has been left alone and this tile will not save over it — close the tile and " +
                "open it again once the file can be read.");
        }
        catch (Exception ex)
        {
            // The store has refused already, for the reason it documents: this catch is reached with
            // the tile half-populated, so the next save would put that emptiness on top of the session
            // it failed to read.
            Trace.TraceWarning($"Failed to load goal state: {ex}");
            Say($"This tile's saved goal could not be read ({ex.Message}). The file has been left " +
                "alone and this tile will not save over it.");
        }
    }

    /// <summary>
    /// How to try again from where the tile is standing — which is not always "click Resume".
    /// <para>Resume does nothing in Goal or Summary, and the detection path runs from Goal: every one
    /// of these messages used to point at a button that was either absent or inert.</para>
    /// </summary>
    private string TryAgain(GoalPhase? phase = null) =>
        GoalTilePolicy.CanResume(phase ?? CurrentPhase) ? "click Resume to try again." : "try again.";

    private static string Capitalised(string sentence) =>
        sentence.Length == 0 ? sentence : char.ToUpperInvariant(sentence[0]) + sentence[1..];

    /// <summary>A note from the tile itself, straight into the transcript. Deliberately not
    /// AddMessageAsync: this is used while loading, before the view is attached, and from the save
    /// path, where saving again would be a loop.</summary>
    private void Say(string text, bool aboutThisSession = false) =>
        Messages.Add(new GoalMessage
        {
            Role = GoalMessageRole.System,
            Text = text,
            Phase = CurrentPhase,
            AboutThisSession = aboutThisSession,
        });

    /// <summary>The same note, but not if it is already the last thing in the transcript. Pressing
    /// Enter at a stopped run answered once per keystroke, and each answer was a write to disk.</summary>
    private async Task SayOnceAsync(string text)
    {
        if (Messages.LastOrDefault() is { Role: GoalMessageRole.System } last && last.Text == text)
            return;

        await AddMessageAsync(GoalMessageRole.System, text, CurrentPhase);
    }

    /// <summary>Runs something on the UI thread, from wherever this happens to be called.</summary>
    private static void PostFireAndForget(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    /// <summary>The same, awaited, for a caller that needs it done before it carries on.</summary>
    private static Task Post(Action action)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.InvokeAsync(action).GetTask();

        action();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asks before a transcript is thrown away, or answers yes when there is nothing to lose.
    /// Shared by the + button and by typing a new goal into a finished tile, which are the same act.
    /// </summary>
    private async Task<bool> ConfirmDiscardAsync()
    {
        // What is worth a dialog is what would be lost, not which phase the tile is in. Asking about
        // the phase meant that a Clarify which failed — and so put the engine back to Goal — let the
        // next thing typed wipe the goal, the answers and the tool's replies without a word. Notes the
        // tile wrote about itself are not worth interrupting anybody over.
        if (!GoalTilePolicy.WorthConfirming(Messages)) return true;

        // No dialog to ask in means no. The same answer the Settings dialog gives, and for the same
        // reason: an unanswered question is not a yes, and there is no undo for a discarded session.
        if (ConfirmAction == null) return false;

        return await ConfirmAction("Discard the current goal and start fresh?");
    }

    public void Dispose()
    {
        // Idempotent: the second call would reach a cancellation token that has already been disposed,
        // and would write the file again on the way out.
        if (_disposed) return;

        // Only if there was something to interrupt. Paused before it is cancelled, and in that order,
        // because a cancellation with no pause behind it is reported by RunAiAsync as "Operation
        // cancelled." — a system message, which then becomes the last thing in the transcript, and
        // WasInterrupted reads a Clarify or Plan whose last message is not the user's as one that
        // already has its answer. The tile came back saying "answer the questions above" with no
        // questions above it, and no Resume to ask them again.
        //
        // Unconditionally is worse than not at all: every idle tile then came back claiming to be
        // paused, and Resume in Clarify asked its questions a second time.
        if (GoalTilePolicy.ClosingIsAPause(IsRunning))
        {
            _engine.IsPaused = true;
            IsPaused = true;
        }

        _disposed = true;

        // A running DispatcherTimer is rooted by the dispatcher and its handler holds this tile, so a
        // closed tile that was mid-run would go on ticking a label nobody can see, for ever.
        StopElapsed();

        // The token is disposed and cleared by RunAiAsync's own finally, so this usually finds nothing
        // — but "usually" is not a guarantee worth an exception on the way out of a tile.
        // Cancelled but not disposed: the run that owns it is still unwinding and will dispose it in
        // WorkingAsync's own finally. Disposing it here left that run reading Token on a dead object.
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { /* already gone */ }

        // Whatever the debounce was still holding — but only if there is something to write, or
        // something already written to keep current. A Goal tile opened and closed without a word used
        // to leave an empty session in the user's repository, and nothing here ever prunes those.
        _store.Dispose(flush: Messages.Count > 0 || _store.FileExists);

        // After the final write, so a probe still in flight cannot be the reason it did not happen.
        // Cancelled, not disposed — see the field.
        _lifetime.Cancel();

        // Linked to the lifetime above, so this only releases its registration.
        Interlocked.Exchange(ref _detectAvailabilityCheck, null)?.Dispose();

        // Beside FileMentions below and for the same reason: a closed tile has no use for the answer,
        // and the workspace's watcher stops altogether once its last subscriber has gone.
        _treeWatch?.Dispose();

        // The `@` suggestions read the working tree with a git process, and a tile nobody is looking at
        // has no use for the answer. Last, because it is the only thing here that cannot affect the
        // save above.
        FileMentions.Dispose();
    }
}
