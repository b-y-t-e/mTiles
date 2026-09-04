namespace mTiles.Services;

/// <summary>
/// Clears up after the writer <see cref="WorkspaceAgentFiles"/> replaces: the <c># Database access</c>
/// section the old <c>ClaudeLocalMdWriter</c> injected into <c>claude.local.md</c> and into
/// <c>AGENTS.md</c>.
/// </summary>
/// <remarks>
/// <para><b>A class of its own, and called explicitly.</b> It has its own reason to change and its own
/// expiry date — when no installation can still be carrying the old section, the whole file goes — and
/// it edits files in somebody else's repository, which is not something a constructor should do as a
/// side effect of being reached.</para>
/// <para>Not a "fix the file name's case" migration. <c>claude.local.md</c> was never read by Claude
/// Code on Linux at all — it opens <c>CLAUDE.local.md</c> literally — and the section it carried is now
/// a skill, so the file has no successor to be renamed into.</para>
/// <para><b>The section goes, the file stays — even when nothing is left in it.</b> That is
/// <see cref="GitIgnoreFile"/>'s rule and it is here for the same reason: the old writer created
/// <c>AGENTS.md</c> where there was none, so a repository whose user then committed it would otherwise
/// see this application delete a tracked file out of their working tree the first time the workspace
/// was opened. An emptied file is a line in <c>git status</c>; a deleted tracked one is a deletion
/// waiting to be staged into somebody's history.</para>
/// <para>Idempotent by shape rather than by a flag: it removes only a section that carries the bridge's
/// own evidence, so it runs once in practice and cannot take away a <c># Database access</c> heading a
/// person wrote themselves.</para>
/// </remarks>
public static class LegacyDatabaseSectionCleanup
{
    /// <summary>The two files the old writer put its section into, in the spelling it used.</summary>
    private static readonly string[] FilesTheOldWriterWroteTo =
        ["claude.local.md", WorkspaceAgentFiles.CanonicalInstructionFile];

    /// <summary>Cuts the old section out of both files, wherever one is still carrying it.</summary>
    public static void Run(string workspaceDir)
    {
        foreach (var fileName in FilesTheOldWriterWroteTo)
            RemoveOldSectionFrom(Path.Combine(workspaceDir, fileName));
    }

    private static void RemoveOldSectionFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            var content = ByteForByte.GetString(File.ReadAllBytes(path));
            var cleaned = WithoutOldSections(content);
            if (ReferenceEquals(cleaned, content)) return;

