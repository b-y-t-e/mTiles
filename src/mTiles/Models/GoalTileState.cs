using System.Text.Json.Serialization;
using mTiles.Services;

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
/// <summary>Dropping the nulls a file can put inside a list, in one place so every collection here is
/// guarded the same way.</summary>
internal static class Without
{
    public static List<T> Nulls<T>(List<T>? items) =>
        items is null ? [] : [..items.Where(x => x is not null)];
}

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

    /// <summary>What the verify command printed the last time it failed. Null once it passes.</summary>
    public string? LastVerifyOutput { get; set; }

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
