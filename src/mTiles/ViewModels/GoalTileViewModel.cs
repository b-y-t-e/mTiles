using System.Collections.ObjectModel;
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
    private List<AiToolInfo>? _cachedTools;
    private bool _isLoading;

    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private GoalPhase _currentPhase = GoalPhase.Goal;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _selectedToolName = "Claude Code";
    [ObservableProperty] private string _selectedModel = "claude-opus-4-6";
    [ObservableProperty] private string _phaseLabel = "Waiting for goal...";
    [ObservableProperty] private bool _isPaused;

    public ObservableCollection<GoalMessage> Messages { get; } = [];
    public ObservableCollection<string> AvailableTools { get; } = [];
    public Action? ScrollToEnd { get; set; }
    public Action? TileSettingsChanged { get; set; }
    public Func<string, Task<bool>>? ConfirmAction { get; set; }

    public List<string> AvailableModels { get; } =
    [
        "claude-opus-4-6",
        "claude-sonnet-4-6",
        "claude-haiku-4-5-20251001",
        "claude-fable-5"
    ];

    private AiToolInfo? _resolvedTool;

    public string FilePath => _filePath;

    public GoalTileViewModel(string workingDirectory, SettingsService settingsService)
    {
        _workingDirectory = workingDirectory;
        _settingsService = settingsService;

        var goalsDir = Path.Combine(workingDirectory, ".mterminal", "goals");
        _filePath = Path.Combine(goalsDir, $"{Guid.NewGuid():N}.json");

        DetectTools();
        LoadDefaultModel();
    }

    public GoalTileViewModel(string filePath, string workingDirectory, SettingsService settingsService)
    {
        _workingDirectory = workingDirectory;
        _settingsService = settingsService;
        _filePath = filePath;

        DetectTools();
        LoadDefaultModel();
        _isLoading = true;
        LoadState();
        _isLoading = false;
    }

    // ── Tool detection ──────────────────────────────────

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
            AvailableTools.Add("(no AI tools detected)");

        _resolvedTool = tools.FirstOrDefault(t => t.Name == SelectedToolName && t.IsInstalled)
                        ?? tools.FirstOrDefault(t => t.IsInstalled);

        if (_resolvedTool != null)
            SelectedToolName = _resolvedTool.Name;
    }

    private void LoadDefaultModel()
    {
        var defaults = _settingsService.Settings.GoalDefaultModels;
        var toolBinary = _resolvedTool?.BinaryName;
        if (toolBinary != null && defaults.TryGetValue(toolBinary, out var model))
            SelectedModel = model;
    }

    partial void OnSelectedToolNameChanged(string value)
    {
        var tools = GetCachedTools();
        _resolvedTool = tools.FirstOrDefault(t => t.Name == value && t.IsInstalled);
        LoadDefaultModel();
    }

    partial void OnSelectedModelChanged(string value)
    {
        if (_isLoading) return;
        var toolBinary = _resolvedTool?.BinaryName;
        if (toolBinary != null)
        {
            _settingsService.Settings.GoalDefaultModels[toolBinary] = value;
            _settingsService.Save();
        }
    }

    // ── Phase dispatch ──────────────────────────────────

    [RelayCommand]
    private async Task Submit()
    {
        var text = InputText.Trim();
        if (string.IsNullOrEmpty(text) || IsRunning) return;
        InputText = "";

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
                    await RunClarifyAsync();
                    break;

                case GoalPhase.Clarify:
                    _engine.RecordClarification(text);
                    await AddMessageAsync(GoalMessageRole.User, text, GoalPhase.Clarify);
                    await RunPlanAsync();
                    break;

                case GoalPhase.Plan:
                    await AddMessageAsync(GoalMessageRole.User, text, GoalPhase.Plan);
                    if (GoalWorkflowEngine.IsApproval(text))
                    {
                        var lastAssistant = Messages.LastOrDefault(m => m.Role == GoalMessageRole.Assistant)?.Text ?? "";
                        _engine.ApprovePlan(lastAssistant);
                        await RunImplementReviewLoopAsync();
                    }
                    else
                    {
                        _engine.RecordClarification(text);
                        await RunClarifyAsync();
                    }
                    break;

                case GoalPhase.Implement:
                case GoalPhase.Review:
                    await AddMessageAsync(GoalMessageRole.System, "AI is working. Please wait for the current phase to complete.", CurrentPhase);
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Goal workflow error: {ex.Message}");
            await AddMessageAsync(GoalMessageRole.System, $"Unexpected error: {ex.Message}", CurrentPhase);
        }
    }

    // ── Workflow phases ─────────────────────────────────

    private async Task RunClarifyAsync() =>
        await RunPhaseAsync(
            GoalPhase.Clarify,
            "AI is asking clarifying questions...",
            _engine.BuildClarifyPrompt(),
            "Answer the questions above, then press Send.",
            GoalPhase.Goal, "Waiting for goal...");

    private async Task RunPlanAsync() =>
        await RunPhaseAsync(
            GoalPhase.Plan,
            "AI is creating a plan...",
            _engine.BuildPlanPrompt(),
            "Type 'ok' to approve, or describe what to change.",
            GoalPhase.Clarify, "Answer the questions above, then press Send.");

    private async Task RunPhaseAsync(GoalPhase phase, string runningLabel, string prompt,
        string successLabel, GoalPhase fallbackPhase, string fallbackLabel)
    {
        _engine.CurrentPhase = phase;
        SyncFromEngine();
        PhaseLabel = runningLabel;

        var response = await RunAiAsync(prompt);
        if (response != null)
        {
            await AddMessageAsync(GoalMessageRole.Assistant, response, phase);
            PhaseLabel = successLabel;
        }
        else
        {
            await AddMessageAsync(GoalMessageRole.System, "AI returned an empty response. Try again.", phase);
            _engine.CurrentPhase = fallbackPhase;
            SyncFromEngine();
            PhaseLabel = fallbackLabel;
        }
        SaveState();
    }

    private async Task RunImplementReviewLoopAsync()
    {
        try
        {
            while (_engine.CanIterate)
            {
                _engine.IncrementIteration();

                _engine.CurrentPhase = GoalPhase.Implement;
                SyncFromEngine();
                PhaseLabel = $"AI is implementing (iteration {_engine.IterationCount}/{_engine.MaxIter})...";

                var diff = _engine.LastReviewFeedback != null ? await GetGitDiffAsync() : null;
                var implPrompt = _engine.BuildImplementPrompt(diff);
                var implResult = await RunAiAsync(implPrompt);
                if (implResult != null)
                    await AddMessageAsync(GoalMessageRole.Assistant, implResult, GoalPhase.Implement);
                if (implResult == null) { await ShowSummaryAsync("Stopped during implementation."); return; }

                _engine.CurrentPhase = GoalPhase.Review;
                SyncFromEngine();
                PhaseLabel = "AI is reviewing changes...";

                var reviewDiff = await GetGitDiffAsync();
                var reviewPrompt = _engine.BuildReviewPrompt(reviewDiff);
                var reviewResult = await RunAiAsync(reviewPrompt);
                if (reviewResult != null)
                    await AddMessageAsync(GoalMessageRole.Assistant, reviewResult, GoalPhase.Review);
                if (reviewResult == null) { await ShowSummaryAsync("Stopped during review."); return; }

                if (GoalWorkflowEngine.IsVerdictPass(reviewResult))
                {
                    _engine.ClearReviewFeedback();
                    break;
                }

                _engine.RecordReviewFeedback(reviewResult);
                if (_engine.CanIterate)
                    await AddMessageAsync(GoalMessageRole.System, $"Review found issues. Re-implementing (attempt {_engine.IterationCount + 1})...", GoalPhase.Review);
            }

            await ShowSummaryAsync();
        }
        finally
        {
            SaveState();
        }
    }

    private async Task ShowSummaryAsync(string? reason = null)
    {
        _engine.CurrentPhase = GoalPhase.Summary;
        SyncFromEngine();
        PhaseLabel = "Done. Type a new goal or click Reset.";

        var summary = reason != null
            ? $"{reason} Completed {_engine.IterationCount} iteration(s).\nType a new goal or click Reset."
            : $"Goal completed after {_engine.IterationCount} iteration(s).\nType a new goal or click Reset.";

        await AddMessageAsync(GoalMessageRole.System, summary, GoalPhase.Summary);
    }

    // ── AI process execution ────────────────────────────

    private async Task<string?> RunAiAsync(string prompt)
    {
        if (_resolvedTool?.ExecutablePath == null)
        {
            await AddMessageAsync(GoalMessageRole.System, "No AI tool available. Install Claude Code or another supported tool.", GoalPhase.Goal);
            return null;
        }

        IsRunning = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var runner = AiProcessRunner.GetRunner(_resolvedTool.BinaryName);
            var result = await AiProcessRunner.RunPlainAsync(
                _resolvedTool.ExecutablePath,
                prompt,
                _workingDirectory,
                SelectedModel,
                runner,
                ct: _cts.Token);

            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (OperationCanceledException)
        {
            if (IsPaused)
                await AddMessageAsync(GoalMessageRole.System, "Paused. Click Resume to continue.", CurrentPhase);
            else
                await AddMessageAsync(GoalMessageRole.System, "Operation cancelled.", CurrentPhase);
            return null;
        }
        catch (Exception ex)
        {
            await AddMessageAsync(GoalMessageRole.System, $"Error: {ex.Message}", CurrentPhase);
            return null;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task<string?> GetGitDiffAsync()
    {
        try
        {
            var git = new GitCommandRunner(_workingDirectory, _settingsService.Settings.GitPath is { Length: > 0 } p ? p : "git");
            var output = await git.RunAsync("diff", throwOnError: false);
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Git diff failed: {ex.Message}");
            return null;
        }
    }

    // ── Commands ────────────────────────────────────────

    [RelayCommand]
    private void Pause()
    {
        _cts?.Cancel();
        _engine.IsPaused = true;
        IsPaused = true;
        SaveState();
    }

    [RelayCommand]
    private async Task ResumeAsync()
    {
        if (!IsPaused) return;
        _engine.IsPaused = false;
        IsPaused = false;

        try
        {
            switch (CurrentPhase)
            {
                case GoalPhase.Implement:
                case GoalPhase.Review:
                    await AddMessageAsync(GoalMessageRole.System, "Resuming implementation...", CurrentPhase);
                    await RunImplementReviewLoopAsync();
                    break;
                case GoalPhase.Clarify:
                    await RunClarifyAsync();
                    break;
                case GoalPhase.Plan:
                    await RunPlanAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Goal resume error: {ex.Message}");
            await AddMessageAsync(GoalMessageRole.System, $"Unexpected error: {ex.Message}", CurrentPhase);
        }
    }

    [RelayCommand]
    private async Task NewGoalAsync()
    {
        if (IsRunning) return;

        var hasProgress = Messages.Count > 0 && CurrentPhase != GoalPhase.Goal;
        if (hasProgress && ConfirmAction != null)
        {
            var confirmed = await ConfirmAction("Discard current goal and start fresh?");
            if (!confirmed) return;
        }

        Messages.Clear();
        _engine.StartNewGoal("");
        SyncFromEngine();
        PhaseLabel = "Waiting for goal...";
        SaveState();
    }

    // ── Engine ↔ ViewModel sync ────────────────────────

    private void SyncFromEngine()
    {
        CurrentPhase = _engine.CurrentPhase;
        IsPaused = _engine.IsPaused;
    }

    // ── UI helpers ──────────────────────────────────────

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
    }

    // ── Persistence ─────────────────────────────────────

    private void SaveState()
    {
        try
        {
            List<GoalMessage> messagesCopy;
            if (Dispatcher.UIThread.CheckAccess())
                messagesCopy = [..Messages];
            else
                messagesCopy = Dispatcher.UIThread.Invoke(() => Messages.ToList());

            var state = _engine.ToState(messagesCopy, SelectedToolName, SelectedModel);
            _persistence.Save(_filePath, state);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Failed to save goal state: {ex.Message}");
        }
    }

    private void LoadState()
    {
        try
        {
            var state = _persistence.Load(_filePath);
            if (state == null) return;

            _engine.LoadFrom(state);
            CurrentPhase = state.CurrentPhase;
            SelectedToolName = state.SelectedToolName;
            SelectedModel = state.SelectedModel;
            IsPaused = state.IsPaused;

            foreach (var m in state.Messages)
                Messages.Add(m);

            PhaseLabel = _engine.GetPhaseLabel();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Failed to load goal state: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
