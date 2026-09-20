using System.Globalization;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>What a line of a unified diff is, for colouring it.</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    Hunk,
    Header,
}

/// <summary>
/// One line of a unified diff, as it is drawn: the line's own number in the file, the one-character
/// marker that says what happened to it, and the code with that marker taken off.
/// </summary>
/// <remarks>
/// The three are separate because the row is drawn as three columns — a number gutter, a marker and
/// the code — the way the agent's own terminal interface draws it. Left as one string the number would
/// have to be padded into the text and the tint would start at the <c>+</c> instead of at the edge of
/// the row. <paramref name="Number"/> is null for the lines a file has no number for — the hunk header
/// and the diff's own header — and for every line of a patch nobody can number (see
/// <see cref="DiffLines.Parse"/>).
/// </remarks>
public sealed record DiffLine(string Text, DiffLineKind Kind, int? Number = null)
{
    /// <summary>The number as it is drawn, empty where there is none.</summary>
    public string NumberText => Number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>The one character that says what happened to this line.</summary>
    public string Marker => Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "-",
        _ => string.Empty,
    };
}

/// <summary>
/// A unified diff as lines worth colouring. Pure.
/// </summary>
/// <remarks>Capped, because a diff is drawn as one control per line in a list that does not virtualise
/// — the same constraint the Goal tile's transcript lives under — and a regenerated lock file is tens of
/// thousands of lines nobody reads in a conversation.</remarks>
public static class DiffLines
{
    public const int MaxLines = 1500;

    /// <summary>A patch of whole files, numbered: every line is drawn with its own line number.</summary>
    /// <remarks>Only a patch git made of the files themselves may be read this way. The hunk headers of
    /// one made of a fragment — an <c>Edit</c> tool's old and new text, an approval's detail — count
    /// from the first line of the fragment, so a change to line 412 would be numbered 1, and nothing on
    /// screen tells that apart from a real line number.</remarks>
    public static IReadOnlyList<DiffLine> ParseFilePatch(string? diff) => Read(diff, numbered: true);

    /// <summary>A patch of anything, unnumbered: what happened to each line, and nothing about where
    /// it sits in a file.</summary>
    public static IReadOnlyList<DiffLine> Parse(string? diff) => Read(diff, numbered: false);

    private static IReadOnlyList<DiffLine> Read(string? diff, bool numbered)
    {
        if (string.IsNullOrEmpty(diff)) return [];

        var lines = diff.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var result = new List<DiffLine>(Math.Min(lines.Length, MaxLines + 1));

        // The two counters a hunk header sets. A removed line is numbered in the file it was removed
        // from and everything else in the file as it now stands, which is what makes a block of added
        // lines read as the numbers you would see if you opened it. Both start at 1, which is where a
        // hunk header that cannot be read leaves them too: content with no `@@` at all — an approval's
        // detail made of bare +/- lines, a provider's patch without hunk headers — is still numbered
        // from the first line of a file rather than from a line 0 no file has.
        var oldNumber = 1;
        var newNumber = 1;

        // Whether we are inside a hunk, which is the only thing that tells a line of the file from a
        // line of the format: "--" removed from a file is written "---" and is otherwise taken for the
        // patch's own `--- a/path`, losing its number, its ground and the old side's count from there on.
        var inHunk = false;

        for (var i = 0; i < lines.Length && i < MaxLines; i++)
        {
            var line = lines[i];

            // A hunk runs until a line that cannot be part of one. Every line of a hunk body begins
            // with a space, a `+`, a `-` or git's `\` marker — or is empty, which is how a trailing
            // space is sometimes stripped from a blank context line — so anything else is the next
            // file's own header (`diff --git …`, `index …`) and ends the hunk. Asking the shape of
            // the line rather than the kind it was given is what keeps a second file in one patch
            // from being numbered on from the first: inside a hunk a header can never be recognised
            // as one, because `--- a/b` is also what removing `-- a/b` looks like.
            if (line.StartsWith("@@", StringComparison.Ordinal)) inHunk = true;
            else if (inHunk && (!ContinuesHunk(line) || OpensAnotherFile(lines, i))) inHunk = false;

            var kind = KindOf(line, inHunk);
            int? number = null;
            switch (kind)
            {
                case DiffLineKind.Hunk:
                    (oldNumber, newNumber) = HunkStarts(line);
                    break;
                case DiffLineKind.Added:
                    number = newNumber++;
                    break;
                case DiffLineKind.Removed:
                    number = oldNumber++;
                    break;
                case DiffLineKind.Context:
                    number = newNumber++;
                    oldNumber++;
                    break;
            }

            result.Add(new DiffLine(WithoutMarker(line, kind), kind, numbered ? number : null));
        }

        if (lines.Length > MaxLines)
            result.Add(new DiffLine($"… {lines.Length - MaxLines} more lines", DiffLineKind.Header));
        return result;
    }

