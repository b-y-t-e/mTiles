using System.Text.Json.Serialization;
using mTiles.Services;

namespace mTiles.Models;

public enum GoalMessageRole
{
    User,
    Assistant,
    System
}

public sealed class GoalMessage
{
    /// <summary>Tolerantly read: one unrecognised role in one message must not cost the whole
    /// transcript. Unknown reads as <c>System</c> — a line attributed to the tile, which is never fed
    /// back to a tool as the user's words or as its own.</summary>
    [JsonConverter(typeof(TolerantGoalMessageRoleConverter))]
    public GoalMessageRole Role { get; set; }

    /// <summary>Refuses a null, as everything reachable from <see cref="GoalTileState"/> does: this one
    /// is bound straight into the transcript and compared against by <c>SayOnceAsync</c>.</summary>
    public string Text
    {
        get => _text;
        set => _text = value ?? "";
    }
    private string _text = "";
    /// <summary>Tolerantly read, exactly as <c>GoalTileState.CurrentPhase</c> is: a phase written by a
    /// newer build must not cost the session. This one was missed when the other two were covered —
    /// and it is the more likely of the two to carry a value from the future, because there is one per
    /// message.</summary>
    [JsonConverter(typeof(TolerantGoalPhaseConverter))]
    public GoalPhase Phase { get; set; }

    /// <summary>
    /// Text the tool wrote, in its own words, to be rendered as markdown.
    /// </summary>
    /// <remarks>
    /// <para>The distinction is provenance, not role. <see cref="GoalMessageRole.Assistant"/> covers two
    /// different things: the tool's own prose — a plan, an implementation note — which is markdown
    /// and reads far better rendered, and this application's own tables, which are not. A review is
    /// composed here into columns: a severity, a file and a line on one row, the title and detail
    /// indented under it. That layout is made of spaces, and markdown eats spaces — runs collapse,
    /// two-space indents become paragraph continuations, and the reviewer's own <c>*</c> or <c>_</c>
    /// inside a finding turns into emphasis. The one part of the transcript arranged to be read in
    /// columns was the part being re-flowed.</para>
    /// <para><b>Which way round this flag points is a migration decision.</b> It began as
    /// <c>Preformatted</c>, defaulting to false, and that was wrong for every goal file written before
    /// it existed: <c>System.Text.Json</c> reads a missing field as the default, so every review already
    /// on disk came back claiming to be prose and was re-flowed on the first restart. Turned round, an
    /// unrecognised field means <em>no</em> markdown — which is exactly the behaviour those files were
    /// written under. It fails safe in the new direction too: a prose message that forgets to set this
    /// is shown as written, which is plain rather than wrong.</para>
    /// </remarks>
    public bool Markdown { get; set; }
    /// <summary>Whether this message is rendered as markdown. Only the tool's own words are — not what
    /// this application composed, and not what the user typed.</summary>
    [JsonIgnore]
    public bool IsMarkdown => Markdown && Role == GoalMessageRole.Assistant;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// A note about <em>this</em> opening of the tile rather than part of the conversation, and so never
    /// written to the file.
    /// <para>One exists today — the tool this goal was using is not installed — and it is said while
    /// loading. Saved with everything else, it came back from the file on the next load and had a fresh
    /// copy appended beside it: open such a goal ten times and the transcript carries ten identical
    /// warnings, each one having been true once. Filtered in <c>GoalWorkflowEngine.ToState</c> rather
    /// than at the call site, so a second note of this kind cannot reintroduce the bug.</para>
    /// <para>The damaged-file note is deliberately <em>not</em> one of them, though it is also said
    /// while loading. It records something that happened once and cannot happen again for this file:
    /// the session was set aside as <c>.bad-…</c> and this transcript started empty over it. That is a
    /// fact about the goal rather than about the opening, and it is the only trace the user has that
    /// anything was ever there — so it is written down, and it does not accumulate, because the file
    /// it complains about is no longer the one being read.</para>
    /// </summary>
    [JsonIgnore]
    public bool AboutThisSession { get; set; }

    /// <summary>
    /// What a review found, kept as findings rather than only as the sentence they were flattened into.
    /// </summary>
    /// <remarks>
    /// <para>The transcript is a terminal and a review was a column of monospace: a padded severity, a
    /// file and a category on one line, the title and detail indented under it. It scans, up to a
    /// point, and the point is colour — a warning and a blocker were the same grey text, so the one
    /// thing worth seeing first was the thing the eye had to search for.</para>
    /// <para>Kept <em>beside</em> <see cref="Text"/>, not instead of it. Text still holds the head of
    /// the review — the verdict, the counts, the tool's own reason where it gave one — so a message
    /// written by an older build, which has no findings here, still renders exactly as it always did.
    /// That is also what the copy button assembles from, so what lands on the clipboard is the whole
    /// review rather than the half that happens to be a string.</para>
    /// <para>Guarded against a null list and a null <em>in</em> the list, as everything reachable from
    /// <see cref="GoalTileState"/> is: this is bound straight into an items control that reads
    /// <c>Severity</c> off every element.</para>
    /// </remarks>
    public List<GoalFinding> Findings
    {
        get => _findings;
        set => _findings = Without.Nulls(value);
    }
    private List<GoalFinding> _findings = [];

    /// <summary>Whether this message is a review with findings to draw as rows rather than as text.
    /// </summary>
    [JsonIgnore]
    public bool HasFindings => Findings.Count > 0;
}
