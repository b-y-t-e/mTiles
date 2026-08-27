using System.Text.RegularExpressions;

namespace mTiles.Services;

/// <summary>
/// Whether what a tool printed is it refusing one particular flag.
/// </summary>
/// <remarks>
/// <para>This tile passes two flags of its own that the user never typed — <c>--permission-mode</c> and
/// <c>--effort</c> — and a tool older than either rejects it and runs nothing. That fails <b>every</b>
/// goal on that machine, on the default setting, so the message has to name the flag and the way out.
/// Naming the <em>wrong</em> flag is worse than naming none: it sends somebody to a setting that cannot
/// help while the one that can goes unmentioned.</para>
/// <para><b>Which is why this reads lines rather than the whole of stdout.</b> Both matchers used to
/// ask "does this text contain the flag, and does it contain a word like unknown" — and a CLI that
/// refuses one flag prints its <em>entire</em> usage, which lists both. Every rejection therefore
/// matched both, and the first one asked won, whichever flag had actually been refused. A rejection
/// names its flag on the same line: <c>error: unknown option '--effort'</c>,
/// <c>option '--permission-mode &lt;mode&gt;' argument 'auto' is invalid</c>. That is the signal.</para>
/// <para>The bare usage dump with no error line is kept as a second, weaker answer, and only where the
/// text names this flag and none of the others — which is the one case where a usage message on its own
/// still says something unambiguous.</para>
/// </remarks>
internal static partial class RejectedFlag
{
    /// <param name="flag">The flag to blame, spelled as it goes on the command line.</param>
    /// <param name="valueRejectionCounts">
    /// Whether a refused <em>value</em> counts as well as a refused flag.
    /// <para>The two flags differ here and the difference is measured. <c>--effort</c> with a value it
    /// does not know warns and carries on — calling that a configuration problem would send somebody to
    /// fix a setting that works. <c>--permission-mode</c> with a value it does not know refuses to run
    /// ("argument 'auto' is invalid. Allowed choices are ..."), which is the same dead end as an
    /// unknown flag and wants the same sentence.</para>
    /// </param>
    /// <param name="otherFlags">The other flags this application passes uninvited. They are what makes
    /// a usage dump ambiguous, so their presence withdraws the weaker answer.</param>
    public static bool Named(string? toolOutput, string flag, bool valueRejectionCounts,
        params string[] otherFlags)
    {
        var text = toolOutput ?? "";
        if (text.Length == 0 || !text.Contains(flag, StringComparison.OrdinalIgnoreCase)) return false;

        foreach (var line in text.Split('\n'))
            if (line.Contains(flag, StringComparison.OrdinalIgnoreCase)
                && (valueRejectionCounts ? RejectsAnything().IsMatch(line) : RejectsFlag().IsMatch(line)))
                return true;

        // No error line naming anything. A usage message is then all there is to go on, and it is only
        // worth acting on when it mentions this flag alone.
        return text.Contains("usage:", StringComparison.OrdinalIgnoreCase)
               && !otherFlags.Any(other => text.Contains(other, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The flag itself refused. Both spellings of "recognised", because the tools here are written on
    /// both sides of the Atlantic.
    /// </summary>
    /// <remarks>
    /// The word <c>option</c> is what keeps this off a warning about a <em>value</em>: "Unknown
    /// --effort value 'bogus' — ignoring it" is a run that carried on and produced an answer.
    /// </remarks>
    [GeneratedRegex(@"(unknown|unrecognized|unrecognised|invalid|not recognized)\s+option",
        RegexOptions.IgnoreCase)]
    private static partial Regex RejectsFlag();

    /// <summary>The flag or its value refused — the wider reading, for a flag whose bad value is
    /// equally fatal. "allowed choices" is how commander.js words it.</summary>
    [GeneratedRegex(@"unknown|unrecognized|unrecognised|invalid|allowed choices|not recognized",
        RegexOptions.IgnoreCase)]
    private static partial Regex RejectsAnything();
}
