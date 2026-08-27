using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// The one place that turns <see cref="AiEffort"/> into the two strings anybody needs: the flag the
/// tool is given, and the words the tile shows.
/// </summary>
/// <remarks>
/// Pure and separate from both, for the reason <see cref="AiPermissionModes"/> is: these values are
/// somebody else's CLI contract. Spelling them once makes a move in that contract one line to fix, and
/// lets a test state what the contract currently is.
/// </remarks>
public static class AiEfforts
{
    /// <summary>What goes after <c>--effort</c>, or <c>null</c> for "pass no flag".</summary>
    public static string? Flag(AiEffort effort) => effort switch
    {
        AiEffort.Low => "low",
        AiEffort.Medium => "medium",
        AiEffort.High => "high",
        AiEffort.XHigh => "xhigh",
        AiEffort.Max => "max",
        _ => null,
    };

    /// <summary>How it reads in the status strip — lower case, like everything else there.</summary>
    public static string Label(AiEffort effort) => Flag(effort) ?? "tool default";

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
    /// default — rather than an exception, for the reason <c>AiPermissionModes.FromLabel</c> gives: the
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
    /// <para>That is exactly what <see cref="AiPermissionModes.LooksLikeRejectedMode"/> was written
    /// for, and it is matched the same way: the flag's own name plus a word saying it was rejected,
    /// never one of those alone. A miss costs the old unhelpful sentence; a false positive tells
    /// somebody their settings are wrong when the failure was something else.</para>
    /// </remarks>
    public static bool LooksLikeRejectedEffort(string? toolOutput) =>
        RejectedFlag.Named(toolOutput, "--effort", valueRejectionCounts: false, "--permission-mode");

    /// <summary>What to tell the user when it was. Names the control and the value that always works.
    /// </summary>
    public const string RejectedEffortAdvice =
        "This looks like the AI tool refusing the effort flag this tile asked for, which a version of " +
        "it older than that flag will do. Pick \"tool default\" in the strip above to pass no flag at " +
        "all, or update the tool.";
}
