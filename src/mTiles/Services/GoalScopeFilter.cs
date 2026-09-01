using System.Text;
using System.Text.RegularExpressions;

namespace mTiles.Services;

/// <summary>
/// The composer's text as a scope: which <c>@</c> paths it names, and whether a changed file is inside
/// them.
/// </summary>
/// <remarks>
/// <para>The three Goal buttons take the composer seriously when it is not empty: its words travel as a
/// narrowing guideline in the prompt, and the paths it names as <c>@</c> mentions go further — they
/// <b>filter the working tree</b> before it reaches the tool, so a review of "only this file" is judged
/// on a block that holds only that file. The soft half is a sentence; the hard half is this class.</para>
/// <para>Pure, and separate from the git calls, because both halves are opinions about text: which
/// tokens are mentions, and what a named folder means for a changed file beside it.</para>
/// </remarks>
internal static partial class GoalScopeFilter
{
    /// <summary>
    /// The paths the composer names as <c>@</c> mentions, deduplicated, forward-slashed.
    /// </summary>
    /// <remarks>
    /// Two spellings arrive from <see cref="FileMentionToken.Mention"/>: a bare token, and a quoted one
    /// for a path with whitespace. The <c>@</c> must open a word — an address like
    /// <c>someone@example.com</c> is prose, not a mention — which is what the lookbehind buys.
    /// </remarks>
    public static IReadOnlyList<string> Mentions(string? composerText)
    {
        var text = composerText ?? "";
        var found = new List<string>();

        foreach (Match mention in QuotedMention().Matches(text))
            Add(found, mention.Groups["path"].Value);

        foreach (Match mention in BareMention().Matches(text))
            Add(found, mention.Groups["path"].Value);

        return found;
    }

    private static void Add(List<string> found, string raw)
    {
        // A sentence ends the way sentences do, and a path that picked one up stops matching the tree
        // it names. Nothing inside a path ends this way on Windows, where these characters are illegal
        // in a file name — and the trailing slash goes too: "@src/" is how a folder mention is typed,
        // and the scope it names is "src", not "src/".
        var cleaned = raw.Replace('\\', '/').Trim().TrimEnd('.', ',', ';', ':', '!', '?').TrimEnd('/');
        if (cleaned.Length == 0) return;

        // A token with neither a directory nor an extension names nothing on disk — "@admin about the
        // failure" is prose with an at-sign in it. Letting it into the scope would filter the whole
        // tree to nothing over a word nobody meant as a path, and the note saying so would not give the
        // diff back.
        if (!cleaned.Contains('/') && !cleaned.Contains('.')) return;

        if (!found.Contains(cleaned, StringComparer.OrdinalIgnoreCase)) found.Add(cleaned);
    }

