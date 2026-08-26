using System.Text;

namespace mTiles.Services;

/// <summary>
/// Adds and removes a single marked entry in a <c>.gitignore</c>.
/// <para>Editing a file that belongs to the user's project — one they may have written by hand and will
/// certainly read in a diff — so every rule here is about touching as little as possible. Adding
/// appends and never rewrites. Removing takes away only the block that carries our marker: a line the
/// <em>user</em> wrote, even the very same line, is theirs and stays.</para>
/// <para><b>Bytes, not text.</b> Reading with <c>File.ReadAllText</c> and writing it back re-encodes the
/// whole file: a UTF-8 BOM disappears, and a <c>.gitignore</c> saved in any other encoding is decoded as
/// UTF-8 and written back as UTF-8 — mojibake that cannot be undone. Everything this class searches for
/// and writes is ASCII, which every encoding git can read agrees on byte for byte, so working on raw
/// bytes leaves whatever else is in the file exactly as it was.</para>
/// <para>A file operation rather than a git command, which is why it is not on <c>GitService</c>: git
/// has nothing to say about the contents of <c>.gitignore</c>, and putting it there would mean a class
/// that shells out for everything except this.</para>
/// </summary>
internal static class GitIgnoreFile
{
    private const string FileName = ".gitignore";

    /// <summary>Written above the entry, and the only thing that makes it ours. Without a marker,
    /// removing would take away an identical line the user added themselves years ago.</summary>
    private const string Marker = "# mTiles workspace state";

