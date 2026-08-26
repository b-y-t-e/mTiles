using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// The one place that turns <see cref="AiPermissionMode"/> into the two strings anybody needs: the
/// flag the tool is given, and the words the tile shows.
/// <para>Pure and separate from both, because the flag values are somebody else's CLI contract —
/// <c>claude --permission-mode</c> accepts a fixed set of spellings and rejects anything else with a
/// usage error, which a headless run reports as the tool having failed. Spelling them in one place
/// makes that a single line to fix when the contract moves, and lets a test state it.</para>
/// </summary>
public static class AiPermissionModes
{
    /// <summary>What goes after <c>--permission-mode</c>, or <c>null</c> for "pass no flag".</summary>
    public static string? Flag(AiPermissionMode mode) => mode switch
    {
        AiPermissionMode.Auto => "auto",
        AiPermissionMode.AcceptEdits => "acceptEdits",
        AiPermissionMode.BypassPermissions => "bypassPermissions",
        _ => null,
    };

    /// <summary>How the mode reads in the tile's status strip — lower case, like everything else
    /// there.</summary>
    public static string Label(AiPermissionMode mode) => mode switch
    {
        AiPermissionMode.Auto => "auto",
        AiPermissionMode.AcceptEdits => "accept edits",
        AiPermissionMode.BypassPermissions => "bypass",
        _ => "tool default",
    };

    /// <summary>The modes in the order the combo box offers them: safest first, and the one that asks
    /// nothing last.</summary>
    public static IReadOnlyList<AiPermissionMode> All { get; } =
    [
        AiPermissionMode.Auto,
        AiPermissionMode.AcceptEdits,
        AiPermissionMode.BypassPermissions,
        AiPermissionMode.ToolDefault,
    ];

    /// <summary>The labels, for a combo box bound to strings.</summary>
    public static IReadOnlyList<string> Labels { get; } = All.Select(Label).ToList();

    /// <summary>The mode a label came from. Anything unrecognised is <see cref="AiPermissionMode.Auto"/>
    /// — the default — rather than an exception: this is fed by a combo box whose contents come from
    /// <see cref="Labels"/>, so the only way to miss is a change made here, and the safe answer to that
    /// is the default rather than a crash while the tile is being built.</summary>
    public static AiPermissionMode FromLabel(string? label) =>
        All.FirstOrDefault(m => string.Equals(Label(m), label, StringComparison.OrdinalIgnoreCase),
            AiPermissionMode.Auto);

    /// <summary>
    /// Whether what the tool printed is it rejecting the mode this tile gave it.
    /// </summary>
    /// <remarks>
    /// <para>The spellings are somebody else's CLI contract, and it has already moved once: an older
    /// Claude Code called the default mode <c>default</c> and does not know <c>auto</c>. On such a
    /// machine <em>every</em> run of this tile fails — on the default setting, with no goal ever
    /// reaching a plan — and the transcript says only "the AI tool reported a failure" over a usage
    /// message about a flag the user never typed and cannot see. The fix is two clicks away and nothing
    /// pointed at it.</para>
    /// <para>Matched on the flag's own name plus a word that says it was rejected, rather than on the
    /// word "permission" alone: the tool prints that in plenty of messages that are about the work. The
    /// cost of a miss is the old, unhelpful sentence; the cost of a false positive is telling somebody
    /// their settings are wrong when the failure was something else.</para>
    /// </remarks>
    public static bool LooksLikeRejectedMode(string? toolOutput)
    {
        var text = toolOutput ?? "";
        if (!text.Contains("--permission-mode", StringComparison.OrdinalIgnoreCase)) return false;

        return text.Contains("invalid", StringComparison.OrdinalIgnoreCase)
               || text.Contains("unknown", StringComparison.OrdinalIgnoreCase)
               || text.Contains("unrecognized", StringComparison.OrdinalIgnoreCase)
               || text.Contains("unrecognised", StringComparison.OrdinalIgnoreCase)
               || text.Contains("usage:", StringComparison.OrdinalIgnoreCase)
               || text.Contains("allowed choices", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What to tell the user when it was. Names the control and the value that always works.
    /// </summary>
    public const string RejectedModeAdvice =
        "This looks like the AI tool refusing the permission mode this tile asked for, which an older " +
        "version of it will do for a mode it has never heard of. Pick \"tool default\" in the strip " +
        "above to pass no flag at all, or update the tool.";
}
