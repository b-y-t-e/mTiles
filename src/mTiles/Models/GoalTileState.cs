namespace mTiles.Models;

public sealed class GoalTileState
{
    public string OriginalGoal { get; set; } = "";
    public List<string> ClarificationHistory { get; set; } = [];
    public string ApprovedPlan { get; set; } = "";
    public GoalPhase CurrentPhase { get; set; }
    public string SelectedToolName { get; set; } = "";
    public string SelectedModel { get; set; } = "";
    public int IterationCount { get; set; }
    public bool IsPaused { get; set; }
    public string? LastReviewFeedback { get; set; }
    public List<GoalMessage> Messages { get; set; } = [];
}
