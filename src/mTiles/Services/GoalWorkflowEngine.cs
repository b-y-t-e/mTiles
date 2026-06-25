using mTiles.Models;

namespace mTiles.Services;

public sealed class GoalWorkflowEngine
{
    private readonly GoalPromptBuilder _promptBuilder = new();
    private const int MaxIterations = 5;

    public string OriginalGoal { get; set; } = "";
    public List<string> ClarificationHistory { get; } = [];
    public string ApprovedPlan { get; set; } = "";
    public string? LastReviewFeedback { get; set; }
    public int IterationCount { get; set; }
    public GoalPhase CurrentPhase { get; set; } = GoalPhase.Goal;
    public bool IsPaused { get; set; }

    public bool CanIterate => IterationCount < MaxIterations;
    public int MaxIter => MaxIterations;

    public string BuildClarifyPrompt() =>
        _promptBuilder.BuildClarify(OriginalGoal, ClarificationHistory);

    public string BuildPlanPrompt() =>
        _promptBuilder.BuildPlan(OriginalGoal, ClarificationHistory);

    public string BuildImplementPrompt(string? gitDiff) =>
        _promptBuilder.BuildImplement(OriginalGoal, ApprovedPlan, LastReviewFeedback, gitDiff);

    public string BuildReviewPrompt(string? gitDiff) =>
        _promptBuilder.BuildReview(OriginalGoal, gitDiff);

    public void StartNewGoal(string goal)
    {
        OriginalGoal = goal;
        ClarificationHistory.Clear();
        ApprovedPlan = "";
        LastReviewFeedback = null;
        IterationCount = 0;
        IsPaused = false;
        CurrentPhase = GoalPhase.Goal;
    }

    public void RecordClarification(string text) =>
        ClarificationHistory.Add(text);

    public void ApprovePlan(string planText)
    {
        ApprovedPlan = planText;
        LastReviewFeedback = null;
        IterationCount = 0;
    }

    public void RecordReviewFeedback(string feedback) =>
        LastReviewFeedback = feedback;

    public void ClearReviewFeedback() =>
        LastReviewFeedback = null;

    public void IncrementIteration() =>
        IterationCount++;

    public static bool IsVerdictPass(string reviewResponse) =>
        reviewResponse.Contains("VERDICT: PASS", StringComparison.OrdinalIgnoreCase);

    public static bool IsApproval(string text)
    {
        var normalized = text.Trim().TrimEnd('.', '!').ToLowerInvariant();
        return normalized is "ok" or "okay" or "yes" or "tak" or "go" or "approve"
            or "approved" or "start" or "do it" or "lgtm" or "ship it" or "proceed";
    }

    public string GetPhaseLabel() => IsPaused
        ? "Paused. Click Resume to continue."
        : CurrentPhase switch
        {
            GoalPhase.Goal => "Waiting for goal...",
            GoalPhase.Clarify => "Answer the questions above, then press Send.",
            GoalPhase.Plan => "Type 'ok' to approve, or describe what to change.",
            GoalPhase.Summary => "Done. Type a new goal or click Reset.",
            _ => $"Resumed at {CurrentPhase} phase."
        };

    public GoalTileState ToState(List<GoalMessage> messages, string toolName, string model) => new()
    {
        OriginalGoal = OriginalGoal,
        ClarificationHistory = [..ClarificationHistory],
        ApprovedPlan = ApprovedPlan,
        CurrentPhase = CurrentPhase,
        SelectedToolName = toolName,
        SelectedModel = model,
        IterationCount = IterationCount,
        IsPaused = IsPaused,
        LastReviewFeedback = LastReviewFeedback,
        Messages = messages
    };

    public void LoadFrom(GoalTileState state)
    {
        OriginalGoal = state.OriginalGoal;
        ClarificationHistory.Clear();
        ClarificationHistory.AddRange(state.ClarificationHistory);
        ApprovedPlan = state.ApprovedPlan;
        CurrentPhase = state.CurrentPhase;
        IterationCount = state.IterationCount;
        IsPaused = state.IsPaused;
        LastReviewFeedback = state.LastReviewFeedback;
    }
}
