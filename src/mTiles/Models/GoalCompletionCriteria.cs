namespace mTiles.Models;

/// <summary>
/// When a goal is allowed to call itself done, as the user set it on the tile.
/// <para>All of this used to be one <c>const int MaxIterations = 5</c> plus the presence of the string
/// "VERDICT: PASS". The defaults here reproduce that behaviour for anyone who never opens the panel —
/// no errors, no warnings, the goal met — except that the two mechanical stops are on, because both
/// only ever end a run that was already going nowhere.</para>
/// </summary>
public sealed class GoalCompletionCriteria
{
    /// <summary>How many <see cref="GoalSeverity.Error"/> findings may remain. Zero, and moving it off
    /// zero is a deliberate act.</summary>
    public int MaxErrors { get; set; }

    /// <summary>How many <see cref="GoalSeverity.Warning"/> findings may remain. Zero by default, which
    /// is strict; a goal in a codebase with existing debt is where raising this earns its keep.</summary>
    public int MaxWarnings { get; set; }

    /// <summary>Whether the review has to say the goal was actually reached. Separate from the finding
    /// counts because "no bugs" and "does what was asked" are different questions.</summary>
    public bool RequireGoalMet { get; set; } = true;

    /// <summary>Attempts at the goal, not attempts per launch: a run that is paused and resumed
    /// finishes the attempt it was in the middle of rather than opening a new one.</summary>
    public int MaxIterations { get; set; } = 5;

    /// <summary>
    /// A command the tile runs itself after each implementation — <c>dotnet build</c>, <c>npm test</c>.
    /// <para>The one completion criterion that is not the tool's opinion of its own work. Its exit code
    /// is a hard gate and its output goes into the review prompt, so the review argues with a compiler
    /// rather than with the diff alone. Empty means the step is skipped entirely.</para>
    /// </summary>
    public string VerifyCommand
    {
        get => _verifyCommand;
        set => _verifyCommand = value ?? "";
    }
    private string _verifyCommand = "";

    // The two mechanical stops — two reviews in a row reaching the same conclusion, and an
    // implementation that left the working tree exactly as it found it — used to be settings here and
    // checkboxes on the panel. They are neither now: both are always on.
    //
    // A setting is worth having where two users would reasonably choose differently. Nobody
    // reasonably chooses to spend attempts on a tool that just wrote nothing, or on a review that has
    // already said the same thing twice — turning either off buys nothing but a longer wait for the
    // same ending. They were switches over a decision with one sensible answer, and every switch on
    // that panel costs a line the user has to read and decide about.
    //
    // What replaces them is the summary saying plainly which of the two ended the run, which is the
    // part the user actually needed. Old goal files may still carry the two fields; System.Text.Json
    // ignores what it does not recognise, so they are read as absent and that is correct — the
    // behaviour they used to switch off is no longer switchable.

    public GoalCompletionCriteria Copy() => new()
    {
        MaxErrors = MaxErrors,
        MaxWarnings = MaxWarnings,
        RequireGoalMet = RequireGoalMet,
        MaxIterations = MaxIterations,
        VerifyCommand = VerifyCommand,
    };
}
