using mTiles.Models;

namespace mTiles.Services;

/// <summary>Where the gate is: still counting, or waiting for somebody.</summary>
internal enum GoalGateState
{
    /// <summary>The clock is running and will let the loop carry on by itself.</summary>
    Counting,

    /// <summary>Nothing moves until the user says so.</summary>
    Waiting,
}

/// <summary>
/// The rules of the pause between a review and the next attempt, apart from the timer that carries
/// them out.
/// <para>Pure and separate for the reason <see cref="ChainPolicy"/> and <see cref="GoalLoopPolicy"/>
/// are: the loop around it needs an AI process and a git worktree to turn over once, and every one of
/// these rules is a condition that would otherwise be written inline and unreachable by a test.</para>
/// </summary>
internal static class GoalReviewGatePolicy
{
    /// <summary>What a goal that has never been configured waits — long enough to read a heading and
    /// reach for a tick, short enough that a run left alone is not a run stopped.</summary>
    public const int DefaultSeconds = 15;

    /// <summary>The shortest and the longest wait a file may ask for. The floor is not zero: a gate
    /// that expires the instant it is drawn is <see cref="GoalReviewGateMode.Off"/> wearing another
    /// name, and reaching one by typing in a box is how somebody loses the feature without knowing
    /// they turned it off.</summary>
    public const int MinSeconds = 3;

    /// <inheritdoc cref="MinSeconds"/>
    public const int MaxSeconds = 600;

    /// <summary>The wait this goal actually gets, whatever the file or the box says.</summary>
    public static int Seconds(int typed) => Math.Clamp(typed, MinSeconds, MaxSeconds);

    /// <summary>
    /// Whether the gate opens at all.
    /// </summary>
    /// <param name="mode">The goal's own setting.</param>
    /// <param name="pickable">Whether this review left anything to tick or untick. A review that found
    /// nothing to pick from — prose the parser could not structure, or a clean list with only the
    /// verdict against it — has no question to ask, and a gate with an empty list in it is a wait
    /// charged for nothing.</param>
    /// <param name="hasNextAttempt">Whether the run would carry on afterwards. With the budget spent
    /// the loop is about to summarise, so there is nothing for a pick to change.</param>
    /// <remarks>The two modes answer <paramref name="pickable"/> differently on purpose.
    /// <see cref="GoalReviewGateMode.Countdown"/> exists to offer the pick, so with nothing to pick it
    /// is a wait charged for nothing and is skipped; <see cref="GoalReviewGateMode.Manual"/> is
    /// somebody saying they want the lap to stop whatever it found, and skipping it because the review
    /// held only blockers would be the tile deciding it knew better.</remarks>
    public static bool Opens(GoalReviewGateMode mode, bool pickable, bool hasNextAttempt) =>
        hasNextAttempt && mode switch
        {
            GoalReviewGateMode.Manual => true,
            GoalReviewGateMode.Countdown => pickable,
            _ => false,
        };

    /// <summary>
    /// Whether this finding can be left unfixed.
    /// </summary>
    /// <remarks>Errors, warnings and suggestions. The first two go back to the tool and are counted
    /// unless unticked; a suggestion is the other way round — left alone unless ticked, when it goes
    /// back and holds the goal open until a review stops raising it (see
    /// <see cref="GoalWorkflowEngine.IncludedSuggestions"/>). Not a blocker: that severity is the one with no tolerance in the
    /// completion criteria either, and for the same reason: it is what a reviewer writes when the
    /// change is unacceptable rather than merely wrong, so a tick beside it would be the tolerance the
    /// panel deliberately does not offer, reached from somewhere else.</remarks>
    public static bool CanPick(GoalSeverity severity) =>
        severity is GoalSeverity.Error or GoalSeverity.Warning or GoalSeverity.Suggestion;

    /// <summary>The labels the picker offers, in the order it offers them.</summary>
    public static IReadOnlyList<string> Labels { get; } =
        Enum.GetValues<GoalReviewGateMode>().Select(Label).ToList();

    /// <summary>What a mode is called on screen.</summary>
    public static string Label(GoalReviewGateMode mode) => mode switch
    {
        GoalReviewGateMode.Countdown => "countdown",
        GoalReviewGateMode.Manual => "pause",
        _ => "off",
    };

    /// <summary>The sentence under a row of the picker.</summary>
    public static string Description(GoalReviewGateMode mode) => mode switch
    {
        GoalReviewGateMode.Countdown =>
            "Show the findings and carry on by itself when the clock runs out. Touching a tick stops " +
            "the clock and waits for Resume.",
        GoalReviewGateMode.Manual =>
            "Stop after every review and wait for Resume, however long that takes.",
        _ => "Hand the findings straight back to the tool and carry on.",
    };

    /// <summary>The mode a label names, falling back to the default rather than throwing — the rule
    /// <c>GoalRoles.FromLabel</c> follows, and for the same reason: the only way to miss is a label
    /// this build no longer offers, which is a setting to read forgivingly and not a crash.</summary>
    public static GoalReviewGateMode FromLabel(string? label) =>
        Enum.GetValues<GoalReviewGateMode>().FirstOrDefault(
            m => string.Equals(Label(m), label, StringComparison.OrdinalIgnoreCase),
            GoalReviewGateMode.Countdown);

    /// <summary>Where a freshly opened gate starts.</summary>
    public static GoalGateState Initial(GoalReviewGateMode mode) =>
        mode == GoalReviewGateMode.Countdown ? GoalGateState.Counting : GoalGateState.Waiting;

    /// <summary>
    /// Where the gate goes when a tick is touched: always to waiting, and never back.
    /// <para>This is the rule the whole feature is asked for. Somebody who has just decided one finding
    /// is not worth fixing is reading the rest of the list, and a clock that goes on running underneath
    /// them re-implements the review they are still making up their mind about. The clock does not
    /// restart either — that would be a gate that can be kept open only by fidgeting with it.</para>
    /// </summary>
    public static GoalGateState AfterEdit(GoalGateState _) => GoalGateState.Waiting;

    /// <summary>Whether the loop may carry on by itself. Only while counting, and only once the clock
    /// is out.</summary>
    public static bool Expired(GoalGateState state, int remainingSeconds) =>
        state == GoalGateState.Counting && remainingSeconds <= 0;

    /// <summary>
    /// What the gate says about itself.
    /// <para>Here rather than in the view because it is the one sentence that has to agree with the
    /// state machine above: a block that says "continuing in 4 s" beside a clock that stopped two
    /// findings ago is worse than one that says nothing.</para>
    /// </summary>
    public static string Line(GoalGateState state, int remainingSeconds, int fixing, int total) =>
        state == GoalGateState.Counting
            ? $"Fixing {fixing} of {total} — continuing in {Math.Max(remainingSeconds, 0)} s"
            : $"Fixing {fixing} of {total} — paused. Resume when you are ready.";
}
