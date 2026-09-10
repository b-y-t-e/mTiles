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
    /// Every <c>@</c> token the composer names, deduplicated and forward-slashed — cleaned, but not
    /// judged.
    /// </summary>
    /// <remarks>
    /// <para>Two spellings arrive from <see cref="FileMentionToken.Mention"/>: a bare token, and a
    /// quoted one for a path with whitespace. The <c>@</c> must open a word — an address like
    /// <c>someone@example.com</c> is prose, not a mention — which is what the lookbehind buys.</para>
    /// <para><b>What a token names is not decided here, and used to be half-decided here.</b> A syntax
    /// rule stood in front of this list and dropped any token carrying neither a slash nor a dot as
    /// prose, which cost the two things people most often point at. <c>@frontend</c> is a directory,
    /// and it never reached the scope: pointing at a folder read the whole tree unnarrowed. Worse, in
    /// a repository with a branch of that name the token then fell through to <c>GoalScopeRef</c>, so
    /// the diff was read from a branch instead of filtered to a folder — the one spelling where the
    /// rule did not merely lose the narrowing but answered a different question.</para>
    /// <para>So the callers ask the two things that know: the filesystem first, and
    /// <c>git rev-parse</c> with whatever is left. A token that is neither — <c>@admin</c>,
    /// <c>@mentions</c> — resolves as nothing and changes nothing, which is the same answer the
    /// syntax rule gave, for a good deal less. This class stays pure text, and keeps no opinion about
    /// what exists.</para>
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

    /// <summary>
    /// What the composer says once its <c>@</c> tokens are taken out.
    /// </summary>
    /// <remarks>
    /// <para>The question this answers is whether the box holds a <b>goal</b>. A box holding only
    /// pointers does not: "@frontend" is not something to achieve, and adopted as a goal it is a
    /// sentence nobody wrote. It is a narrowing, and the goal is still the one to be read out of the
    /// changes.</para>
    /// <para>Text rather than a flag, because the caller wants both answers from it: whether anything
    /// is left, and what the words are once the pointers stop competing with them.</para>
    /// <para><b>Cut by position, never by text.</b> Replacing a token's spelling wherever it occurs
    /// makes one mention eat the head of another: in "@src @src/Cart.cs" the first replacement takes
    /// the opening of the second and leaves "/Cart.cs" behind, which reads here as words — so a box
    /// holding nothing but pointers was adopted as a goal, the one case this method exists to catch.
    /// The matches carry their own offsets, so what is kept is copied between them and the spelling
    /// of a token is never looked for a second time.</para>
    /// </remarks>
    public static string WordsOnly(string? composerText)
    {
        var text = composerText ?? "";
        var spans = new List<(int Start, int End)>();

        foreach (Match mention in QuotedMention().Matches(text))
            spans.Add((mention.Index, mention.Index + mention.Length));

        foreach (Match mention in BareMention().Matches(text))
            spans.Add((mention.Index, mention.Index + mention.Length));

        // Kept rather than cut, so two spans that overlap — a bare token inside a quoted one — cost
        // nothing to reconcile: the writer simply never goes backwards.
        var kept = new StringBuilder();
        var copiedTo = 0;
        foreach (var (start, end) in spans.OrderBy(span => span.Start))
        {
            if (start > copiedTo) kept.Append(text, copiedTo, start - copiedTo);
            if (end > copiedTo)
            {
                kept.Append(' ');
                copiedTo = end;
            }
        }

        if (copiedTo < text.Length) kept.Append(text, copiedTo, text.Length - copiedTo);

        return kept.ToString().Trim();
    }

    private static void Add(List<string> found, string raw)
    {
        // A sentence ends the way sentences do, and a path that picked one up stops matching the tree
        // it names. Nothing inside a path ends this way on Windows, where these characters are illegal
        // in a file name — and the trailing slash goes too: "@src/" is how a folder mention is typed,
        // and the scope it names is "src", not "src/".
        var text = raw.Replace('\\', '/').Trim();
        var cleaned = KeepRangeSeparator(text, text.AsSpan().TrimEnd(SentenceEnd).Length).TrimEnd('/');
        if (cleaned.Length == 0) return;

        if (!found.Contains(cleaned, StringComparer.OrdinalIgnoreCase)) found.Add(cleaned);
    }

    /// <summary>Punctuation that ends a sentence and never a path.</summary>
    private static readonly char[] SentenceEnd = ['.', ',', ';', ':', '!', '?'];

    /// <summary>
    /// The token cut back to <paramref name="end"/>, unless the cut would eat a range's own
    /// separator.
    /// </summary>
    /// <remarks>
    /// <para><b>A trailing <c>..</c> is git's spelling, not the user's full stop.</b>
    /// <c>@master..</c> is the one documented way to name a branch called after an ordinary word —
    /// <c>GoalScopeRef.NamesARef</c> refuses the bare token on purpose, because <c>admin</c>,
    /// <c>dev</c> and <c>release</c> are words as often as they are branches. Trimmed with the rest of
    /// the sentence punctuation it came back as <c>master</c>, which is exactly the bare word that
    /// rule throws away: the scope never formed, the tree was read from <c>HEAD</c>, and nothing
    /// anywhere said so. <c>@master..HEAD</c> escaped it only because a letter happens to end it.</para>
    /// <para>Exactly two are put back, never the whole run: <c>@master...</c> at the end of a sentence
    /// is a range and a full stop, and three dots are a symmetric difference this tile does not
    /// answer. A token whose cut stops anywhere else — <c>@src/Cart.cs.</c> — is untouched, because
    /// what follows the cut is one dot and not a separator.</para>
    /// </remarks>
    private static string KeepRangeSeparator(string token, int end) =>
        end + 2 <= token.Length && token.AsSpan(end, 2) is ".."
            ? token[..(end + 2)]
            : token[..end];

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
    /// Whether this change holds anything inside the paths the user named.
    /// </summary>
    /// <remarks>
    /// <para>One rule, asked by two callers who must not disagree about it. <c>GoalDiffContext</c>
    /// asks it to decide whether the filter can be applied at all — a named path holding none of the
    /// change is a specification, a folder or a note, and filtering on it would show the tool an empty
    /// block. The Goal tile asks it to decide whether a typed run has anything to review: pointing at
    /// something is not the same as pointing at work that is already there.</para>
    /// <para><b>The diff alone cannot answer it.</b> A tree whose whole change is new files has an
    /// empty <c>git diff HEAD</c> and a full <c>ls-files --others</c>, so both halves are asked and
    /// either one surviving is enough. The <c>--stat</c> summary is deliberately not part of the
    /// question: it is derived from the diff and describes it, so on its own it is totals about a
    /// change nothing is showing.</para>
    /// <para>Naming no paths narrows nothing, so the answer is simply whether there is a change at
    /// all.</para>
    /// </remarks>
    public static bool HoldsChange(string? diff, string? untracked, IReadOnlyList<string>? mentions)
    {
        if (diff is not { Length: > 0 } && untracked is not { Length: > 0 }) return false;
        if (mentions is not { Count: > 0 }) return true;

        return Diff(diff, mentions) is { Length: > 0 } || Lines(untracked, mentions) is { Length: > 0 };
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
