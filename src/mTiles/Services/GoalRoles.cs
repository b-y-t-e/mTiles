using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// Which job a Goal tile's AI call is doing, and how hard it is asked to think for it.
/// </summary>
/// <remarks>
/// <para>Pure and argued in a table test, the same construction as <see cref="ChainPolicy"/> and
/// <c>UsagePace</c>: what it holds is an opinion about how to spend somebody's money, so it is
/// readable without a tile, a process or a settings file.</para>
/// <para><b>Why the work is the cheap one.</b> It reverses this application's earlier opinion — that
/// the budget is in attempts, so a shallow attempt costs as much of it as a careful one — and the
/// reversal is argued in <c>docs/adr/0003-effort-by-role-in-a-goal-run.md</c>. In short: separating the
/// reviewer from the author is what the evidence supports, and spending the compute on the review is
/// not; errors from a cheaper author are also the easier ones for a review to catch.</para>
/// <para><b>Why the review is nonetheless high and not medium.</b> This repository's own goal logs are
/// a measurement of exactly that step, and they are in <c>docs/GOAL.md</c>: twenty-one reviews at
/// <see cref="AiEffort.Medium"/> returned one finding or none and eight came back empty, against
/// twenty-one at <see cref="AiEffort.High"/> where not one was empty and the median was four. An empty
/// review is not a cheap review — it is a run that passes <c>RequireGoalMet</c> on its first attempt
/// and reports a goal with an unfixed bug in it as met. The ADR argues against trading a second
/// reviewing *agent* for a deeper one; it says nothing against the level this measurement fixes.</para>
/// <para><b>Why that level is nonetheless not the default any more.</b> The measurement says what a
/// review at <see cref="AiEffort.High"/> catches, not what every run is worth paying for it: a deep
/// review on a two-line change is most of the run's cost for a step that had little to read. So the
/// deep one keeps its rung and its name — <see cref="GoalEffortPreset.Careful"/>, the old
/// <c>balanced</c> unchanged, one click away — and <c>balanced</c> now names the rung below it.
/// Anybody who had chosen the old word is moved onto <c>careful</c> rather than onto the cheaper
/// review; only the word moved, nobody's run did. See <c>AppSettings.LegacyGoalEffortPreset</c>.</para>
/// </remarks>
public static class GoalRoles
{
    /// <summary>
    /// What a phase is asking the agent to do.
    /// </summary>
    /// <remarks>Everything that does not write and is not a judgement is planning — including
    /// <see cref="GoalPhase.Summary"/>, which is the same train of thought read back. There is no case
    /// for <see cref="GoalRole.Commit"/> here on purpose: the commit plan has no phase of its own and is
    /// asked for during whichever one the run is in, so its caller names the role rather than deriving
    /// it. A phase this build does not know reads as planning, which is the role that may do the least.
    /// </remarks>
    public static GoalRole For(GoalPhase phase) => phase switch
    {
        GoalPhase.Implement => GoalRole.Work,
        GoalPhase.Review => GoalRole.Review,
        _ => GoalRole.Planning,
    };

    /// <summary>
    /// What the commit plan is always run at, whatever the preset says.
    /// </summary>
    /// <remarks>A constant rather than a row in the presets: naming which of the changed files belong
    /// together and writing a subject line for them is mechanical work on a list this application has
    /// already computed, and there is nothing in it for a model to think longer about. Being a constant
    /// is the point — no preset and no field can set it wrong.</remarks>
    public const AiEffort CommitEffort = AiEffort.Low;

    /// <summary>
    /// How hard this preset asks a given role to think.
    /// </summary>
    /// <remarks><see cref="GoalRole.Commit"/> answers <see cref="CommitEffort"/> for every preset,
    /// <see cref="GoalEffortPreset.ToolDefault"/> included — that preset means "pass no flag", which is
    /// what the commit run would rather have than a level nobody chose, so it is the one case where the
    /// constant gives way. A preset this build does not know is read as
    /// <see cref="GoalEffortPreset.Balanced"/>, the default, rather than throwing while a tile is being
    /// built.</remarks>
    public static AiEffort EffortFor(GoalEffortPreset preset, GoalRole role)
    {
        if (preset == GoalEffortPreset.ToolDefault) return AiEffort.ToolDefault;
        if (role == GoalRole.Commit) return CommitEffort;

        return preset switch
        {
            GoalEffortPreset.Thorough => role switch
            {
                GoalRole.Work => AiEffort.Medium,
                _ => AiEffort.High,
            },
            GoalEffortPreset.Careful => role switch
            {
                GoalRole.Work => AiEffort.Low,
                GoalRole.Review => AiEffort.High,
                _ => AiEffort.Medium,
            },
            GoalEffortPreset.Cheap => AiEffort.Low,
            _ => role switch
            {
                GoalRole.Work => AiEffort.Low,
                _ => AiEffort.Medium,
            },
        };
    }

