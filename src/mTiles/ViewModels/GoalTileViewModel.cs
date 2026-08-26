using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;

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
/// <item><c>RediscoverSelectedToolAsync</c>, which walks PATH on a pool thread and marshals back before
/// touching <c>AvailableTools</c>;</item>
/// <item><c>RefreshDetectAvailability</c>, which asks git in the background and marshals back before
/// setting <c>HasUncommittedChanges</c>.</item>
/// </list>
/// <para>Anything reachable from those three checks the dispatcher; nothing else does, and nothing else
/// should start. Sprinkling the check more widely would be worse than useless — it would suggest the
/// workflow can be driven from anywhere, which is exactly the belief that makes a race look acceptable.
/// </para>
/// </remarks>
public partial class GoalTileViewModel : ObservableObject, IDisposable
{
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

    private List<AiToolInfo>? _cachedTools;

    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private GoalPhase _currentPhase = GoalPhase.Goal;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _selectedToolName = "Claude Code";
    [ObservableProperty] private string _phaseLabel = "Waiting for goal...";
    [ObservableProperty] private bool _isPaused;

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
    /// <para>Two of the five stops, for two different reasons. <b>BudgetSpent</b> is the plain one: the
    /// attempts ran out and more of them is exactly what is missing. <b>VerifyTimedOut</b> is the one
    /// the user can <em>fix</em> — and once they have cleared the command that hung, carrying on is
    /// what they want; the alternative was retyping the goal into an empty tile.</para>
    /// <para>The other three are not budgets and never will be: a met goal has nothing to continue
    /// towards, a no-progress stop has just established that two reviews in a row found exactly the
    /// same things, and a no-change stop means the tool wrote nothing. Offering Continue there would
    /// sell AI runs whose outcome is already known.</para>
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

            // A timed-out verify command is not a budget, so Continue would normally have nothing to
            // offer — pressing it would buy another half hour of the same wait. But it is also the one
            // stop the user can *fix*, and the summary tells them how: clear the command under the tune
            // button. Once they have, carrying on is exactly what they want, and the alternative is
            // retyping the goal into an empty tile and paying for the clarification round again. So the
            // button appears when the command that hung is no longer there to hang again.
            GoalStopReason.VerifyTimedOut => _engine.Criteria.VerifyCommand.Length == 0,