            WriteAtomically(path, ByteForByte.GetBytes(cleaned));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Could not clear the old database section out of '{0}': {1}", path, ex.Message);
        }
    }

    /// <summary>
    /// The one encoding that survives a round trip byte for byte: every byte is one character and every
    /// character is that byte again.
    /// </summary>
    /// <remarks><b>Bytes, not text</b> — <see cref="GitIgnoreFile"/>'s rule, and here for the same
    /// reason. <c>File.ReadAllText</c> plus <c>File.WriteAllText</c> re-encodes the whole file: a UTF-8
    /// BOM disappears and content saved in anything else — a <c>CLAUDE.local.md</c> somebody wrote in
    /// Windows-1250 — is decoded as UTF-8 and written back mangled beyond recovery, into a file this
    /// application only ever appended to and into somebody's repository, so the loss reaches a commit.
    /// Everything searched for and written here is ASCII, which every such encoding agrees on, so
    /// decoding the file this way leaves whatever else is in it exactly as it was.</remarks>
    private static readonly System.Text.Encoding ByteForByte = System.Text.Encoding.Latin1;

    /// <summary>The only characters trimmed at a cut, because a cut is made in <em>bytes</em>.</summary>
    /// <remarks><see cref="string.TrimEnd()"/> would take a byte such as <c>0xA0</c> for whitespace,
    /// which in a UTF-8 file is the second half of a non-breaking space — half a character removed and
    /// the other half left behind. These four mean the same in every encoding this can meet.</remarks>
    private static readonly char[] AsciiWhitespace = [' ', '\t', '\r', '\n'];

    /// <summary>
    /// Writes through a temporary file beside the real one and moves it into place, the rule
    /// <see cref="GitIgnoreFile"/> follows: truncating and then failing halfway would leave the user's
    /// own instructions gone, and the caller swallows failures so nothing would say so.
    /// </summary>
    private static void WriteAtomically(string path, byte[] content)
    {
        var temporary = path + ".mtiles-tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch { /* the original is untouched, which is the point */ }
            throw;
        }
    }

    /// <summary>The headings the old writer used across its generations, newest first.</summary>
    /// <remarks>Three, because the section was renamed twice and a repository can be carrying any of
    /// them: leaving an older one in place keeps a live <c>http://localhost:&lt;port&gt;</c> and the
    /// names of somebody's servers in a committed file after database access has been switched off,
    /// which is the one thing the blind delete exists to prevent.</remarks>
    private static readonly string[] OldSectionHeadings =
    [
        "# Database access",
        "# Database Service",
        "# List databases",
    ];

    /// <summary>The sentence the newest of the old sections opened with.</summary>
    private const string OldSectionSignature = "SQL queries via local HTTP bridge";

    /// <summary>The bridge endpoint every generation of the section spelled out, which is what says a
    /// section under one of those headings is ours and not somebody's own notes.</summary>
    /// <remarks><b>Both halves, never <c>/query/</c> alone.</b> That path on its own is an ordinary
    /// string in anybody's REST documentation, so it made the gate say "ours" about a section somebody
    /// wrote themselves. The old writer always spelled the whole address out
    /// (<c>http://localhost:&lt;port&gt;/query/...</c>), so asking for the pair costs nothing and stops
    /// the one shape that was being read as ours.</remarks>
    private static readonly string[] OldSectionEndpointSignature = ["http://localhost:", "/query/"];

    /// <summary>Answers <paramref name="content"/> itself when there was nothing of ours to remove, so
    /// the caller can tell "unchanged" from "emptied" without comparing strings.</summary>
    internal static string WithoutOldSections(string content)
    {
        var result = content;
        foreach (var heading in OldSectionHeadings)
            result = WithoutOldSection(result, heading);
        return result;
    }

    private static string WithoutOldSection(string content, string heading)
    {
        var from = 0;
        while (true)
        {
            var headingAt = IndexOfHeadingLine(content, heading, from);
            if (headingAt < 0) return content;

            // The next heading is looked for from the end of *this* heading line, never from the start
            // of the blank lines walked back over below: with enough blank lines in front of it, the
            // search found the newline introducing this very heading, the section came out as nothing
            // but whitespace, and the skip below landed on the same heading again - a loop with no way
            // out, on the UI thread that opens the workspace.
            var end = content.IndexOf("\n# ", headingAt + heading.Length, StringComparison.Ordinal);

            var start = headingAt;
            while (start > 0 && content[start - 1] is '\r' or '\n')
                start--;

            var section = end < 0 ? content[start..] : content[start..(end + 1)];
            if (!IsOurs(section))
            {
                // Somebody's own heading of the same name: skip past it and keep looking, because ours
                // may be further down the same file.
                if (end < 0) return content;
                from = end + 1;
                continue;
            }

            if (end < 0)
                return content[..start].TrimEnd(AsciiWhitespace);

            var before = content[..start].TrimEnd(AsciiWhitespace);
            var after = content[(end + 1)..];
            return before.Length == 0 ? after : before + "\n\n" + after;
        }
    }

    /// <summary>
    /// The offset of <paramref name="heading"/> where it really is a heading: at the start of a line,
    /// and with nothing after it on that line.
    /// </summary>
    /// <remarks>A plain <c>IndexOf</c> matched the second <c>#</c> of <c>## Database access</c> — one of
    /// the user's own subsections — and the cut then began mid-line, leaving an orphaned <c>#</c> behind
    /// and taking their section with it. Requiring the rest of the line to be empty keeps
    /// <c># Database access notes</c> theirs as well.</remarks>
    private static int IndexOfHeadingLine(string content, string heading, int from)
    {
        for (var at = content.IndexOf(heading, from, StringComparison.Ordinal); at >= 0;
             at = content.IndexOf(heading, at + 1, StringComparison.Ordinal))
        {
            if (at > 0 && content[at - 1] is not ('\n' or '\r'))
                continue;

            var lineEnd = content.IndexOf('\n', at);
            var rest = lineEnd < 0 ? content[(at + heading.Length)..] : content[(at + heading.Length)..lineEnd];
            if (rest.Trim(AsciiWhitespace).Length == 0)
                return at;
        }

        return -1;
    }

    private static bool IsOurs(string section) =>
        section.Contains(OldSectionSignature, StringComparison.Ordinal)
        || OldSectionEndpointSignature.All(part => section.Contains(part, StringComparison.Ordinal));
}
