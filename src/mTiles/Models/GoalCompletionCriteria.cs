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
    /// Whether the work has to leave the project compiling. On unless the user says otherwise.
    /// </summary>
    /// <remarks>
    /// <para>An instruction in the prompt, not a command this tile runs. What it replaces was a verify
    /// command: the tile asked the tool for a shell line, asked the user to approve it, ran it, and
    /// gated completion on its exit code. That worked only in a repository that was already green —
    /// and a project whose build or tests are not is the ordinary case, so the gate spent every attempt
    /// on failures the goal had not caused and then reported the goal as not reached.</para>
    /// <para>The tool is standing in the repository and knows how this project is built, so it is told
    /// what has to be true and left to work out how to check it. That also puts the result where it is
    /// useful — in the tool's own review of its work — instead of in an exit code the user had to
    /// underwrite first.</para>
    /// </remarks>
    public bool RequireBuild { get; set; } = true;

    /// <summary>
    /// Whether the work has to leave the project's tests passing. On unless the user says otherwise —
    /// see <see cref="RequireBuild"/> for why this is a sentence in a prompt rather than a gate.
    /// </summary>
    /// <remarks>Separate from <see cref="RequireBuild"/> because the answers differ: a repository with
    /// a red suite nobody has got to yet still has to compile, and asking for green tests there is
    /// asking for work the user did not want.</remarks>
    public bool RequireTestsPass { get; set; } = true;

    /// <summary>
    /// Which SOLID principles the work is held to. All five unless the user says otherwise.
    /// </summary>
    /// <remarks>
    /// <para>Here rather than in the application's settings because it belongs to the goal, not to the
    /// person: the same user wants all five in a library they will maintain for years and none of them
    /// in a one-page script, and a global switch would make them choose once for both.</para>
    /// <para>Here rather than in a panel of its own because it is a completion criterion in the only
    /// sense that matters. A violation is reported as a warning, and the tolerance beside it is zero by
    /// default — so these switches decide, as directly as <see cref="MaxWarnings"/> does, what the run
    /// has to fix before it may stop.</para>
    /// <para>Guarded against a null in its own setter, the rule everything deserialised here follows: a
    /// property initialiser does not survive <c>"Solid": null</c>, and what follows is a
    /// <see cref="NullReferenceException"/> in the middle of building a prompt.</para>
    /// </remarks>
    public SolidPrinciples Solid
    {
        get => _solid;
        set => _solid = value ?? new SolidPrinciples();
    }
    private SolidPrinciples _solid = new();

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

    // VerifyCommand is gone the same way, and for a reason the switches above never had: it was a
    // criterion that could not be met. A repository whose tests are not all green is the ordinary case
    // rather than a broken one, and a command whose exit code gates completion turned every goal in
    // such a repository into a run that spent all its attempts on failures it did not cause and then
    // reported the goal as not reached. Old goal files carry the key; it is read as absent.

    public GoalCompletionCriteria Copy() => new()
    {
        MaxErrors = MaxErrors,
        MaxWarnings = MaxWarnings,
        RequireGoalMet = RequireGoalMet,
        MaxIterations = MaxIterations,
        RequireBuild = RequireBuild,
        RequireTestsPass = RequireTestsPass,
        // Copied, not shared. Copy() exists so a caller can hold the criteria as they were; handing it
        // the same instance would let a later edit reach back into the snapshot.
        Solid = Solid.Copy(),
    };
}
