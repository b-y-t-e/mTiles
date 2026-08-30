using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// The canonical scale for <see cref="AiEffort"/>: what each level is called, where it sits, and what
/// an agent whose own scale is shorter should be given instead.
/// </summary>
/// <remarks>
/// Pure, and holding no agent's flags, for the reason <see cref="AiBehaviours"/> holds none: the way a
/// level reaches a CLI is that CLI's business — <c>--effort</c>, <c>--thinking</c>,
/// <c>-c model_reasoning_effort=…</c>, or a three-step scale — and each agent's <c>EffortArgs</c> says
/// it once. What is shared is the scale itself, and that is what is here.
/// </remarks>
public static class AiEfforts
{
    /// <summary>
    /// The level's canonical name, or <c>null</c> for "pass no flag".
    /// </summary>
    /// <remarks>Canonical rather than any one CLI's: claude, pi and agy all happen to use these words,
    /// which is convenient and not a contract — codex spells the same idea
    /// <c>-c model_reasoning_effort=…</c> and agy's scale stops at three. What each agent does with a
    /// level is its own <c>EffortArgs</c>; this is only what the level is called.</remarks>
    public static string? Name(AiEffort effort) => effort switch
    {
        AiEffort.Low => "low",
        AiEffort.Medium => "medium",
        AiEffort.High => "high",
        AiEffort.XHigh => "xhigh",
        AiEffort.Max => "max",
        _ => null,
    };

    /// <summary>How it reads in the status strip — lower case, like everything else there.</summary>
    public static string Label(AiEffort effort) => Name(effort) ?? "tool default";

    /// <summary>
    /// Where this level sits on the scale, as a number that can be compared.
    /// </summary>
    /// <remarks>Spelled out rather than taken from the enum's own order, which starts at
    /// <see cref="AiEffort.High"/> because that member is the default and was declared first.
    /// <see cref="AiEffort.ToolDefault"/> is off the scale: it is the absence of a level, not the
    /// lowest one.</remarks>
    public static int Rank(AiEffort effort) => effort switch
    {
        AiEffort.Low => 0,
        AiEffort.Medium => 1,
        AiEffort.High => 2,
        AiEffort.XHigh => 3,
        AiEffort.Max => 4,
        _ => -1,
    };

    /// <summary>
    /// The level to actually use when <paramref name="wanted"/> is not among
    /// <paramref name="supported"/>: the nearest one that is, and the <b>higher</b> of two equally
    /// near.
    /// </summary>
    /// <remarks>
    /// <para>To nearest with ties upward, which is the opposite of what <c>AiBehaviours.RoundDown</c>
    /// does, and deliberately. Being wrong here costs money and nothing else — while the tile is meant
    /// to be left alone, where a shallow attempt spends as much of the attempt budget as a careful one
    /// and produces less for it. So agy, whose scale stops at <c>high</c>, is given <c>high</c> for
    /// <c>xhigh</c> and for <c>max</c>.</para>
    /// <para><see cref="AiEffort.ToolDefault"/> asked for is <see cref="AiEffort.ToolDefault"/> given:
    /// passing no flag is something every CLI can do, and it is the way out for a version too old for
    /// the flag at all.</para>
    /// </remarks>
    public static AiEffort RoundToNearest(AiEffort wanted, IReadOnlyList<AiEffort> supported)
    {
        if (supported.Contains(wanted)) return wanted;

        var target = Rank(wanted);
        if (target < 0) return AiEffort.ToolDefault;

        return supported
            .Where(level => Rank(level) >= 0)
            .OrderBy(level => Math.Abs(Rank(level) - target))
            .ThenByDescending(Rank)
            .DefaultIfEmpty(AiEffort.ToolDefault)
            .First();
    }

    /// <summary>The levels in the order the combo box offers them, cheapest first, with the one that
    /// asks nothing last.</summary>
    public static IReadOnlyList<AiEffort> All { get; } =
    [
        AiEffort.Low,
        AiEffort.Medium,
        AiEffort.High,
        AiEffort.XHigh,
        AiEffort.Max,
        AiEffort.ToolDefault,
    ];

    /// <summary>The labels, for a combo box bound to strings.</summary>
    public static IReadOnlyList<string> Labels { get; } = All.Select(Label).ToList();

    /// <summary>The level a label came from. Anything unrecognised is <see cref="AiEffort.High"/> — the
    /// default — rather than an exception, for the reason <c>AiBehaviours.FromLabel</c> gives: the
    /// only way to miss is a change made here, and the safe answer to that is the default rather than a
    /// crash while a tile is being built.</summary>
    public static AiEffort FromLabel(string? label) =>
        All.FirstOrDefault(e => string.Equals(Label(e), label, StringComparison.OrdinalIgnoreCase),
            AiEffort.High);

    /// <summary>
    /// Whether what the tool printed is it refusing the flag rather than the value.
    /// </summary>
    /// <remarks>
    /// <para>Measured against Claude Code, and the two cases are not alike. An unknown <em>value</em>
    /// is forgiving — <c>Warning: Unknown --effort value 'bogus' — ignoring it and using the default
    /// effort</c>, and the run proceeds — so nothing here needs to guard the spellings. An unknown
    /// <em>flag</em> is fatal: a Claude Code from before <c>--effort</c> existed answers
    /// <c>error: unknown option '--effort'</c> and runs nothing, so <b>every</b> goal on that machine
    /// fails, on the default setting, over a flag the user never typed and cannot see.</para>
    /// <para>That is exactly what <see cref="AiBehaviours.LooksLikeRejectedMode"/> was written
    /// for, and it is matched the same way: the flag's own name plus a word saying it was rejected,
    /// never one of those alone. A miss costs the old unhelpful sentence; a false positive tells
    /// somebody their settings are wrong when the failure was something else.</para>
    /// </remarks>
    /// <param name="effortFlag">The flag the tool was actually given, from
    /// <c>IAiAgent.EffortFlagFor</c>. Not a constant here: <c>--effort</c> is Claude Code's
    /// spelling and <c>pi</c> calls the same idea <c>--thinking</c>, so a constant recognised one
    /// tool's refusal and left the other's as a bare "the AI tool reported a failure".</param>
    /// <param name="permissionFlag">The tool's other flag, needed to read a usage message: one is only
    /// worth acting on when it mentions this flag alone.</param>
    public static bool LooksLikeRejectedEffort(
        string? toolOutput, string? effortFlag, string? permissionFlag) =>
        effortFlag is { Length: > 0 }
        && RejectedFlag.Named(toolOutput, effortFlag, valueRejectionCounts: false,
            permissionFlag is { Length: > 0 } ? [permissionFlag] : []);

    /// <summary>What to tell the user when it was. Names the control and the value that always works.
    /// </summary>
    public const string RejectedEffortAdvice =
        "This looks like the AI tool refusing the effort flag this tile asked for, which a version of " +
        "it older than that flag will do. Pick \"tool default\" in the strip above to pass no flag at " +
        "all, or update the tool.";
}