    /// <summary>The code on the line, with the column that says what happened to it taken off.</summary>
    /// <remarks>One character and only where it is the marker's own: a context line carries a leading
    /// space in the format and a blank line in the file is an empty string, which must not become a
    /// negative length.</remarks>
    private static string WithoutMarker(string line, DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added or DiffLineKind.Removed => line.Length > 0 ? line[1..] : line,
        DiffLineKind.Context => line.StartsWith(' ') ? line[1..] : line,
        _ => line,
    };

    /// <summary>The two line numbers a <c>@@ -a,b +c,d @@</c> header starts its hunk at.</summary>
    /// <remarks>Only the ranges between the two <c>@@</c> markers are read, and each side only once: git
    /// puts the enclosing function's own line after the closing marker, so a header such as
    /// <c>@@ -10,3 +12,4 @@ x = -12</c> would otherwise have its trailing <c>-12</c> read as the old
    /// start and number every removed line in the hunk from the wrong place. A header this cannot read
    /// leaves both at 1 rather than throwing: a gutter wrong by a hunk is one to ignore, and an
    /// exception here is a conversation that will not draw.</remarks>
    private static (int Old, int New) HunkStarts(string line)
    {
        var old = 1;
        var @new = 1;
        int? oldRead = null;
        int? newRead = null;

        var body = line.StartsWith("@@ ", StringComparison.Ordinal) ? line[3..] : line;
        var close = body.IndexOf("@@", StringComparison.Ordinal);
        if (close >= 0) body = body[..close];

        foreach (var part in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length < 2) continue;
            var value = Number(part[1..]);
            if (value is null) continue;
            if (part[0] == '-') oldRead ??= value;
            else if (part[0] == '+') newRead ??= value;
        }

        if (oldRead is not null) old = oldRead.Value;
        if (newRead is not null) @new = newRead.Value;
        return (old, @new);
    }

    private static int? Number(string text)
    {
        var comma = text.IndexOf(',');
        if (comma >= 0) text = text[..comma];
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static bool IsNoNewlineMarker(string line) => line.StartsWith("\\ ", StringComparison.Ordinal);

    /// <summary>Whether a line can be part of a hunk's body at all.</summary>
    private static bool ContinuesHunk(string line) =>
        line.Length == 0 || line[0] is ' ' or '+' or '-' or '\\';

    /// <summary>Whether the next file's own header starts here, even though its first line could be
    /// read as a removal.</summary>
    /// <remarks>Several patches concatenated with nothing but their <c>--- a/x</c> / <c>+++ b/x</c>
    /// pair between them are the ordinary shape of a multi-edit tool call, and the pair is the only
    /// separator there is: read as hunk body, the second and every later file opened with a red
    /// <c>-- a/x</c> and a green <c>++ b/x</c>. The pair is what settles it — a removed <c>-- a/x</c>
    /// immediately followed by an added <c>++ b/x</c> is the one thing this reads wrongly, and no
    /// patch of real code has ever looked like that.</remarks>
    private static bool OpensAnotherFile(string[] lines, int index) =>
        lines[index].StartsWith("--- ", StringComparison.Ordinal)
        && index + 1 < lines.Length
        && lines[index + 1].StartsWith("+++ ", StringComparison.Ordinal);

    /// <summary>What a line is, asked with the one piece of context that settles it.</summary>
    /// <remarks>Inside a hunk only the first character means anything, because the rest of the line is
    /// somebody's code: a removed <c>--</c> arrives as <c>---</c> and an added <c>++</c> as <c>+++</c>,
    /// which read as the patch's own file headers exactly where they are impossible. Git's
    /// <c>\ No newline at end of file</c> is the one line inside a hunk that belongs to neither side,
    /// so it is a header — numbered it would take a number the file does not have and push every line
    /// after it, and every later hunk, one too far.</remarks>
    private static DiffLineKind KindOf(string line, bool inHunk) => line switch
    {
        _ when line.StartsWith("@@", StringComparison.Ordinal) => DiffLineKind.Hunk,
        _ when inHunk && IsNoNewlineMarker(line) => DiffLineKind.Header,
        _ when inHunk => line.StartsWith('+') ? DiffLineKind.Added
            : line.StartsWith('-') ? DiffLineKind.Removed
            : DiffLineKind.Context,
        _ when line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)
               || line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal)
               || line.StartsWith("Index: ", StringComparison.Ordinal) || line.StartsWith("===", StringComparison.Ordinal)
            => DiffLineKind.Header,
        _ when line.StartsWith('+') => DiffLineKind.Added,
        _ when line.StartsWith('-') => DiffLineKind.Removed,
        _ => DiffLineKind.Context,
    };
}
