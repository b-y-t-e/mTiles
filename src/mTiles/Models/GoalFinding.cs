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
public sealed class GoalFinding
{
    /// <summary>
    /// The category of the one finding no AI tool writes: the tile's own, standing for a verify command
    /// that failed or never finished.
    /// </summary>
    /// <remarks>
    /// Named rather than left as free text because two places have to agree about it —
    /// <c>GoalTileViewModel.AddVerifyFinding</c> makes it and <c>GoalCompletionPolicy.WhyNotMet</c>
    /// declines to count it a second time — and a string spelled twice is a string that will be
    /// spelled differently once.
    /// </remarks>
    public const string VerifyCategory = "verify";

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
}
