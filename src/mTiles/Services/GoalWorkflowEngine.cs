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
        ProposedPlan = null;
        LastReviewFeedback = null;
        IterationCount = 0;
        IsPaused = false;
        CurrentPhase = GoalPhase.Goal;
    }

    public void RecordClarification(string text) =>
        ClarificationHistory.Add(text);

    /// <summary>The plan as the tool last proposed it, waiting to be approved or argued with.</summary>
    public string? ProposedPlan { get; set; }

    /// <summary>
    /// Remembers what the tool has just proposed, or forgets the last one when a new planning run
    /// begins.
    /// <para>Forgetting is the half that matters. Rejecting a plan sends the tile back to Clarify and
    /// then forward to Plan again; without clearing, a second planning run that produced nothing left
    /// the <em>rejected</em> plan standing, and "ok" approved the plan the user had just turned down.
    /// </para>
    /// </summary>
    public void RecordProposedPlan(string? planText) => ProposedPlan = planText;

    /// <summary>
    /// Adopts the plan the tool proposed, or answers false when there is not one.
    /// <para>It used to be dug out of the transcript — the last assistant message, whatever that was.
    /// Once an empty or failed run could leave the Plan phase paused with no answer in it, typing "ok"
    /// approved the <em>clarifying questions</em> as the plan, or approved an empty string and started
    /// implementing with no plan at all, in silence.</para>
    /// </summary>
    public bool ApprovePlan()
    {
        if (ProposedPlan is not { Length: > 0 }) return false;

        ApprovedPlan = ProposedPlan;
        LastReviewFeedback = null;
        IterationCount = 0;
        return true;
    }

    public void RecordReviewFeedback(string feedback) =>
        LastReviewFeedback = feedback;

    public void ClearReviewFeedback() =>
        LastReviewFeedback = null;

    public static bool IsVerdictPass(string reviewResponse) =>
        reviewResponse.Contains("VERDICT: PASS", StringComparison.OrdinalIgnoreCase);

    public static bool IsApproval(string text)
    {
        var normalized = text.Trim().TrimEnd('.', '!').ToLowerInvariant();
        return normalized is "ok" or "okay" or "yes" or "tak" or "go" or "approve"
            or "approved" or "start" or "do it" or "lgtm" or "ship it" or "proceed";
    }

    /// <summary>
    /// The phases in which the tool is doing the work rather than waiting for the user — the ones a
    /// tile can be interrupted in the middle of, and the ones Resume knows how to pick up.
    /// </summary>
    public static bool IsMidRun(GoalPhase phase) => phase is GoalPhase.Implement or GoalPhase.Review;

    /// <summary>
    /// Whether a saved state was written while the tool was working, and so came back to a tile with
    /// nothing running in it.
    /// <para>The phase alone cannot answer this for Clarify and Plan, because one value covers both
    /// <em>asking</em> the tool and <em>waiting</em> for the user's answer: adding them to
    /// <see cref="IsMidRun"/> would have every tile left waiting for an answer come back claiming to be
    /// interrupted, and Resume would ask the same questions again. The transcript is what tells them
    /// apart — an answer that arrived is the last thing in it, so a run that was cut off leaves the
    /// user's own message last.</para>
    /// </summary>
    public static bool WasInterrupted(GoalTileState state) =>
        IsMidRun(state.CurrentPhase) ||
        (state.CurrentPhase is GoalPhase.Clarify or GoalPhase.Plan &&
         state.Messages.LastOrDefault()?.Role == GoalMessageRole.User);

    public string GetPhaseLabel() => IsPaused
        // True of a pause the user asked for and of a run the application was closed in the middle
        // of — deliberately, because telling them apart would cost a flag in the saved state to say
        // something the user can already see: the work stopped, and Resume starts it again.
        ? IsMidRun(CurrentPhase)
            ? $"Stopped during {CurrentPhase.ToString().ToLowerInvariant()}. Click Resume to continue."
            : "Paused. Click Resume to continue."
        : CurrentPhase switch
        {
            GoalPhase.Goal => "Waiting for goal...",
            GoalPhase.Clarify => "Answer the questions above, then press Send.",
            GoalPhase.Plan => "Type 'ok' to approve, or describe what to change.",
            GoalPhase.Summary => "Done. Type a new goal, or start a fresh one with +.",
            _ => $"Resumed at {CurrentPhase} phase."
        };

    public GoalTileState ToState(List<GoalMessage> messages, string toolName) => new()
    {
        OriginalGoal = OriginalGoal,
        ClarificationHistory = [..ClarificationHistory],
        ApprovedPlan = ApprovedPlan,
        ProposedPlan = ProposedPlan,
        CurrentPhase = CurrentPhase,
        SelectedToolName = toolName,
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
        ProposedPlan = state.ProposedPlan;
        CurrentPhase = state.CurrentPhase;
        IterationCount = state.IterationCount;
        LastReviewFeedback = state.LastReviewFeedback;

        // A run that was interrupted is a pause nobody asked for. The rule lives here rather than in
        // the view model because it is a fact about a loaded state, not about a tile: whoever loads one
        // gets it, and there is no second caller left to forget it.
        IsPaused = state.IsPaused || WasInterrupted(state);
    }
}
