namespace mTiles.Models;

/// <summary>
/// A Goal tile's session as it is written to disk.
/// <para>Every reference property refuses a null in its own setter, the same rule
/// <see cref="AppSettings"/> follows and for the same reason: a property initialiser does not survive
/// deserialisation, so <c>"Messages": null</c> in the file overwrites the fresh list and is not an error
/// the load's own catch would recognise. What followed was a <see cref="NullReferenceException"/> inside
/// <c>GoalWorkflowEngine.LoadFrom</c>, caught as an unknown failure, which stopped the tile saving for
/// the rest of its life — a worse outcome than the damaged JSON beside it, which is at least set aside
/// so the tile can start again. Strings are guarded too: they are compared, split and put into
/// messages, never merely tested for absence.</para>
/// </summary>
public sealed class GoalTileState
{
    public string OriginalGoal
    {
        get => _originalGoal;
        set => _originalGoal = value ?? "";
    }
    private string _originalGoal = "";

    public List<string> ClarificationHistory
    {
        get => _clarificationHistory;
        set => _clarificationHistory = value ?? [];
    }
    private List<string> _clarificationHistory = [];

    public string ApprovedPlan
    {
        get => _approvedPlan;
        set => _approvedPlan = value ?? "";
    }
    private string _approvedPlan = "";

    /// <summary>What the tool last proposed as a plan, before anybody approved it. Nullable on purpose:
    /// null means nothing has been proposed, which is what stops an "ok" approving whatever happens to
    /// be last in the transcript.</summary>
    public string? ProposedPlan { get; set; }

    public GoalPhase CurrentPhase { get; set; }

    public string SelectedToolName
    {
        get => _selectedToolName;
        set => _selectedToolName = value ?? "";
    }
    private string _selectedToolName = "";

    /// <summary>Unused: the tile chooses a tool, not a model. Kept because removing a key is a
    /// migration, and guarded because it is still deserialised into.</summary>
    public string SelectedModel
    {
        get => _selectedModel;
        set => _selectedModel = value ?? "";
    }
    private string _selectedModel = "";

    public int IterationCount { get; set; }
    public bool IsPaused { get; set; }

    /// <summary>Genuinely optional — null means the last review had nothing to say — and read through a
    /// null check rather than taken apart, so this one is left nullable on purpose.</summary>
    public string? LastReviewFeedback { get; set; }

    public List<GoalMessage> Messages
    {
        get => _messages;
        set => _messages = value ?? [];
    }
    private List<GoalMessage> _messages = [];
}
