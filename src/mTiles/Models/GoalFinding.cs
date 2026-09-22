using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Services;

namespace mTiles.Models;

/// <summary>
/// How much a review finding matters, and therefore whether it can stop a goal from finishing.
/// <para>Four levels, and one question they deliberately do not answer: whether the goal was actually
/// reached is not a severity at all and lives on <see cref="GoalReviewResult.GoalMet"/> instead.
/// Squeezing it in here was the old <c>VERDICT: PASS</c>: one word carrying both "the code is sound"
/// and "the code does what was asked", so a review that found no bugs in an implementation of the wrong
/// thing passed.</para>
/// </summary>
public enum GoalSeverity
{
    /// <summary>
    /// Works as written and still must not stand: it breaks a stated constraint or assumption of the
    /// goal, or fails outside the one case in front of it — a platform limit, a race, data loss, a
    /// security hole.
    /// <para>Its own level rather than a loud <see cref="Error"/>, because it answers a different
    /// question. An error says the code is <em>wrong</em>; a blocker says the code is <em>unacceptable</em>,
    /// which is what a reviewer means when they write "this passes the tests and cannot ship". Forcing
    /// that into the other two levels made the choice a bad one either way: "error" claims something is
    /// broken when it demonstrably runs, and "warning" invites it to be tolerated.</para>
    /// <para>It is also the one severity with <b>no threshold</b> — a blocker is never within tolerance,
    /// where errors have a limit the user can raise for a codebase carrying known debt.</para>
    /// </summary>
    Blocker,

    /// <summary>Broken, wrong, or missing. Stops the goal by default.</summary>
    Error,

    /// <summary>Works, but should not stay as it is — a risk, or a Clean Code / SOLID violation.</summary>
    Warning,

    /// <summary>Worth knowing, not worth blocking on. Never counted against a completion criterion, and
    /// deliberately kept out of the next implement prompt, where it competes with real defects for the
    /// tool's attention and for the prompt's own size budget.</summary>
    Suggestion,
}

/// <summary>One thing a review found. Everything except <see cref="Severity"/> and <see cref="Line"/> is
/// free text from the tool, so all of it refuses a null the way the rest of the saved state does.</summary>
/// <remarks>
/// <para>Observable, which is the one thing in <c>Models/</c> that usually is not — the convention here
/// is a DTO with no change notification, and the precedent for breaking it is
/// <see cref="GitFileChange"/>, for exactly the same reason: a row the user ticks is a row two places
/// have to agree about. A finding is drawn in the transcript and again in the dialog a badge opens,
/// and the gate that decides what is fixed next reads the same objects. Without notification the tick
/// would be right wherever it was clicked and stale everywhere else.</para>
/// <para>The three properties below that carry the tick are <see cref="JsonIgnoreAttribute"/>d, and
/// that is not tidiness. What is dismissed is kept once, on the goal's own state, as the list the
/// review prompt and the completion check are both built from; a copy of the answer on every finding
/// in every review message in the transcript is a second record of one fact, and the way it fails is
/// a finding shown as dismissed that nothing is actually subtracting.</para>
/// </remarks>
public sealed partial class GoalFinding : ObservableObject
{
    /// <summary>
    /// Whether this finding is going back to the tool to be fixed. Ticked unless somebody says
    /// otherwise.
    /// </summary>
    /// <remarks>Phrased as the affirmative on purpose: the checkbox beside it means "fix this", so what
    /// the user unticks is what they are letting stand. A flag called <c>Dismissed</c> would be a
    /// checkbox whose ticked state is the exception, which is how a list of six arrives with six boxes
    /// to clear.</remarks>
    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(ShowPick))]
    private bool _fix = true;

    /// <summary>Whether the tick can be moved right now — true while the gate this finding belongs to
    /// is open, false once the run has moved past it, so an old review keeps the record of what was
    /// chosen without offering to change a decision that has already been acted on.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(ShowPick))]
    private bool _canPick;

    /// <summary>
    /// Whether the tick is drawn at all.
    /// </summary>
    /// <remarks>While the gate is open, and afterwards only where the answer was no. A review nobody
    /// was offered a choice about — the gate switched off, or a finding that arrived after the decision
    /// was taken — is drawn exactly as it always was, with no column of permanently ticked boxes down
    /// the side saying nothing.</remarks>
    [JsonIgnore]
    public bool ShowPick => CanPick || !Fix;

    public GoalSeverity Severity { get; set; }

    /// <summary>What kind of problem it is — correctness, goal, solid, tests, security, performance.
    /// Free text on purpose: a tool that invents a sixth category should not have its finding dropped
    /// for it.</summary>
    public string Category
    {
        get => _category;
        set => _category = value ?? "";
    }
    private string _category = "";

    public string File
    {
        get => _file;
        set => _file = value ?? "";
    }
    private string _file = "";

    /// <summary>Null when the tool did not say, which is common and not an error.</summary>
    public int? Line { get; set; }

    public string Title
    {
        get => _title;
        set => _title = value ?? "";
    }
    private string _title = "";

    public string Detail
    {
        get => _detail;
        set => _detail = value ?? "";
    }
    private string _detail = "";

    /// <summary>
    /// What makes two findings the same defect: severity, file and title, case-folded.
    /// </summary>
    /// <remarks>
    /// <para>One definition, because both the things that compare findings compare them across an
    /// implementation: <see cref="GoalReviewResult.Fingerprint"/> asks whether two consecutive reviews
    /// found the same set, and <c>GoalDismissals</c> asks whether the finding in front of it is one the
    /// user has already said is not to be fixed. Written twice they would drift, and only one of them
    /// would be tested.</para>
    /// <para><b>Not the line</b>, and not because the line is uninteresting — because an implementation
    /// moves every line below its own edits, so the same untouched defect comes back three lines lower
    /// and a line-sensitive match calls it a new one. For the fingerprint that meant the "going round in
    /// a circle" stop never firing; for a dismissal it meant the finding returning ticked on the next
    /// lap, counted against the criteria and back in the feedback, with the user's decision undone in
    /// silence. What it costs is the rarer collision the line used to prevent: two findings of one
    /// severity in one file under the <em>same title</em> are folded into one, so a tick beside either
    /// dismisses both. That is a worse trade the other way round — the collision dismisses a second
    /// defect the user was already minded to leave in the same file, while the miss makes the tick
    /// useless exactly where it was asked for.</para>
    /// <para>And not the detail, which is prose the reviewer rewrites on every run for the same
    /// defect.</para>
    /// </remarks>
    [JsonIgnore]
    public string Defect => $"{Severity}:{File}:{Title}".ToLowerInvariant();
}