            // Met has nothing to continue towards, NoChange means the tool wrote nothing, and
            // NoProgress has just established that two reviews running found exactly the same things.
            _ => false,
        };

    /// <summary>The button says how many attempts it will add, because it reads the number out of the
    /// attempts field at the moment it is clicked — so somebody who wants two more can type 2 and
    /// see the button agree before pressing it. It adds nothing, and says nothing about adding, where
    /// the budget was never spent.</summary>
    public string ContinueLabel =>
        AttemptsContinueWouldAdd() is var added && added > 0 ? $"Continue · +{added}" : "Continue";

    /// <summary>Why this run stopped, in the words the bar above the button uses. It said "The attempts
    /// ran out" for both stops, which is untrue of the one where they did not.</summary>
    public string ContinueReason => _engine.LastStopReason == GoalStopReason.VerifyTimedOut
        ? "The verify command was cleared."
        : "The attempts ran out.";

    /// <summary>
    /// What Continue would really add.
    /// </summary>
    /// <remarks>
    /// <para>Nothing at all where the budget still has attempts in it. That is the VerifyTimedOut case:
    /// the run stopped on attempt 2 of 5, so there are three left and Continue only has to let them
    /// happen — adding the field on top raised a ceiling the user had set to 5 up to 7, which is not
    /// something they asked for and not something the button said it would do.</para>
    /// <para>Otherwise the attempts field, unless the ceiling is nearer than that. The label used to
    /// read the field alone, so a goal 48 attempts in offered "+5" and added two.</para>
    /// </remarks>
    private int AttemptsContinueWouldAdd() =>
        _engine.IterationCount < _engine.MaxIter
            ? 0
            : Math.Min(_engine.IterationCount + GoalCompletionPolicy.Attempts(_engine.Criteria),
                       GoalCompletionPolicy.MostAttempts) - _engine.IterationCount;

    partial void OnHasUncommittedChangesChanged(bool value) => OnPropertyChanged(nameof(CanDetectGoal));

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDetectGoal));
        OnPropertyChanged(nameof(CanContinue));
        RefreshAsk();
    }

    partial void OnCurrentPhaseChanged(GoalPhase value)
    {
        OnPropertyChanged(nameof(CanDetectGoal));
        OnPropertyChanged(nameof(CanContinue));
        RefreshAsk();
    }

    /// <summary>The approval panel's button reads the box, so it has to be told when the box changes.
    /// </summary>
    partial void OnInputTextChanged(string value) => OnPropertyChanged(nameof(ApprovalActionLabel));

    // ── What the tile is asking for right now ───────────

    /// <summary>
    /// The clarifying questions, each with its own box.
    /// </summary>
    /// <remarks>
    /// Rebuilt from the engine rather than added to, so there is one copy of the truth and a reload
    /// produces the same panel a fresh round does.
    /// </remarks>
    public ObservableCollection<GoalQuestionAnswer> Questions { get; } = [];

    /// <summary>
    /// Wired once, from both constructors, because <see cref="ShowQuestions"/> is derived from the
    /// collection and a derived flag that only updates when somebody remembers to say so is a flag that
    /// is wrong the first time somebody does not.
    /// </summary>
    private void WatchQuestions() => Questions.CollectionChanged += (_, _) => RefreshAsk();

    /// <summary>
    /// Whether the question panel is up.
    /// </summary>
    /// <remarks>
    /// The panel replaces the composer rather than sitting above it, because they would be two boxes
    /// asking for the same thing with different filing rules. While it is up the composer has nothing
    /// to send that the panel cannot send better.
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
    /// <para>Still up in Implement and Review when the run is stopped, where there is nothing to send
    /// but the tile answers with what to do instead. That sentence is the point: a phase with no
    /// composer and no explanation is a tile that has stopped responding.</para>
    /// </remarks>
    public bool ShowComposer => !IsRunning && !ShowQuestions && !ShowApproval;

    /// <summary>What the panel's one button does, which follows the box under it. A user who has typed
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
    /// All that is left of a line that used to say what was being asked as well. The panel is capped
    /// and scrolls, so the count is the one thing it cannot show for itself — everything else that
    /// line said was already on the status strip, in the placeholder and on the button.
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
    }

    /// <summary>
    /// Sends the answers as one numbered message, the shape the next prompt reads.
    /// </summary>
    /// <remarks>
    /// <para>Unanswered questions are left out rather than sent empty. A blank line under a number
    /// says "none of your business" to a model that cannot tell it from a skipped one, and the round
    /// after it asks again.</para>
    /// <para>The questions go into the transcript here, at the moment they are answered, rather than
    /// when they were asked. That keeps the record complete — question then answer, in order, exactly
    /// as it always read — without spending the screen on a second copy of what the panel above is
    /// already showing. Which is the whole point: the conversation has to stay readable while three
    /// questions are on screen.</para>
    /// </remarks>
    [RelayCommand]
    private async Task SendAnswers()
    {
        // The same question the panel's own visibility asks, so a phase the tile has moved on from
        // cannot send answers to questions it is no longer asking.
        if (!ShowQuestions) return;

        var answered = Questions.Where(q => q.Answer.Trim().Length > 0).ToList();
        if (answered.Count == 0)
        {
            // Not the composer's version of this sentence, which offers to hear what you want changed
            // instead: while the panel is up the composer is not, so there is nowhere to say it.
            await SayOnceAsync("Answer at least one of the questions before sending.");
            return;
        }

        var text = string.Join("\n", answered.Select(q => $"{q.Marker} {q.Answer.Trim()}"));
        var asked = GoalTranscript.Questions(new GoalClarifyResult
        {
            WasStructured = true,
            Questions = [.._engine.PendingQuestions],
        });

        // The transcript first, the pending set second: clearing is what takes the questions off the
        // screen, and doing it before the record is written is a moment in which they exist nowhere at
        // all. Not markdown, because this is a numbered list composed here rather than prose the tool
        // wrote, and markdown would re-flow it.
        await AddMessageAsync(GoalMessageRole.Assistant, asked, GoalPhase.Clarify);
        ClearPendingQuestions();

        InputText = text;
        await Submit();
    }

    /// <summary>Approves the plan, or sends the correction typed under it — whichever the box says.
    /// Both go through <c>Submit</c>, which is where every rule about phases, pauses and discarding a
    /// session already lives.</summary>
    [RelayCommand]
    private async Task ApproveOrChange()
    {
        // The same question the panel's visibility asks. IsRunning alone let the command run in a phase
        // with no plan in it, where Submit's own "there is no plan to approve yet" is the only thing
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
    public ObservableCollection<string> AvailableTools { get; } = [];
    public Action? ScrollToEnd { get; set; }
    public Func<string, Task<bool>>? ConfirmAction { get; set; }

    private AiToolInfo? _resolvedTool;

    /// <summary>
    /// How a prompt is actually run. Replaced by a test so the phase machine can be driven without a
    /// tool installed, a process spawned or a repository on disk.
    /// <para>The seam is the same one <c>TerminalControl.PtyFactory</c> gives the launch chain, and for
    /// the same reason: this loop is where the bugs kept landing, and every one of them needed a real
    /// AI process and a real worktree to reach. A static default rather than a constructor argument,
    /// because nothing in the application chooses it and a parameter every call site has to pass null
    /// for is a parameter that will be passed the wrong thing eventually.</para>
    /// </summary>
    /// <summary>The stand-in for a real tool. It answers with an <see cref="AiOutput"/> rather than a
    /// string so a test can say the tool <em>failed</em> — the path that pauses the run and prints what
    /// the tool managed to say. While this returned a string that path could only be reached at the
    /// level of the stream reader, which is the half that does not decide anything.</summary>
    internal static Func<AiToolInfo, string, string, CancellationToken, Task<AiOutput>>? AiRunnerFactory { get; set; }

    public string FilePath => _filePath;

    public GoalTileViewModel(string workingDirectory, SettingsService settingsService)
    {
        _workingDirectory = workingDirectory;
        _settingsService = settingsService;

        var goalsDir = Path.Combine(workingDirectory, ".mterminal", "goals");
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

        DetectTools();
        RefreshDetectAvailability();
    }

    public GoalTileViewModel(string filePath, string workingDirectory, SettingsService settingsService)
    {
        _workingDirectory = workingDirectory;
        _settingsService = settingsService;
        _filePath = filePath;
        _store = NewStore();
        WatchQuestions();
        Criteria = NewCriteriaEditor();

        DetectTools();
        LoadState();
        Criteria.Reload();
        RefreshDetectAvailability();
    }

    // ── Tool detection ──────────────────────────────────

    /// <summary>What the combo box shows when there is nothing to show. Named because it is added in
    /// one place and has to be taken away again in another.</summary>
    private const string NoToolsPlaceholder = "(no AI tools detected)";

    private List<AiToolInfo> GetCachedTools()
    {
        return _cachedTools ??= AiToolDetector.Detect(
            _settingsService.Settings.CustomAiToolPaths,
            _settingsService.Settings.CustomAiTools);
    }

    private void DetectTools()
    {
        _cachedTools = null;
        var tools = GetCachedTools();

        AvailableTools.Clear();
        foreach (var t in tools.Where(t => t.IsInstalled))
            AvailableTools.Add(t.Name);

        if (AvailableTools.Count == 0)
            AvailableTools.Add(NoToolsPlaceholder);

        _resolvedTool = tools.FirstOrDefault(t => t.Name == SelectedToolName && t.IsInstalled)
                        ?? tools.FirstOrDefault(t => t.IsInstalled);

        if (_resolvedTool != null)
            SelectedToolName = _resolvedTool.Name;
    }

    /// <summary>
    /// Scans again for the tool the user has chosen, without disturbing anything else.
    /// <para>Deliberately not <see cref="DetectTools"/>, which is a first-run routine: it clears
    /// <see cref="AvailableTools"/> — resetting the selection of the combo box bound to it, which
    /// writes back through the binding — and, finding the chosen tool still absent, falls back to any
    /// other installed one. Called mid-run, that silently swapped the tool a goal was being carried out
    /// with. Here nothing is removed and nothing is substituted: if the tool is still not there, the
    /// run says so, which is the truth.</para>
    /// <para>The scan itself walks PATH and several home directories, so it goes to a background thread
    /// rather than stopping the one drawing the tile.</para>
    /// </summary>
    private async Task RediscoverSelectedToolAsync()
    {
        var settings = _settingsService.Settings;
        var tools = await Task.Run(() =>
            AiToolDetector.Detect(settings.CustomAiToolPaths, settings.CustomAiTools));

        _cachedTools = tools;

        // Back on the UI thread before touching AvailableTools: it is bound to a combo box, and after
        // the await above this is whatever thread the continuation landed on. The rest of the class
        // asks the same question rather than assuming (AddMessageAsync, SaveState).
        await Post(() =>
        {
            var installed = tools.Where(t => t.IsInstalled).Select(t => t.Name).ToList();

            // The placeholder goes as soon as there is something real, or the list offers "(no AI tools
            // detected)" next to a working tool and lets the user pick it.
            if (installed.Count > 0)
                AvailableTools.Remove(NoToolsPlaceholder);

            foreach (var name in installed.Where(name => !AvailableTools.Contains(name)))
                AvailableTools.Add(name);
        });

        _resolvedTool = tools.FirstOrDefault(t => t.Name == SelectedToolName && t.IsInstalled);
    }

    partial void OnSelectedToolNameChanged(string value)
    {
        var tools = GetCachedTools();
        _resolvedTool = tools.FirstOrDefault(t => t.Name == value && t.IsInstalled);
    }

    // ── Completion criteria ─────────────────────────────

    /// <summary>
    /// A verify command that came out of the goal file and has not yet been agreed to in this session.
    /// <para>Deliberately <b>not persisted</b>, and that is the whole design. Goal files live in
    /// <c>.mterminal/goals/</c> inside the user's own repository; nothing gitignores that directory
    /// unless the Git tile is used, so a committed one travels with a branch — and it carries a shell
    /// command that this tile runs after every attempt. A stored "the user agreed" flag would travel
    /// with it and agree on their behalf.</para>
    /// </summary>
    private bool _verifyCommandNeedsConsent;

    /// <summary>
    /// Set once the tile has explained that it cannot ask about the verify command.
    /// <para>The loop asks on every attempt and other messages land in between, so <c>SayOnceAsync</c>
    /// — which only skips a note that is still the <em>last</em> thing in the transcript — printed it
    /// up to five times a run.</para>
    /// <para>Per tile, and deliberately <b>not</b> reset by <c>StartFreshGoal</c> beside
    /// <c>_clarifyBudgetReported</c>: this one is about the tile having nowhere to ask, which a new goal
    /// does not change.</para>
    /// </summary>
    private bool _verifyConsentUnavailableReported;

    /// <summary>Whether this goal has already been told why it is not getting more questions. Reset
    /// with the goal, not with the tile.</summary>
    private bool _clarifyBudgetReported;

    private GoalCriteriaEditor NewCriteriaEditor() => new(
        () => _engine.Criteria,
        criteria =>
        {
            // Read before the assignment: what they say about the state the engine is still holding.
            var commandChanged = !string.Equals(
                _engine.Criteria.VerifyCommand, criteria.VerifyCommand, StringComparison.Ordinal);
            var attemptsChanged = _engine.Criteria.MaxIterations != criteria.MaxIterations;

            _engine.Criteria = criteria;

            // Moving the field by hand makes the number theirs again. AttemptsBeforeExtension exists so
            // the next goal starts from what the user chose rather than from what Continue wrote — and
            // once they have chosen again, the remembered value is the stale one: 5, Continue to 10,
            // then 8 typed in, and the next goal started at 5. Only reached from the panel; Continue
            // writes through the engine and reloads with _filling set, so it never comes through here.
            if (attemptsChanged)
                _engine.AttemptsBeforeExtension = null;

            // The output belonged to the command that printed it. Left in place across an edit, a build
            // error from `dotnet build` was handed to the next implementation under the heading "the
            // project's verify command failed with this output" while the command was now `npm test` —
            // or had been removed entirely, in which case nothing would ever have printed it again.
            if (commandChanged)
                _engine.LastVerifyOutput = null;

            // The Continue button names the number it will add, and that number is this field. The
            // reason with it: clearing the verify command is what makes the button appear after a
            // timeout, so the bar above it has to be there to be read.
            OnPropertyChanged(nameof(ContinueLabel));
            OnPropertyChanged(nameof(ContinueReason));
            OnPropertyChanged(nameof(CanContinue));

            // Messages alone, and no File.Exists: this runs on every keystroke in a text box, and a
            // tile with no messages is one with no goal in it — which is exactly the tile that must not
            // be given a session file. A saved tile always has messages.
            if (Messages.Count > 0)
                SaveStateSoon();
        });

    [RelayCommand]
    private void ToggleCriteria() => ShowCriteria = !ShowCriteria;

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
            Trace.TraceWarning($"Goal continue error: {ex.Message}");
            await AddMessageAsync(GoalMessageRole.System, $"Unexpected error: {ex.Message}", CurrentPhase);
        }
    }

    // ── Detecting a goal from the working tree ──────────

    /// <summary>
    /// Asks git whether there is anything to detect a goal from, without making anybody wait for it.
    /// <para>Fire and forget on purpose: the answer only decides whether two buttons are shown, and the
    /// user is not blocked on either.</para>
    /// <para><b>It is asked at moments, not continuously</b>, and the moments are named rather than
    /// implied: when the tile is built, when a run ends, when a detection finds nothing, and when a
    /// fresh goal is started. It is emphatically <em>not</em> watching the working tree — the changes
    /// it asks about are made in the terminal tiles next door, so between those moments the answer can
    /// be stale and the buttons can be missing from a tree that has since acquired changes, or offered
    /// over one that has since been committed. Both are recoverable in a click; a filesystem watcher
    /// over an entire worktree, per Goal tile, is not a price worth paying for a button's visibility,
    /// and the run itself re-reads the tree rather than trusting this.</para>
    /// </summary>
    private void RefreshDetectAvailability()
    {
        // The workflow keeps unwinding after Dispose and its finally asks for one of these. There is
        // nothing left to show the answer to.
        if (_disposed) return;

        var token = _lifetime.Token;

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

    [RelayCommand]
    private Task DetectGoalAsync() => DetectAsync(andRun: false);

    /// <summary>Work out the goal and go straight into the fix loop, without a plan and without a
    /// clarification round — the "I know what I am doing, finish it" path.</summary>
    [RelayCommand]
    private Task DetectGoalAndRunAsync() => DetectAsync(andRun: true);

    private async Task DetectAsync(bool andRun)
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
            await WorkingAsync(() => RunDetectAsync(andRun));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Goal detection error: {ex.Message}");
            await AddMessageAsync(GoalMessageRole.System, $"Unexpected error: {ex.Message}", CurrentPhase);
        }

        RefreshDetectAvailability();
    }

    private async Task RunDetectAsync(bool andRun)
    {
        // The tree is read *before* the transcript is cleared, and that order is the whole of it. The
        // button is shown on the strength of `git status`, which is a different command from the one
        // that builds the prompt: a commit made between the two, an exclusion the two apply
        // differently, a repository with no HEAD — any of them ends with nothing to detect from, and
        // clearing first meant the user had paid for that with their session.
        Working("Reading the working tree...");

        WorktreeSnapshot tree;
        try
        {
            tree = await ReadWorktreeAsync();
        }
        catch (OperationCanceledException)
        {
            // As in RunVerifyAsync: the label has to come back, or the strip keeps saying the tile is
            // reading a working tree it stopped reading.
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
        var run = await RunAiAsync(_engine.BuildDetectGoalPrompt(tree.Text!, PromptBudget()));

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

        // Started once, with what it is actually starting. The detect-and-run path used to call this
        // with "" and then again with the goal a few lines later.
        StartFreshGoal(andRun ? goal : "");
        SyncFromEngine(save: File.Exists(_filePath));

        if (!andRun)
        {
            // Into the composer, not into the transcript. A detected goal is a draft — it is the tool's
            // reading of half-finished work, and the user is the only one who knows what the other half
            // was meant to be. Putting it in the transcript would make it a decision already taken.
            //
            // Never over something already in the box, which is the same rule the clarification skeleton
            // follows in RunClarifyAsync and it is owed here for the same reason: the composer stays
            // editable throughout — only the Send button is disabled while the tile works — so a
            // detection that takes as long as the tool takes is a window in which the user can type, and
            // the answer arriving must not delete what they wrote. Asked of the box as it is *now*
            // rather than against a snapshot taken at the click: text that was already there when they
            // clicked is theirs too, and the two cases deserve the same answer.
            if (string.IsNullOrWhiteSpace(InputText))
            {
                InputText = goal;
                await AddMessageAsync(GoalMessageRole.System,
                    "Detected this goal from your uncommitted changes. Edit it if you like, then press " +
                    "Send.", GoalPhase.Goal);
            }
            else
            {
                // It still has to land somewhere it can be read and copied from. Keeping the user's text
                // must not turn the click into nothing at all — that is the outcome a button must
                // never have, and the reason DetectAsync has a catch of last resort in the first place.
                await AddMessageAsync(GoalMessageRole.System,
                    $"Detected this goal from your uncommitted changes:\n\n{goal}\n\n" +
                    "Your own text is still in the composer. Send it, or clear the box and detect again " +
                    "to have this put there.", GoalPhase.Goal);
            }

            PhaseLabel = _engine.GetPhaseLabel();
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
        await AddMessageAsync(GoalMessageRole.User, goal, GoalPhase.Goal);
        _engine.CurrentPhase = GoalPhase.Review;
        SyncFromEngine();

        await RunImplementReviewLoopAsync(startAtReview: true);
    }

    // ── Phase dispatch ──────────────────────────────────

    [RelayCommand]
    private async Task Submit()
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
                    SyncFromEngine();
                    await AddMessageAsync(GoalMessageRole.User, text, GoalPhase.Goal);
                    await WorkingAsync(RunClarifyAsync);
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
            Trace.TraceWarning($"Goal workflow error: {ex.Message}");
            await AddMessageAsync(GoalMessageRole.System, $"Unexpected error: {ex.Message}", CurrentPhase);
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
        // showing the old questions, with the approval panel suppressed behind them (ShowApproval
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

        // Structured questions are asked in the panel, which owns a box per question — so they are
        // deliberately *not* put in the transcript here. They go in when they are answered, together
        // with the answer, which keeps the record in the order it always read while leaving the screen
        // to the conversation. Three questions and their reasons is most of a small tile.
        //
        // Prose is the other half of the rule and keeps the behaviour it always had: there is nothing
        // to build a panel from, so it is a message and the composer answers it.
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
                    // arrives, so the approval panel appeared by luck — luck that runs out the day
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

            while (GoalLoopPolicy.NextAttempt(_engine.IterationCount, _engine.MaxIter, finishing) is { } attempt)
            {
                _engine.IterationCount = attempt;
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
                    var impl = await RunLoopPhaseAsync(
                        GoalPhase.Implement,
                        $"AI is implementing (attempt {attempt}/{_engine.MaxIter})...",
                        tree => _engine.BuildImplementPrompt(tree, PromptBudget()));

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
                    // this point on can be interrupted, the verify command most of all: it is minutes
                    // of build, it is cancelled by Pause, and leaving Implement standing through it
                    // meant Resume ran the whole implementation again over a worktree that already had
                    // its changes.
                    _engine.CurrentPhase = GoalPhase.Review;
                    SyncFromEngine();

                    if (PauseRequested)
                    {
                        PhaseLabel = _engine.GetPhaseLabel();
                        return;
                    }

                    // Asked here, before the verify command and before the review, and that ordering is
                    // the whole point of the extra read. The tree the review is handed is read *after*
                    // the verify command has run, so a command that regenerates a tracked file — a
                    // build, a formatter, a snapshot test — made the two trees differ and quietly
                    // disarmed this stop in exactly the workspaces most likely to have one configured.
                    //
                    // Two short git processes against a lap that costs minutes of AI, and they pay for
                    // themselves the moment this fires: there is no sense building and reviewing a
                    // change that was never made.
                    if (await ImplementationChangedNothingAsync(treeBeforeImplement))
                    {
                        stopReason = GoalStopReason.NoChange;
                        break;
                    }
                }

                var verify = await RunVerifyAsync(criteria.VerifyCommand);
                if (verify == null) return;

                // A verification that had to be killed ends the run rather than being tried again. The
                // timeout is already half an hour, so the attempts still on the budget are hours of
                // waiting for the same answer — and the answer is unusable either way: a command that
                // never finished says nothing about whether the goal is met, so every one of those
                // attempts would end at the same gate. Reviewing this attempt first would spend a run
                // arguing about a build nobody has seen the result of.
                // Kept for the next implementation, not only for the review. The reviewer used to be the
                // only one shown the compiler's own words, and what reached whoever had to fix the build
                // was the reviewer's account of them: a line and column turned into "there is a type
                // mismatch somewhere in the cart code". Cleared on a pass, so a build fixed on attempt 2
                // is not still being explained on attempt 5.
                _engine.LastVerifyOutput = verify is { Ran: true, Succeeded: false, Output.Length: > 0 }
                    ? verify.Value.Output
                    : null;

                if (verify is { TimedOut: true })
                {
                    stopReason = GoalStopReason.VerifyTimedOut;
                    break;
                }

                var reviewRun = await RunLoopPhaseAsync(
                    GoalPhase.Review,
                    "AI is reviewing changes...",
                    tree => _engine.BuildReviewPrompt(tree, verify.Value.Output, PromptBudget()),
                    // The raw answer is not what goes in the transcript. It is read first and written
                    // back as a list of findings, because the prose around a JSON block says the same
                    // things at greater length and printing both means reading every review twice.
                    addMessage: false);

                if (reviewRun is not { } reviewed) return;

                var review = GoalResponseParser.ParseReview(reviewed.Text);
                ShowFindings(review);
                await AddMessageAsync(GoalMessageRole.Assistant,
                    GoalTranscript.Review(review, verify, criteria.RequireGoalMet), GoalPhase.Review);

                if (GoalCompletionPolicy.IsMet(review, verify, criteria))
                {
                    _engine.ClearReviewFeedback();
                    stopReason = GoalStopReason.Met;
                    break;
                }

                // Only the errors and warnings go back, and only as findings. The whole review used to,
                // nits and prose included, so an attempt could be spent renaming a variable while the
                // null dereference above it stayed exactly where it was.
                _engine.RecordReviewFeedback(GoalTranscript.Feedback(review));
                outstanding = GoalCompletionPolicy.WhyNotMet(review, verify, criteria);

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

            await ShowSummaryAsync(stopReason, outstanding);
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
    /// <para>Reads the tree again rather than reusing the review's copy, which is read after the verify
    /// command and so carries whatever that changed. A cancellation answers <c>false</c>: a pause is
    /// not evidence about the implementation, and the pause is handled at the next hand-over anyway.
    /// </para>
    /// </summary>
    private async Task<bool> ImplementationChangedNothingAsync(WorktreeSnapshot treeBeforeImplement)
    {
        try
        {
            // Both reads have to have worked. Comparing the *text* alone answered yes for a workspace
            // that is not a repository at all — where every read produces the same nothing — so every
            // goal ended after one attempt, told the user the implementation had changed nothing, and
            // was confidently wrong.
            return (await ReadWorktreeAsync()).ProvablyUnchangedFrom(treeBeforeImplement);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs the tile's verify command, or answers "not run" when there is not one. Null means the user
    /// paused during it.
    /// </summary>
    private async Task<VerifyOutcome?> RunVerifyAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return VerifyOutcome.NotRun();

        if (PauseRequested)
        {
            PhaseLabel = _engine.GetPhaseLabel();
            return null;
        }

        // No reason attached: ConsentToVerifyCommandAsync has already written the explanation into the
        // transcript, and a Problem here would have the caller print a second one underneath it.
        if (!await ConsentToVerifyCommandAsync(command))
            return VerifyOutcome.NotRun();

        // Asked again on the way out of the dialog. The check above happened before it, and the dialog
        // is awaited: the panel stays usable while it is on screen, so Pause can be pressed between the
        // question and the answer — and the build started anyway, which is the one thing Pause exists
        // to stop.
        if (PauseRequested)
        {
            PhaseLabel = _engine.GetPhaseLabel();
            return null;
        }

        Working("Running the verify command...");

        VerifyOutcome outcome;
        try
        {
            outcome = await new VerifyCommandRunner(_workingDirectory, _settingsService.Settings)
                .RunAsync(command, _cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Or the strip is left saying "Running the verify command..." over a tile with nothing
            // running in it — which is what the user sees for as long as the tile stays open.
            PhaseLabel = _engine.GetPhaseLabel();
            return null;
        }

        // Said only when there is something to say. A command that passed is reported by the review
        // line that follows it, and repeating it here would put a line in the transcript after every
        // successful build for the life of the goal.
        if (outcome is { TimedOut: true, Problem: { Length: > 0 } killed })
            // Not "the review will go ahead without it": it will not. A verification that never finished
            // is about the work, unlike a missing shell, and the loop stops on it.
            await AddMessageAsync(GoalMessageRole.System,
                $"The verify command never finished ({killed}).", GoalPhase.Review);
        else if (outcome.Problem is { Length: > 0 } problem)
            await AddMessageAsync(GoalMessageRole.System,
                $"The verify command could not be run ({problem}). The review will go ahead without it.",
                GoalPhase.Review);
        else if (outcome is { Ran: true, Succeeded: false })
            await AddMessageAsync(GoalMessageRole.System,
                $"Verify command exited {outcome.ExitCode}. Its output goes to the review.",
                GoalPhase.Review);

        return outcome;
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

        // StartNewGoal empties the counts; this only has to take the badges down afterwards. Clearing
        // them here as well was the same reset written twice, in two files, with nothing keeping the
        // pair in step.
        _engine.StartNewGoal(goal);

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

    /// <summary>
    /// Asks once, per session, before running a verify command this tile did not watch the user type —
    /// see <see cref="_verifyCommandNeedsConsent"/> for why it is asked at all.
    /// <para>No dialog means <b>no</b>, as on the Settings dialog and for the same reason: an
    /// unanswered question is not a yes. Declining clears the command rather than skipping it once —
    /// otherwise the question returns on every attempt, and a question asked five times is one answered
    /// wrongly on the fifth.</para>
    /// </summary>
    private async Task<bool> ConsentToVerifyCommandAsync(string command)
    {
        // A command the user typed in this session is a command they chose.
        if (!_verifyCommandNeedsConsent || Criteria.VerifyCommandWasTyped) return true;

        // Too long to show is too long to approve. Asking about a command while hiding part of it is
        // worse than not asking: it collects a yes for something nobody saw. There is no elision
        // anywhere in this path — a command that will not fit in the question is refused, and the user
        // is told to shorten it in a panel where they can see the whole thing.
        if (!CommandDisplay.CanBeConsentedTo(command))
        {
            _verifyCommandNeedsConsent = false;
            _engine.Criteria.VerifyCommand = "";
            _engine.LastVerifyOutput = null;
            Criteria.Reload();
            SaveStateNow();

            await AddMessageAsync(GoalMessageRole.System,
                $"This goal's verify command is {CommandDisplay.ForDialog(command).Length} characters " +
                "long — too long " +
                "to show you in full, and this tile will not ask you to approve something it has to " +
                "hide half of. It has been removed. Set a shorter one under the tune button.",
                CurrentPhase);

            return false;
        }

        // Nowhere to ask. The command is not run — an unanswered question is not a yes — but neither is
        // it thrown away: "I could not ask" and "they said no" are the same answer about *running* it
        // and opposite answers about *keeping* it, and deleting somebody's setting because a dialog was
        // not wired is a decision nobody made. The flag stays up, so a later run with a window in front
        // of it asks properly.
        if (ConfirmAction == null)
        {
            if (!_verifyConsentUnavailableReported)
            {
                _verifyConsentUnavailableReported = true;
                await AddMessageAsync(GoalMessageRole.System,
                    $"This goal carries a verify command (`{CommandDisplay.ForDialog(command)}`) that has not been " +
                    "approved, and this tile cannot ask. It will be skipped.", CurrentPhase);
            }

            return false;
        }

        var agreed = await ConfirmAction("This goal was saved with a verify command:\n\n" +
                                         $"{CommandDisplay.ForDialog(command)}\n\n" +
                                         "Run it in this workspace after every attempt?");

        _verifyCommandNeedsConsent = false;

        if (agreed) return true;

        // Only if it is still the command that was asked about. The dialog is awaited, and the panel is
        // usable while it is on screen: somebody who answers "no" to the command out of the file and
        // then types their own would have had the new one deleted by this line, in the name of a
        // refusal that was never about it. A command they have just typed is one they chose, so there
        // is nothing left to refuse either.
        if (!string.Equals(_engine.Criteria.VerifyCommand, command, StringComparison.Ordinal))
            return false;

        // An actual refusal removes it, rather than skipping it once: otherwise the question returns on
        // every attempt, and a question asked five times is one answered wrongly on the fifth.
        _engine.Criteria.VerifyCommand = "";
        _engine.LastVerifyOutput = null;
        Criteria.Reload();
        SaveStateNow();

        await AddMessageAsync(GoalMessageRole.System,
            "The verify command was not approved and has been removed from this goal. Set one under the " +
            "tune button if you want one.", CurrentPhase);

        return false;
    }

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
        ShowBadges();
    }

    private void ShowBadges()
    {
        Badges.Clear();

        // Positional, and only trusted at exactly the right length. The array is saved to disk, so a
        // file written before a severity existed would otherwise be read against the new ordinals and
        // put every count beside the wrong letter — a blocker reported as an error. A length that does
        // not match is from a different version of this enum: drop it, and the strip is blank until the
        // next review, which is a truth rather than a wrong number.
        var counts = _engine.LastReviewCounts;
        if (counts.Length != GoalSeverities.Length) return;

        for (var i = 0; i < GoalSeverities.Length; i++)
            if (counts[i] > 0)
                Badges.Add(new GoalBadge { Severity = GoalSeverities[i], Count = counts[i] });
    }

    private async Task ShowSummaryAsync(GoalStopReason reason, string? outstanding = null)
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

        // The label and the reason too, not only whether the button is there. Both are read from state
        // that has just moved: the label is the ceiling less the attempts already spent, and the reason
        // is the stop this method has just recorded.
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(ContinueLabel));
        OnPropertyChanged(nameof(ContinueReason));

        PhaseLabel = _engine.GetPhaseLabel();

        var summary = GoalCompletionPolicy.Summarise(reason, _engine.IterationCount, outstanding)
                      + "\nType a new goal, or start a fresh one with +.";

        await AddMessageAsync(GoalMessageRole.System, summary, GoalPhase.Summary);
    }

    // ── AI process execution ────────────────────────────

    /// <summary>
    /// One AI run: what it produced, and what happened to it.
    /// <para>The two travel together because they are one answer. They were three fields the caller had
    /// to remember to pass to <see cref="GoalLoopPolicy.Judge"/>, and each was added separately after
    /// its own bug — cancellation, then a missing tool, then a failed process — with a call site
    /// somewhere forgetting the newest one every time.</para>
    /// </summary>
    private readonly record struct AiRun(GoalRunVerdict Verdict, string? Text);

    private async Task<AiRun> RunAiAsync(string prompt)
    {
        // Looked for again before giving up. Detection runs once, when the tile is built, so a tool
        // installed after that stayed invisible for the life of the tile — and the message telling the
        // user to install it and click Resume then sent them round the same loop for ever.
        if (_resolvedTool?.ExecutablePath == null)
            await RediscoverSelectedToolAsync();

        if (_resolvedTool?.ExecutablePath == null)
        {
            await AddMessageAsync(GoalMessageRole.System,
                "No AI tool available. Install Claude Code or another supported tool, then " + TryAgain(),
                CurrentPhase);
            return new AiRun(GoalLoopPolicy.Judge(null, cancelled: false, toolMissing: true), null);
        }

        // The token belongs to WorkingAsync, which holds one for the whole of a phase or a loop, so it
        // is not made here. Making one per AI call left it null in the gaps between calls — where Pause
        // had nothing to cancel — and left the git commands before each call uncancellable, so a pause
        // taken while the working tree was being read waited for both processes to finish.
        var token = _cts?.Token ?? CancellationToken.None;

        try
        {
            var result = AiRunnerFactory is { } run
                ? await run(_resolvedTool, prompt, _workingDirectory, token)
                : await AiProcessRunner.RunPlainAsync(
                    _resolvedTool.ExecutablePath,
                    prompt,
                    _workingDirectory,
                    AiProcessRunner.GetRunner(_resolvedTool.BinaryName),
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
                    ct: token);

            // The failure the tool reported about itself, carried beside its words rather than read out
            // of them. A run that ended in a turn limit or a refused key comes back with text in it —
            // an apology, a half-finished note — and judged on the text alone that was Answered: the
            // failure adopted as the plan, or as the review, and acted on.
            //
            // The text still goes into the message, because a failed implementation has usually already
            // written files and this is the only account of what is now in the worktree.
            if (result.Failed)
            {
                await AddMessageAsync(GoalMessageRole.System,
                    $"The AI tool reported a failure. {Capitalised(TryAgain())}\n\n{result.Text}",
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
                $"The AI tool failed: {ex.Message}. {Capitalised(TryAgain())}", CurrentPhase);
            return new AiRun(GoalLoopPolicy.Judge(null, cancelled: false, failed: true), null);
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
        GoalPhase phase, string runningLabel, Func<string?, string> buildPrompt, bool addMessage = true)
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
            tree = await ReadWorktreeAsync();
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

        var run = await RunAiAsync(buildPrompt(tree.Text));

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
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try { await work(); }
        finally
        {
            IsRunning = false;

            // Here rather than at each call site: a run ends five ways — finished, paused, cancelled,
            // failed, thrown — and a tile left showing the last file the tool happened to open is one
            // that looks busy while it waits for you.
            Activity = "";

            _cts?.Dispose();
            _cts = null;
        }
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
    /// run — <c>RediscoverSelectedToolAsync</c> can find one mid-loop — and because the answer depends
    /// on the executable's own path length. Null when a test has replaced the runner: there is no
    /// command line in that case either.</para>
    /// </summary>
    private int? PromptBudget()
    {
        // A test has replaced the runner: there is no command line to fit.
        if (AiRunnerFactory != null) return null;

        if (_resolvedTool?.ExecutablePath is { } path)
            return AiProcessRunner.PromptBudget(path, AiProcessRunner.GetRunner(_resolvedTool.BinaryName));

        // No tool resolved *yet* — and RunAiAsync scans again before giving up, so one may well be found
        // a moment from now. The prompt is already built by then and cannot be rebuilt, so answering
        // "no limit" here meant a prompt fitted to nothing being handed to a .cmd shim, refused by the
        // guard, and reproduced identically on every Resume. The tightest of the two Windows limits is
        // the safe assumption: it costs some context in a case that may not even arise, where the other
        // answer costs the run.
        return CommandLineLength.Tightest();
    }

    /// <summary>The working tree, as the prompts see it. Read through <see cref="WorktreeReader"/>,
    /// which owns the git commands and the seam that lets a test do without them.</summary>
    private Task<WorktreeSnapshot> ReadWorktreeAsync() =>
        NewWorktreeReader().ReadAsync(_cts?.Token ?? CancellationToken.None);

    private WorktreeReader NewWorktreeReader() =>
        new(_workingDirectory,
            _settingsService.Settings.GitPath is { Length: > 0 } p ? p : "git");

    // ── Commands ────────────────────────────────────────

    [RelayCommand]
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
            Trace.TraceWarning($"Goal resume error: {ex.Message}");
            await AddMessageAsync(GoalMessageRole.System, $"Unexpected error: {ex.Message}", CurrentPhase);
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
        // created an empty session in .mterminal/goals/, which is the thing the guard in Dispose is
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
    /// <summary>The label a phase runs under, and the end of whatever the last phase was doing. The
    /// clear belongs with the label because they change together: without it the strip named a file
    /// from the implementation for the whole of a verify command, which can run for half an hour —
    /// a tile looking busy about something that finished.</summary>
    private void Working(string label)
    {
        PhaseLabel = label;
        Activity = "";
    }

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
    private async Task AddMessageAsync(GoalMessageRole role, string text, GoalPhase phase,
        bool markdown = false)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Messages.Add(new GoalMessage
                { Role = role, Text = text, Phase = phase, Markdown = markdown });
            ScrollToEnd?.Invoke();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Messages.Add(new GoalMessage
                    { Role = role, Text = text, Phase = phase, Markdown = markdown });
                ScrollToEnd?.Invoke();
            });
        }

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
            ? _engine.ToState([..Messages], SelectedToolName)
            : Dispatcher.UIThread.Invoke(() => _engine.ToState([..Messages], SelectedToolName)),

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

            // Assigned only when it names a tool that is here. Anything else — a name that has since
            // been uninstalled, or the empty string a state written before a tool was ever chosen
            // carries — runs OnSelectedToolNameChanged, finds nothing, and clears the working tool
            // DetectTools has just picked.
            var savedTool = state.SelectedToolName;
            var savedToolIsGone = savedTool.Length > 0 && !AvailableTools.Contains(savedTool);
            if (savedTool.Length > 0 && !savedToolIsGone)
                SelectedToolName = savedTool;

            foreach (var m in state.Messages)
                Messages.Add(m);

            ShowBadges();

            // The questions a closed tile was waiting on. This is what the pending set is persisted
            // for — a panel built from a parsed answer would not survive the tile being closed, and the
            // goal would come back waiting for an answer to questions nobody could see any more.
            SyncQuestions();

            // After the transcript, not before it: a note about this session belongs at the end of the
            // session, and said first it sat above everything the user had ever typed into this tile.
            if (savedToolIsGone)
                // The substitute is read from _resolvedTool, not from SelectedToolName: that property
                // holds the "(no AI tools detected)" placeholder when nothing is installed, and naming
                // it offered the placeholder as the tool that would be used instead.
                Say(_resolvedTool != null
                    ? $"The tool this goal was using ({savedTool}) is not installed. " +
                      $"{_resolvedTool.Name} will be used instead."
                    : $"The tool this goal was using ({savedTool}) is not installed, and " +
                      "no other AI tool was found. Install one and click Resume.",
                    aboutThisSession: true);

            // Said out loud, because it is a shell command that arrived in a file and will be run
            // without the user typing it again. Goal files live in `.mterminal/goals/` inside the
            // user's own repository, nothing gitignores that directory unless the Git tile is used, and
            // a committed one travels with the branch. Naming it is the difference between a command
            // the user chose and one they merely inherited. This line is a notice, not a barrier: the
            // barrier is the dialog, and it comes before the command is ever run.
            if (_engine.Criteria.VerifyCommand is { Length: > 0 } verify)
            {
                _verifyCommandNeedsConsent = true;

                // One that cannot be shown is named by its length instead. The transcript may elide
                // where the dialog may not — but the transcript is reserialised into the goal file on
                // every save, so a command of a hundred kilobytes would be carried there twice over.
                // The consent gate refuses it anyway; this only has to say what is in the file.
                Say(CommandDisplay.CanBeConsentedTo(verify)
                    ? $"This goal carries a verify command: `{CommandDisplay.ForDialog(verify)}`. You will be " +
                      "asked before it runs."
                    : $"This goal carries a verify command of {CommandDisplay.ForDialog(verify).Length} " +
                      "characters — too long to show you in full, so it will not be run. Set a shorter " +
                      "one under the tune button.",
                    aboutThisSession: true);
            }

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
    }
}
