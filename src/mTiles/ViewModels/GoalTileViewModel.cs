using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;

namespace mTiles.ViewModels;

public partial class GoalTileViewModel : ObservableObject, IDisposable
{
    private readonly string _workingDirectory;
    private readonly SettingsService _settingsService;
    private readonly GoalWorkflowEngine _engine = new();
    private readonly GoalStatePersistence _persistence = new();
    private readonly string _filePath;

    private CancellationTokenSource? _cts;

    /// <summary>Said once. A tile that cannot save says so, but a tile that cannot save is also likely
    /// to fail on every message after that, and a transcript is not a log.
    /// <para>Read and written from the debounce timer's thread as well as the UI one, deliberately
    /// without a lock: the worst a lost race costs is the same sentence twice, and <c>_disposed</c> is
    /// locked because losing <em>that</em> race leaks a timer and writes after the tile is gone.</para>
    /// </summary>
    private bool _saveFailureReported;

    private Timer? _debounceTimer;
    private readonly Lock _debounceLock = new();

    /// <summary>Set at the start of <see cref="Dispose"/>. The workflow keeps unwinding after the tile
    /// is closed — the cancelled run still adds its message — and each of those asked for a save, which
    /// armed a fresh timer after the final flush had already gone out: a write belonging to a tile that
    /// no longer exists, on a timer nobody would ever dispose.</summary>
    private bool _disposed;

    /// <summary>
    /// Set when the goal file exists but could not be opened, which stops this tile writing for the
    /// rest of its life. The file is almost certainly intact — locked, or on a disk having a bad
    /// moment — and the tile standing in front of it is empty, so a save would replace a real session
    /// with the blank one that failed to load it. Refusing to write costs the user this session; not
    /// refusing costs them the one already on disk.
    /// </summary>
    /// <remarks>Same benign race as <see cref="_saveFailureReported"/>: a write that slips through the
    /// instant it is set is a write of the state the tile is already showing.</remarks>
    private bool _saveRefused;
    private List<AiToolInfo>? _cachedTools;

    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private GoalPhase _currentPhase = GoalPhase.Goal;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _selectedToolName = "Claude Code";
    [ObservableProperty] private string _phaseLabel = "Waiting for goal...";
    [ObservableProperty] private bool _isPaused;

    public ObservableCollection<GoalMessage> Messages { get; } = [];
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
    internal static Func<AiToolInfo, string, string, CancellationToken, Task<string>>? AiRunnerFactory { get; set; }

    public string FilePath => _filePath;

    public GoalTileViewModel(string workingDirectory, SettingsService settingsService)
    {
        _workingDirectory = workingDirectory;
        _settingsService = settingsService;

        var goalsDir = Path.Combine(workingDirectory, ".mterminal", "goals");
        _filePath = Path.Combine(goalsDir, $"{Guid.NewGuid():N}.json");

        DetectTools();
    }