    /// <summary>The presets in the order the picker offers them, with the one that asks nothing last.
    /// </summary>
    public static IReadOnlyList<GoalEffortPreset> All { get; } =
    [
        GoalEffortPreset.Balanced,
        GoalEffortPreset.Careful,
        GoalEffortPreset.Thorough,
        GoalEffortPreset.Cheap,
        GoalEffortPreset.ToolDefault,
    ];

    /// <summary>The roles a preset describes, in the order a run reaches them.</summary>
    /// <remarks>Commit is left out, and is the only omission: it is a constant, so listing it would put
    /// a row in front of the user that nothing they do here can change.</remarks>
    public static IReadOnlyList<GoalRole> Described { get; } =
        [GoalRole.Planning, GoalRole.Work, GoalRole.Review];

    /// <summary>How it reads in the strip — one lower-case word, like everything else there.</summary>
    public static string Label(GoalEffortPreset preset) => preset switch
    {
        GoalEffortPreset.Careful => "careful",
        GoalEffortPreset.Thorough => "thorough",
        GoalEffortPreset.Cheap => "cheap",
        GoalEffortPreset.ToolDefault => "default",
        _ => "balanced",
    };

    /// <summary>What a role is called on the row that spells a preset out.</summary>
    public static string Name(GoalRole role) => role switch
    {
        GoalRole.Work => "work",
        GoalRole.Review => "review",
        GoalRole.Commit => "commit",
        _ => "plan",
    };

    /// <summary>
    /// The three levels behind the one word, for the picker row's own description.
    /// </summary>
    /// <remarks>The whole reason the strip can carry one control where the feature has three: the
    /// detail is on the row, visible the moment the list is opened and invisible the rest of the time.
    /// </remarks>
    public static string Description(GoalEffortPreset preset) =>
        preset == GoalEffortPreset.ToolDefault
            ? "Passes no effort flag at all, so each tool's own settings decide."
            : string.Join(" · ",
                Described.Select(role =>
                    $"{Name(role)} {AiEfforts.Label(EffortFor(preset, role))}"));

    /// <summary>The preset a label came from.</summary>
    /// <remarks>Anything unrecognised is <see cref="GoalEffortPreset.Balanced"/> — the default — rather
    /// than an exception, the rule <c>AiEfforts.FromLabel</c> follows: the only way to miss is a change
    /// made here, and the safe answer to that is the default rather than a crash while a tile is being
    /// built.</remarks>
    public static GoalEffortPreset FromLabel(string? label) =>
        All.FirstOrDefault(
            preset => string.Equals(Label(preset), label, StringComparison.OrdinalIgnoreCase),
            GoalEffortPreset.Balanced);

    /// <summary>The labels, for a picker bound to strings.</summary>
    public static IReadOnlyList<string> Labels { get; } = All.Select(Label).ToList();

    /// <summary>
    /// The preset that stands for a single effort level somebody set before presets existed.
    /// </summary>
    /// <remarks>
    /// <para>By value, with one exception that carries the whole rule: <see cref="AiEffort.High"/> was
    /// the old default, so a file holding it is indistinguishable from a file nobody ever touched — and
    /// it reads as <see cref="GoalEffortPreset.Balanced"/>, the new default, rather than as somebody
    /// asking for high everywhere. That is the same rule <c>SettingsService.AdoptEmbeddedFont</c>
    /// follows: a stored value that is exactly the old default is not a decision.</para>
    /// <para>Everything else was typed on purpose and is honoured in the direction it was typed —
    /// <c>low</c> to the cheapest preset, <c>xhigh</c> and <c>max</c> to the dearest, <c>default</c> to
    /// the one that passes nothing. Nobody's runs get dearer than what they asked for, and only the
    /// people who never chose get the new, cheaper default.</para>
    /// </remarks>
    public static GoalEffortPreset FromLegacyEffort(AiEffort effort) => effort switch
    {
        AiEffort.Low => GoalEffortPreset.Cheap,
        AiEffort.XHigh or AiEffort.Max => GoalEffortPreset.Thorough,
        AiEffort.ToolDefault => GoalEffortPreset.ToolDefault,
        _ => GoalEffortPreset.Balanced,
    };
}
