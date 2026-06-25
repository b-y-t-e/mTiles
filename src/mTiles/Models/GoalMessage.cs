namespace mTiles.Models;

public enum GoalMessageRole
{
    User,
    Assistant,
    System
}

public sealed class GoalMessage
{
    public GoalMessageRole Role { get; set; }
    public string Text { get; set; } = "";
    public GoalPhase Phase { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