    /// <summary>
    /// Whether one changed path sits inside the scope the composer named.
    /// </summary>
    /// <remarks>
    /// A mention that names a folder covers everything under it, which is what a folder means; a
    /// mention that names a file covers that file. Compared without case, because Windows paths are.
    /// </remarks>
    public static bool Matches(string path, IReadOnlyList<string> mentions)
    {
        var candidate = path.Replace('\\', '/').Trim('/');
        foreach (var mention in mentions)
        {
            if (candidate.Equals(mention, StringComparison.OrdinalIgnoreCase)) return true;
            if (candidate.StartsWith(mention + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// The diff with every file outside the scope removed, section by section — or null when nothing
    /// survived.
    /// </summary>
    /// <remarks>
    /// A git diff is a run of sections each opened by <c>diff --git</c>, so the split is on those
    /// headings and a section is kept when either of its two named paths is inside the scope. A null
    /// answer is a real one: the caller turns it into the note saying the scope matched nothing, which
    /// is what keeps the tool from reading an omission as a clean tree.
    /// </remarks>
    public static string? Diff(string? diff, IReadOnlyList<string> mentions)
    {
        if (mentions.Count == 0 || string.IsNullOrEmpty(diff)) return diff;

        var kept = new StringBuilder();
        var start = IndexOfHeading(diff, 0);
        while (start >= 0)
        {
            var next = IndexOfHeading(diff, start + 1);
            var section = diff[start..(next < 0 ? diff.Length : next)];
            if (SectionMatches(section, mentions)) kept.Append(section);
            start = next;
        }

        return kept.Length == 0 ? null : kept.ToString();
    }

    /// <summary>The untracked list with everything outside the scope removed — one path per line, so
    /// the line is the path. Null when nothing survived.</summary>
    public static string? Lines(string? names, IReadOnlyList<string> mentions)
    {
        if (mentions.Count == 0 || string.IsNullOrWhiteSpace(names)) return names;

        var kept = names.Split('\n')
            .Where(line => Matches(line.Trim(), mentions))
            .ToList();

        return kept.Count == 0 ? null : string.Join("\n", kept);
    }

    /// <summary>
    /// The <c>--stat</c> summary with every line about a file outside the scope removed.
    /// </summary>
    /// <remarks>
    /// A stat line carries its path before the <c>" | "</c> that starts the counts. The totals at the
    /// bottom ("N files changed, …") describe the <em>whole</em> change, and after a filter that is
    /// exactly what they no longer describe — dropped rather than left lying.
    /// <para>A rename's line names both paths, <c>old =&gt; new</c> — and git writes the brace form
    /// when the rename shares a directory, <c>src/{Agents =&gt; Auth}/X.cs</c>, where neither side of
    /// the arrow is the whole new path. The line is matched on the path <em>after</em> the arrow, with
    /// the shared prefix before the brace put back in front of it, because the scope names the file the
    /// change is now at. Lines a wide <c>--stat</c> elided to <c>…/Name</c> still match nothing — an
    /// accepted cosmetic gap; the row is supplementary and its diff section is what carries the file.</para>
    /// </remarks>
    public static string? Stat(string? summary, IReadOnlyList<string> mentions)
    {
        if (mentions.Count == 0 || string.IsNullOrWhiteSpace(summary)) return summary;

        var kept = summary.Split('\n')
            .Where(line =>
            {
                var at = line.IndexOf(" | ", StringComparison.Ordinal);
                return at > 0 && Matches(RenamedTo(line[..at].Trim()), mentions);
            })
            .ToList();

        return kept.Count == 0 ? null : string.Join("\n", kept);
    }

    /// <summary>The path a stat line is about: what is left of the counts, with a rename's arrow and
    /// braces resolved to the path the file now has.</summary>
    private static string RenamedTo(string left)
    {
        var arrow = left.IndexOf(" => ", StringComparison.Ordinal);
        if (arrow < 0) return left;

        var after = left[(arrow + 4)..];
        var brace = left.LastIndexOf('{');
        return brace < 0
            ? after
            : left[..brace] + after.Replace("}", "");
    }

    private static int IndexOfHeading(string diff, int from)
    {
        var at = from == 0 ? 0 : diff.IndexOf("\ndiff --git ", from, StringComparison.Ordinal);
        return at < 0 ? -1 : at == 0 ? 0 : at + 1;
    }

    private static bool SectionMatches(string section, IReadOnlyList<string> mentions)
    {
        // The heading names both sides, and renamed files name them differently; either matching is
        // what "this file is in scope" means here. git quotes a heading whose path is not plain ASCII,
        // so the quotes come off before the paths do. A section with no newline at all — a diff whose
        // last line is a heading — is its own heading.
        var end = section.IndexOf('\n');
        var heading = (end < 0 ? section : section[..end]).Replace("\"", "");
        var at = heading.IndexOf(" b/", StringComparison.Ordinal);
        if (at > 0 && Matches(heading[(at + 3)..], mentions)) return true;
        var a = heading.IndexOf(" a/", StringComparison.Ordinal);
        return a > 0 && Matches(heading[(a + 3)..(at > 0 ? at : heading.Length)], mentions);
    }

    [GeneratedRegex(@"(?<=^|\s)@""(?<path>[^""]+)""")]
    private static partial Regex QuotedMention();

    [GeneratedRegex(@"(?<=^|\s)@(?<path>[^\s@""]+)")]
    private static partial Regex BareMention();
}
