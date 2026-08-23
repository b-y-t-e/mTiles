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

    /// <summary>Refuses a null, as everything reachable from <see cref="GoalTileState"/> does: this one
    /// is bound straight into the transcript and compared against by <c>SayOnceAsync</c>.</summary>
    public string Text
    {
        get => _text;
        set => _text = value ?? "";
    }
    private string _text = "";
    public GoalPhase Phase { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