    public GoalTileViewModel(string filePath, string workingDirectory, SettingsService settingsService)
    {
        _workingDirectory = workingDirectory;
        _settingsService = settingsService;
        _filePath = filePath;

        DetectTools();
        LoadState();
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
                    Messages.Clear();
                    _engine.StartNewGoal(text);
                    SyncFromEngine();
                    await AddMessageAsync(GoalMessageRole.User, text, GoalPhase.Goal);
                    await WorkingAsync(RunClarifyAsync);
                    break;

                case GoalPhase.Clarify:
                    // The phase moves before the answer is written, so the two are never on disk apart.
                    // Between them the file said Clarify with the user's message last, which reads as an
                    // interrupted Clarify — and a restart there resumed by asking the questions again
                    // instead of planning.
                    _engine.RecordClarification(text);
                    _engine.CurrentPhase = GoalPhase.Plan;
                    SyncFromEngine();
                    await AddMessageAsync(GoalMessageRole.User, text, GoalPhase.Clarify);
                    await WorkingAsync(RunPlanAsync);
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

    private async Task RunClarifyAsync() =>
        await RunPhaseAsync(GoalPhase.Clarify, "AI is asking clarifying questions...", _engine.BuildClarifyPrompt());

    private async Task RunPlanAsync() =>
        await RunPhaseAsync(GoalPhase.Plan, "AI is creating a plan...", _engine.BuildPlanPrompt());

    /// <summary>
    /// Runs one of the two phases the user answers, and leaves the tile saying where it now stands.
    /// <para>What each phase says when it is waiting comes from <see cref="GoalWorkflowEngine
    /// .GetPhaseLabel"/> alone. It used to be passed in as a success label and a fallback label as well,
    /// so the same four sentences existed in three places — here, in the engine, and in the summary —
    /// with nothing keeping them in step.</para>
    /// </summary>
    private async Task RunPhaseAsync(GoalPhase phase, string runningLabel, string prompt)
    {
        // A new planning run forgets the last proposal before it starts. Without this, a plan the user
        // rejected outlived the rejection: back through Clarify to Plan, a second run that produced
        // nothing left the old plan standing, and "ok" approved the one they had just turned down.
        if (phase == GoalPhase.Plan)
            _engine.RecordProposedPlan(null);

        _engine.CurrentPhase = phase;
        SyncFromEngine();
        PhaseLabel = runningLabel;

        var run = await RunAiAsync(prompt);
        switch (run.Verdict)
        {
            case GoalRunVerdict.Answered:
                if (phase == GoalPhase.Plan)
                    _engine.RecordProposedPlan(run.Text!);

                await AddMessageAsync(GoalMessageRole.Assistant, run.Text!, phase);
                PhaseLabel = _engine.GetPhaseLabel();
                break;

            case GoalRunVerdict.NoTool:
            case GoalRunVerdict.Failed:
                PauseAndWait();
                break;

            case GoalRunVerdict.Cancelled:
                // Stopped on purpose, so the phase stays where it is: Resume re-runs this phase, and a
                // restart finds the tile exactly here. Falling back would have moved the tile backwards
                // for a run the user paused, and the message would have blamed the tool for it.
                PhaseLabel = _engine.GetPhaseLabel();
                break;

            case GoalRunVerdict.Empty:
                // The same answer the loop gives, which it did not used to. Falling back a phase was
                // worse than it looked: from Clarify it landed in Goal, where the next thing sent
                // clears the transcript and starts a new goal — so a tool that replied with nothing
                // put the session one keystroke from being thrown away.
                await AddMessageAsync(GoalMessageRole.System,
                    "The tool returned nothing. Click Resume to try again.", phase);
                PauseAndWait();
                break;

            // Named rather than defaulted, here as in RunLoopPhaseAsync: a verdict added later would
            // otherwise fall into the branch that moves the tile backwards a phase.
            default:
                throw new UnreachableException($"Unhandled verdict for {phase}.");
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
            var passed = false;

            while (GoalLoopPolicy.NextAttempt(_engine.IterationCount, _engine.MaxIter, finishing) is { } attempt)
            {
                _engine.IterationCount = attempt;
                finishing = false;

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
                    var implResult = await RunLoopPhaseAsync(
                        GoalPhase.Implement,
                        $"AI is implementing (iteration {attempt}/{_engine.MaxIter})...",
                        tree => _engine.BuildImplementPrompt(tree));

                    if (implResult == null) return;

                    // The implementation is done and only the review is owed, so the phase moves before
                    // the pause is honoured. Stopping while still in Implement had Resume start the
                    // whole implementation again — against a worktree that already had its changes.
                    if (PauseRequested)
                    {
                        _engine.CurrentPhase = GoalPhase.Review;
                        SyncFromEngine();
                        PhaseLabel = _engine.GetPhaseLabel();
                        return;
                    }
                }

                var reviewResult = await RunLoopPhaseAsync(
                    GoalPhase.Review,
                    "AI is reviewing changes...",
                    tree => _engine.BuildReviewPrompt(tree));

                if (reviewResult == null) return;

                if (GoalWorkflowEngine.IsVerdictPass(reviewResult))
                {
                    _engine.ClearReviewFeedback();
                    passed = true;
                    break;
                }

                _engine.RecordReviewFeedback(reviewResult);
                if (PauseRequested)
                {
                    // The review has run and asked for another pass, so what is owed is the next
                    // implementation. Stopping while still in Review had Resume re-run the review it
                    // had just recorded: an AI run spent on an unchanged tree, and the same verdict
                    // twice in the transcript. When the budget is spent there is no next attempt to
                    // move to, and staying put is then the honest answer.
                    if (GoalLoopPolicy.NextAttempt(_engine.IterationCount, _engine.MaxIter, false) is not { } pending)
                    {
                        // Nothing is left to pause: the budget is spent and this review was the last
                        // thing the run had to do. Leaving it paused offered a Resume that could only
                        // re-run the review it had just finished, for one AI run and the same verdict
                        // twice, before summarising anyway.
                        await ShowSummaryAsync($"Stopped after {_engine.MaxIter} attempts without a passing review.");
                        return;
                    }

                    _engine.IterationCount = pending;
                    _engine.CurrentPhase = GoalPhase.Implement;
                    SyncFromEngine();
                    PhaseLabel = _engine.GetPhaseLabel();
                    return;
                }

                // The next attempt's number comes from the same rule that decides whether there is one,
                // rather than from a second copy of the arithmetic that could disagree with it.
                if (GoalLoopPolicy.NextAttempt(_engine.IterationCount, _engine.MaxIter, false) is { } next)
                    await AddMessageAsync(GoalMessageRole.System, $"Review found issues. Re-implementing (attempt {next})...", GoalPhase.Review);
            }

            // Falling out of the loop is the budget running out, which is not the same thing as the
            // review passing — and saying "goal completed after 5 iterations" for it told the user the
            // opposite of what had happened.
            await ShowSummaryAsync(passed
                ? null
                : $"Stopped after {_engine.MaxIter} attempts without a passing review.");
        }
        finally
        {
            SaveStateNow();
        }
    }

