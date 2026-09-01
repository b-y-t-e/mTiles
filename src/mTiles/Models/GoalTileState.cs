using System.Text.Json.Serialization;
using mTiles.Services;

namespace mTiles.Models;

/// <summary>Dropping the nulls a file can put inside a list, in one place so every collection here is
/// guarded the same way.</summary>
internal static class Without
{
    public static List<T> Nulls<T>(List<T>? items) =>
        items is null ? [] : [..items.Where(x => x is not null)];
}

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

    /// <summary>
    /// Guarded twice: against a null list, and against a null <em>in</em> the list.
    /// </summary>
    /// <remarks>
    /// The second is the one that was missing, and it is the same lesson the settings file already
    /// taught: a guard only ever covers the level somebody remembered. <c>"ClarificationHistory": null</c>
    /// was handled; <c>["a", null]</c> reached <c>GoalWorkflowEngine.LoadFrom</c>, where every turn is
    /// labelled with <c>StartsWith</c>, and threw where nothing expects it — inside the view model's
    /// catch of last resort, which stops the tile saving for the rest of its life. A file with one null
    /// in it was punished more harshly than a file of corrupt bytes, which is at least set aside so the
    /// tile can start again.
    /// </remarks>
    public List<string> ClarificationHistory
    {
        get => _clarificationHistory;
        set => _clarificationHistory = Without.Nulls(value);
    }
    private List<string> _clarificationHistory = [];

    /// <summary>
    /// The images pasted into this goal, so a resumed run can still say what its markers stand for.
    /// </summary>
    /// <remarks>
    /// Guarded against a null list and a null <em>in</em> the list, like every collection here: each
    /// entry's path is read straight into a prompt block, so one null would throw inside the build of
    /// the very prompt the resume exists to send.
    /// </remarks>
    public List<GoalImageAttachment> AttachedImages
    {
        get => _attachedImages;
        set => _attachedImages = Without.Nulls(value);
    }
    private List<GoalImageAttachment> _attachedImages = [];

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

    /// <summary>Tolerantly read, as <see cref="LastStopReason"/> is and for the same reason: a phase a
    /// newer build wrote is not worth a deleted session. Unknown reads as <c>Goal</c>, so the tile comes
    /// back with its transcript and waiting for a goal rather than not coming back.</summary>
    [JsonConverter(typeof(TolerantGoalPhaseConverter))]
    public GoalPhase CurrentPhase { get; set; }

    /// <summary>
    /// What this goal was being run by before agents had instances.
    /// </summary>
    /// <remarks><b>Read tolerantly for ever, not migrated once.</b> A goal file lives in the workspace
    /// and travels with a branch, so one written on another machine or on an older branch will still
    /// carry a tool name years from now. <c>GoalAgents.MatchingToolName</c> is what turns it back into an
    /// agent; nothing writes it any more.</remarks>
    public string SelectedToolName
    {
        get => _selectedToolName;
        set => _selectedToolName = value ?? "";
    }
    private string _selectedToolName = "";

    /// <summary>Which configured agent carries the goal out — an <c>AiAgentInstance.Id</c>.</summary>
    /// <remarks>Empty means nothing has been chosen yet, which the tile answers with the first agent
    /// this machine can run. An id naming an instance that has since been deleted is <em>not</em>
    /// substituted: the tile says the agent is gone rather than quietly running the goal on another
    /// model than the one it was planned with.</remarks>
    public string ExecutionAgentInstanceId
    {
        get => _executionAgentInstanceId;
        set => _executionAgentInstanceId = value ?? "";
    }
    private string _executionAgentInstanceId = "";

    /// <summary>Which configured agent reviews the work, or empty for the one that did it.</summary>
    /// <remarks>Empty is a real answer and the default one — "the same agent" — rather than an absent
    /// setting, which is why the strip spells it out. A second agent is what makes a review something
    /// other than the author marking their own work, and it is also why the review phase's permission
    /// comes from the agent by phase: two agents writing into one worktree is one more than
    /// <c>GoalBaseline</c> photographed.</remarks>
    public string ReviewAgentInstanceId
    {
        get => _reviewAgentInstanceId;
        set => _reviewAgentInstanceId = value ?? "";
    }
    private string _reviewAgentInstanceId = "";

    /// <summary>Unused: the tile chooses a tool, not a model. Kept because removing a key is a
    /// migration, and guarded because it is still deserialised into.</summary>
    public string SelectedModel
    {
        get => _selectedModel;
        set => _selectedModel = value ?? "";
    }
    private string _selectedModel = "";

    public int IterationCount { get; set; }

    /// <summary>
    /// Why the last run of the loop stopped. Null means none has.
    /// </summary>
    /// <remarks>
    /// Read through <see cref="TolerantEnumConverter{T}"/>, which is not decoration. Enums here are
    /// written as names, and an unknown name is a <see cref="System.Text.Json.JsonException"/> — which
    /// the persistence layer rightly reads as a damaged file, sets aside as <c>.bad-…</c>, and starts
    /// over from empty. The ordinary way an unknown name gets into a goal file is a **downgrade**: a
    /// newer build wrote a stop reason this one has never heard of, and deleting somebody's session over
    /// a field whose only job is to decide whether one button appears is wildly out of proportion.
    /// Unknown reads as null, which is the file's own way of saying nothing has finished here.
    /// </remarks>
    [JsonConverter(typeof(TolerantEnumConverter<GoalStopReason>))]
    public GoalStopReason? LastStopReason { get; set; }

    /// <summary>
    /// The git ref holding the working tree as it was when this goal started, or null when no snapshot
    /// was taken — see <see cref="mTiles.Services.GoalBaseline"/>.
    /// </summary>
    /// <remarks>
    /// Nullable on purpose, and left nullable rather than guarded into an empty string: null is the
    /// answer for every workspace this could not snapshot, and both readers ask whether there is a ref
    /// at all before doing anything. Every check is a pattern match on a non-empty string, so a
    /// converter that turns a null into an empty one costs nothing here.
    /// </remarks>
    public string? BaselineRef { get; set; }

    /// <summary>
    /// The paths the user named with <c>@</c> when this goal was typed or detected, or an empty list
    /// when they named none.
    /// </summary>
    /// <remarks>
    /// The hard half of a narrowed task: every working-tree read of this goal is filtered to these
    /// paths, and a tile reopened after a restart must read the same narrowed tree the run was
    /// started on — otherwise a Resume implements past the narrowing its own goal was written under.
    /// Absent in a file written before this existed, which is the state of every goal that had no
    /// scope.
    /// </remarks>
    public List<string> ScopePaths
    {
        get => _scopePaths;
        set => _scopePaths = Without.Nulls(value);
    }
    private List<string> _scopePaths = [];

    /// <summary>
    /// The ref holding the working tree as this run <em>finished</em>, or null when none was taken.
    /// </summary>
    /// <remarks>
    /// The other end of <see cref="BaselineRef"/>, and persisted for the same reason: what a commit is
    /// allowed to claim is decided by the pair, and a tile reopened tomorrow has nothing else to read
    /// the boundary out of. Null in a file written before this was recorded, which the committer treats
    /// as an unknown upper end rather than as an empty one.
    /// </remarks>
    public string? EndRef { get; set; }

    /// <summary>
    /// True when the work under review is what the user already had, rather than what the tool wrote.
    /// </summary>
    /// <remarks>
    /// Set by <em>Detect &amp; run</em>, and saved because a Resume has to go on judging the same
    /// thing. Without it a tile reopened mid-run reverted to reading the tree against its own baseline
    /// — which on that path is a photograph of the very changes being judged, so the review was handed
    /// an empty diff.
    /// </remarks>
    public bool ReviewsExistingWork { get; set; }

    /// <summary>What the attempts field said before Continue raised it. Null until it does. Saved,
    /// because a tile reopened halfway through a continued run has nothing else to restore the user's
    /// own number from.</summary>
    public int? AttemptsBeforeExtension { get; set; }

    /// <summary>Clarification rounds already spent, so a restart does not renew the budget and let a
    /// tool that keeps finding one more question ask for ever.</summary>
    public int ClarifyRounds { get; set; }

    public bool IsPaused { get; set; }

    /// <summary>How this tile decides a goal is done. Guarded like every other reference here: the
    /// engine reads MaxIterations out of it on every lap, so <c>"Criteria": null</c> in the file would
    /// be a NullReferenceException inside the loop rather than an error the load could catch.</summary>
    public GoalCompletionCriteria Criteria
    {
        get => _criteria;
        set => _criteria = value ?? new GoalCompletionCriteria();
    }
    private GoalCompletionCriteria _criteria = new();

    /// <summary>The previous review reduced to something comparable, so the no-progress stop survives a
    /// restart. Null before the first review, and read through a null check.</summary>
    public string? LastReviewFingerprint { get; set; }

    /// <summary>
    /// What the last review counted, so the badges in the status strip come back with the tile.
    /// <para>Four small integers rather than the findings themselves. A tile paused mid-run comes back
    /// with the review that Resume is about to act on still standing in the transcript, and the strip
    /// summarising it went blank across a restart — the one moment the summary is most wanted.</para>
    /// <para><b>Indexed by position in <c>Enum.GetValues&lt;GoalSeverity&gt;()</c>, so the order of that
    /// enum is part of this file format.</b> Adding a level at the end is safe and <c>ShowBadges</c>
    /// tolerates the length changing; <em>reordering</em> it is not, and nothing at runtime could tell —
    /// every count in every goal file already written would silently change meaning, and yesterday's
    /// blockers would come back as errors. The order is pinned by
    /// <c>The_severities_keep_the_order_the_saved_counts_are_stored_in</c>, which is the only thing that
    /// fails if somebody moves a member.</para>
    /// </summary>
    public int[] LastReviewCounts
    {
        get => _lastReviewCounts;
        set => _lastReviewCounts = value ?? [];
    }
    private int[] _lastReviewCounts = [];

    /// <summary>Genuinely optional — null means the last review had nothing to say — and read through a
    /// null check rather than taken apart, so this one is left nullable on purpose.</summary>
    public string? LastReviewFeedback { get; set; }

    /// <summary>
    /// Questions the tool has asked and the user has not answered yet.
    /// </summary>
    /// <remarks>
    /// Persisted, and that is what lets them be asked in controls rather than printed into the
    /// transcript. A block of text in the transcript survives a restart by being part of the
    /// transcript; a panel built from a parsed answer would not, and closing the tile mid-question
    /// would leave a goal waiting for an answer to questions nobody could see any more.
    /// </remarks>
    public List<GoalQuestion> PendingQuestions
    {
        get => _pendingQuestions;
        set => _pendingQuestions = Without.Nulls(value);
    }
    private List<GoalQuestion> _pendingQuestions = [];

    /// <summary>One line per earlier attempt — what it changed and what it decided against — so a
    /// restart does not send the next attempt back down a dead end an earlier one already backed out
    /// of.</summary>
    public List<string> AttemptLog
    {
        get => _attemptLog;
        set => _attemptLog = Without.Nulls(value);
    }
    private List<string> _attemptLog = [];

    /// <summary>Guarded against a null in the list as well as a null list — see
    /// <see cref="ClarificationHistory"/>. A null message reaches the transcript, the snapshot filter
    /// and <c>GoalTilePolicy.WorthConfirming</c>, all of which read <c>Role</c>.</summary>
    public List<GoalMessage> Messages
    {
        get => _messages;
        set => _messages = Without.Nulls(value);
    }
    private List<GoalMessage> _messages = [];
}