    /// <summary>
    /// Serialises the read-modify-write, because two Git tiles refresh independently and can be looking
    /// at the same repository — two workspaces under one checkout, or the same one open twice. Without
    /// it both read a file with no entry and both append one.
    /// <para>One lock for all paths rather than one per path: this runs rarely and finishes in
    /// microseconds, and a keyed pool would be more machinery than the contention justifies. It does
    /// not cover two copies of the application running at once — that needs a file lock, and the damage
    /// there is a duplicate line rather than a lost file.</para>
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Makes sure <paramref name="entry"/> is listed, under our marker. Returns true when the file was
    /// changed, so a caller can tell "already ignored" from "just ignored" — the difference between
    /// saying nothing and telling the user their repository was edited.
    /// </summary>
    /// <param name="directory">Whose <c>.gitignore</c> to edit. The workspace's own directory, which
    /// need not be the repository root — git reads a <c>.gitignore</c> per directory, so scoping the
    /// entry to the workspace is both correct and narrower than editing the root's file on behalf of a
    /// workspace buried somewhere in a monorepo.</param>
    public static async Task<bool> EnsureAsync(string directory, string entry, CancellationToken ct = default)
    {
        var path = Path.Combine(directory, FileName);

        await Gate.WaitAsync(ct);
        try
        {
            var lines = await ReadLinesAsync(path, ct);
            if (lines.Any(line => Matches(line, entry)))
                return false;   // listed already — by us or by the user, and either way nothing to do

            // Appended, never rewritten: the existing bytes are never read back out and put down again,
            // so a BOM stays a BOM and an unusual encoding stays whatever it was. What goes on the end
            // is ASCII, which reads the same in all of them.
            var nl = EndingOf(lines);
            var block = new StringBuilder();

            bool hadContent = lines.Any(line => line.Length > 0);
            if (hadContent)
            {
                // Finish a file that ended mid-line, then a blank line so our block reads as its own
                // thing rather than as a continuation of whatever came before.
                if (lines[^1].Length > 0)
                    block.Append(nl);
                block.Append(nl);
            }

            block.Append(Marker).Append(nl).Append(entry).Append(nl);

            await File.AppendAllTextAsync(path, block.ToString(), Ascii, ct);
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Takes away the block this class added — the blank line, the marker and the entry — and nothing
    /// else. Returns true when the file was changed.
    /// <para>An entry <em>not</em> under the marker is left alone. It is the same text, but it is the
    /// user's line: they may have written it long before this setting existed, and turning a setting
    /// off is not permission to delete something somebody else put there.</para>
    /// <para>The file itself is never deleted, even when nothing is left in it. This class cannot tell a
    /// <c>.gitignore</c> it created from an empty one the user committed, and deleting a tracked file
    /// turns into a deletion in their history — a worse outcome than leaving an empty file behind.</para>
    /// </summary>
    public static async Task<bool> RemoveAsync(string directory, string entry, CancellationToken ct = default)
    {
        var path = Path.Combine(directory, FileName);

        await Gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
                return false;

            var lines = SplitLines(await File.ReadAllBytesAsync(path, ct));
            int at = IndexOfOurBlock(lines, entry);
            if (at < 0)
                return false;

            // The marker and the entry, plus the single blank line we put in front of the marker — the
            // one we added, not every blank that happens to be there.
            int from = at;
            int count = 2;
            if (from > 0 && IsBlank(lines[from - 1]))
            {
                from--;
                count++;
            }

            lines.RemoveRange(from, count);
            await WriteAtomicallyAsync(path, JoinLines(lines), ct);
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Writes through a temporary file beside the real one and moves it into place.
    /// <para>Truncating the real file and then writing it leaves nothing behind if the write fails
    /// halfway — and the one caller swallows failures so the tile keeps working, which would turn a full
    /// disk into a silently emptied <c>.gitignore</c>. A move is the closest thing to all-or-nothing the
    /// filesystem offers.</para>
    /// </summary>
    private static async Task WriteAtomicallyAsync(string path, byte[] content, CancellationToken ct)
    {
        var temporary = path + ".mtiles-tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, content, ct);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch { /* the original is untouched, which is the point */ }
            throw;
        }
    }

    /// <summary>The index of our marker line, when the entry follows it. Anything else is not ours.</summary>
    private static int IndexOfOurBlock(List<byte[]> lines, string entry)
    {
        for (int i = 0; i < lines.Count - 1; i++)
        {
            if (Text(lines[i]).Trim() == Marker && Matches(lines[i + 1], entry))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Whether the entry is already listed, in any of the spellings that mean the same directory here.
    /// <para>A trailing slash is git's "directory only" and a leading one is "at this level, not in
    /// subdirectories" — different patterns in general, but for a directory sitting right here they all
    /// ignore it. Comparing the text literally would read a user's <c>/.mtiles/</c> as absent and add
    /// a second line that changes nothing. A negation (<c>!.mtiles/</c>) is deliberately not a match:
    /// it is the user turning the ignore off, and our line would go in above it and lose anyway.</para>
    /// </summary>
    private static bool Matches(byte[] line, string entry)
    {
        var trimmed = Text(line).Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('!'))
            return false;

        return trimmed.Trim('/').Equals(entry.Trim('/'), StringComparison.Ordinal);
    }

    /// <summary>
    /// The file's lines as raw bytes, or none when there is no file.
    /// <para>A failed read is <b>not</b> caught here, and that is the whole point: "no file" and "could
    /// not read the file" look identical to the caller, which would then decide the entry is missing and
    /// append a second one. A locked file must abandon the operation, not guess at its contents.</para>
    /// </summary>
    private static async Task<List<byte[]>> ReadLinesAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return [];

        return SplitLines(await File.ReadAllBytesAsync(path, ct));
    }

    /// <summary>Splits on LF and keeps any CR at the end of a line, so joining with LF is the exact
    /// inverse — trailing blank line included.</summary>
    private static List<byte[]> SplitLines(byte[] bytes)
    {
        var lines = new List<byte[]>();
        int start = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n')
                continue;
            lines.Add(bytes[start..i]);
            start = i + 1;
        }
        lines.Add(bytes[start..]);
        return lines;
    }

    private static byte[] JoinLines(List<byte[]> lines)
    {
        var joined = new List<byte>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                joined.Add((byte)'\n');
            joined.AddRange(lines[i]);
        }
        return [.. joined];
    }

    /// <summary>The line ending the file already uses, so the lines this class adds match it. Mixing
    /// them puts a whitespace change into a diff for no reason, and some tools flag the whole file.</summary>
    private static string EndingOf(List<byte[]> lines) =>
        lines.Any(line => line.Length > 0 && line[^1] == (byte)'\r') ? "\r\n" : "\n";

    private static bool IsBlank(byte[] line) => Text(line).Trim().Length == 0;

    /// <summary>A line as text, for comparing against our ASCII patterns. Bytes that are not valid UTF-8
    /// come back as replacement characters, which simply will not match anything we look for — nothing
    /// is ever written from this, so a misread costs nothing but a duplicate line.</summary>
    private static string Text(byte[] line) => Encoding.UTF8.GetString(line);

    /// <summary>What the appended block is encoded as. Everything in it is ASCII, so this produces the
    /// bytes any encoding git can read would have produced — and, unlike the UTF-8 encoder, it cannot
    /// put a BOM into the middle of somebody's file.</summary>
    private static readonly Encoding Ascii = Encoding.ASCII;
}