    private async Task ShowSummaryAsync(string? reason = null)
    {
        // The pause goes with it. Every route into a summary except a clean finish arrives with a
        // pause outstanding — the budget spent at a pause, a run stopped and then summarised — and a
        // Summary that still calls itself paused labels the tile "Paused. Click Resume to continue."
        // over a Resume that has nothing to do, and keeps saying so after a restart.
        _engine.IsPaused = false;
        _engine.CurrentPhase = GoalPhase.Summary;
        SyncFromEngine();
        PhaseLabel = _engine.GetPhaseLabel();

        var summary = reason != null
            ? $"{reason} Completed {_engine.IterationCount} iteration(s).\nType a new goal, or start a fresh one with +."
            : $"Goal completed after {_engine.IterationCount} iteration(s).\nType a new goal, or start a fresh one with +.";

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
                "No AI tool available. Install Claude Code or another supported tool, then click Resume.",
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
                    ct: token);

            return new AiRun(GoalLoopPolicy.Judge(result, cancelled: false), result);
        }
        catch (OperationCanceledException)
        {
            // Every cancellation now has a pause recorded before it — Pause and Dispose both set it
            // first — so there is one message rather than a branch, and the branch that said "Operation
            // cancelled." is gone with the window that made it reachable.
            await AddMessageAsync(GoalMessageRole.System, "Paused. Click Resume to continue.", CurrentPhase);
            return new AiRun(GoalLoopPolicy.Judge(null, cancelled: true), null);
        }
        catch (Exception ex)
        {
            await AddMessageAsync(GoalMessageRole.System,
                $"The AI tool failed: {ex.Message}. Click Resume to try again.", CurrentPhase);
            return new AiRun(GoalLoopPolicy.Judge(null, cancelled: false, failed: true), null);
        }
    }

    /// <summary>
    /// One phase of the implement/review loop: move into it, read the working tree, ask the tool, and
    /// decide what its answer means. Returns the answer, or <c>null</c> when the loop must stop — every
    /// reason to stop having already been acted on here.
    /// <para>The two phases differed in a name, a label and which prompt to build, and were otherwise
    /// the same twenty lines twice. Which mattered: the NoTool case was added to both by hand, and the
    /// cancelled case was fixed in one of them first.</para>
    /// </summary>
    private async Task<string?> RunLoopPhaseAsync(
        GoalPhase phase, string runningLabel, Func<string?, string> buildPrompt)
    {
        if (PauseRequested) return null;

        _engine.CurrentPhase = phase;
        SyncFromEngine();
        PhaseLabel = runningLabel;

        // Asked again after the working tree is read and before the tool is launched. Reading the tree
        // is two short git processes, but the run after it is minutes, and a pause arriving in that
        // window used to be paid for with the whole of it.
        string? tree;
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
            return null;
        }

        if (PauseRequested) return null;

        var run = await RunAiAsync(buildPrompt(tree));

        // The verdict, not the text. A tool that answers with whitespace returns a string that is not
        // null, so asking about the string put an empty assistant bubble in the transcript and then
        // summarised the run underneath it.
        if (run.Verdict == GoalRunVerdict.Answered)
            await AddMessageAsync(GoalMessageRole.Assistant, run.Text!, phase);

        switch (run.Verdict)
        {
            case GoalRunVerdict.Answered:
                return run.Text;

            case GoalRunVerdict.NoTool:
            case GoalRunVerdict.Failed:
                PauseAndWait();
                return null;

            // A cancelled run is not a finished one. Summarising it moved the tile into Summary, which
            // Resume has no case for and WasInterrupted does not recognise — so pausing an
            // implementation was a one-way door, both in the session and after a restart. Stopping
            // here leaves the phase as it is, which both understand.
            case GoalRunVerdict.Cancelled:
                return null;

            case GoalRunVerdict.Empty:
                // Paused, not summarised: a tool that returned nothing once may answer the next time,
                // which is the argument that has Failed pause rather than end the goal.
                await AddMessageAsync(GoalMessageRole.System,
                    "The tool returned nothing. Click Resume to try again.", phase);
                PauseAndWait();
                return null;

            // Named rather than defaulted: a verdict added later would otherwise be silently treated
            // as an empty answer, which is the one outcome that ends the run and summarises it.
            default:
                throw new UnreachableException($"Unhandled verdict for {phase}.");
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

    /// <summary>The working tree, as the prompts see it. Read through <see cref="WorktreeReader"/>,
    /// which owns the git commands and the seam that lets a test do without them.</summary>
    private Task<string?> ReadWorktreeAsync() =>
        new WorktreeReader(_workingDirectory,
                _settingsService.Settings.GitPath is { Length: > 0 } p ? p : "git")
            .ReadAsync(_cts?.Token ?? CancellationToken.None);

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

        Messages.Clear();
        _engine.StartNewGoal("");
        SyncFromEngine(save: hadFile);
        PhaseLabel = _engine.GetPhaseLabel();
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
    private void SyncFromEngine(bool save = true)
    {
        CurrentPhase = _engine.CurrentPhase;
        IsPaused = _engine.IsPaused;

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
    private async Task AddMessageAsync(GoalMessageRole role, string text, GoalPhase phase)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Messages.Add(new GoalMessage { Role = role, Text = text, Phase = phase });
            ScrollToEnd?.Invoke();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Messages.Add(new GoalMessage { Role = role, Text = text, Phase = phase });
                ScrollToEnd?.Invoke();
            });
        }

        SaveStateSoon();
    }

    // ── Persistence ─────────────────────────────────────

    /// <summary>
    /// Asks for a save shortly after the last change rather than on the spot.
    /// <para>Used for messages, which arrive in bursts and cost the most: a save serialises the whole
    /// transcript, and doing that on the UI thread for every one of a hundred long answers is a hitch
    /// the user feels. Phase changes still go through <see cref="SaveStateNow"/>, because those are
    /// what a restart has to have seen exactly, and they are rare.</para>
    /// <para>The timer's write is wrapped: it runs on a thread-pool thread with nobody left to catch
    /// anything, and an unhandled exception there ends the process. The same reasoning, and the same
    /// shape, as <c>SettingsService.DebouncedSave</c>.</para>
    /// </summary>
    private void SaveStateSoon()
    {
        if (_saveRefused) return;

        lock (_debounceLock)
        {
            // Read inside the lock that arms the timer, and set by Dispose inside the lock that clears
            // it. Checked outside, a caller could pass the check, Dispose could run to completion, and
            // the caller could then arm a timer nobody is left to dispose — a write on behalf of a tile
            // that no longer exists.
            if (_disposed) return;

            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => SaveState(), null, AppDefaults.SaveDebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Writes now, and stops the debounce from firing again.
    /// <para>Deliberately not described as cancelling a write already under way: <c>Timer.Dispose()</c>
    /// does not promise that a callback in flight will not run. It does not need to. Every writer
    /// serialises the state as it stands when it runs rather than when it was scheduled, and
    /// <see cref="GoalStatePersistence.Save"/> takes a lock, so a straggler writes content at least as
    /// new as this one's — never staler. Waiting for it instead would mean blocking the UI thread on a
    /// callback that may itself be waiting for the UI thread.</para>
    /// </summary>
    private void SaveStateNow()
    {
        lock (_debounceLock)
        {
            // Refused after Dispose, which has already written the last word. The workflow keeps
            // unwinding afterwards and its phase changes still call through here, so without this a
            // closed tile could write again over the state it had just flushed.
            if (_disposed) return;

            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        SaveState();
    }

    private void SaveState()
    {
        if (_saveRefused) return;

        try
        {
            // The whole snapshot is taken on the UI thread, engine included. Copying only the messages
            // there left ToState enumerating ClarificationHistory on a pool thread while the workflow
            // added to it — "collection was modified", caught below, and a transient race then lit the
            // permanent "this tile could not save its state" for a tile that saves perfectly well.
            var state = Dispatcher.UIThread.CheckAccess()
                ? Snapshot()
                : Dispatcher.UIThread.Invoke(Snapshot);

            _persistence.Save(_filePath, state);
        }
        catch (Exception ex)
        {
            // Said out loud, once. A tile whose reads fail shouts about it, and a tile whose writes
            // fail was doing so only into the log — which is the failure that costs the user the
            // session they are in the middle of, and the one they would want to know about first.
            Trace.TraceWarning($"Failed to save goal state: {ex.Message}");
            if (_saveFailureReported) return;
            _saveFailureReported = true;
            PostFireAndForget(() => Say($"This tile could not save its state ({ex.Message}). " +
                           "The conversation is on screen but will not survive a restart."));
        }

        GoalTileState Snapshot() => _engine.ToState([..Messages], SelectedToolName);
    }

    private void LoadState()
    {
        try
        {
            var state = _persistence.Load(_filePath);
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
                      "no other AI tool was found. Install one and click Resume.");

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
            // Nothing was touched and nothing will be: see _saveRefused.
            Trace.TraceWarning($"Unreadable goal file: {ex.InnerException?.Message}");
            _saveRefused = true;
            Say($"This tile's saved goal could not be opened ({ex.InnerException?.Message}). " +
                "The file has been left alone and this tile will not save over it — close the tile and " +
                "open it again once the file can be read.");
        }
        catch (Exception ex)
        {
            // The same refusal as an unreadable file, and for the same reason: this catch is reached
            // with the tile half-populated, so the transcript is missing and the next save would put
            // that emptiness on top of the session it failed to read. Null strings no longer get here
            // — GoalTileState refuses them in its setters — but whatever does get here is by definition
            // something nobody anticipated, which is the case worth refusing over.
            Trace.TraceWarning($"Failed to load goal state: {ex}");
            _saveRefused = true;
            Say($"This tile's saved goal could not be read ({ex.Message}). The file has been left " +
                "alone and this tile will not save over it.");
        }
    }

    /// <summary>A note from the tile itself, straight into the transcript. Deliberately not
    /// AddMessageAsync: this is used while loading, before the view is attached, and from the save
    /// path, where saving again would be a loop.</summary>
    private void Say(string text) =>
        Messages.Add(new GoalMessage { Role = GoalMessageRole.System, Text = text, Phase = CurrentPhase });

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

        lock (_debounceLock)
            _disposed = true;

        // The token is disposed and cleared by RunAiAsync's own finally, so this usually finds nothing
        // — but "usually" is not a guarantee worth an exception on the way out of a tile.
        // Cancelled but not disposed: the run that owns it is still unwinding and will dispose it in
        // WorkingAsync's own finally. Disposing it here left that run reading Token on a dead object.
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { /* already gone */ }

        // Whatever the debounce was still holding. A tile closed a moment after the tool answered must
        // not lose that answer to a timer that never got to fire. SaveState directly, because
        // SaveStateNow now refuses once _disposed is set — which it is, above.
        //
        // Only if there is something to write, or something already written to keep current. A Goal
        // tile opened and closed without a word used to leave an empty session in the user's
        // repository, and nothing here ever prunes those.
        if (Messages.Count > 0 || File.Exists(_filePath))
            SaveState();

        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }
}
