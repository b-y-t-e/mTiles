using System.Text;

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
/// One line of a unified diff, as it is drawn: the one-character marker that says what happened to
/// it, and the code with that marker taken off.
/// </summary>
/// <remarks>
/// The two are separate because what is done with them differs: the kind is what colours the line,
/// and the text is what is read. <see cref="DiffMarkdown"/> puts them back together, because the
/// viewer that draws the patch reads the marker out of the text itself.
/// </remarks>
public sealed record DiffLine(string Text, DiffLineKind Kind)
{
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
/// <remarks>Capped, because the whole patch is laid out at once by the transcript's own viewer — the
/// same constraint the Goal tile's transcript lives under — and a regenerated lock file is tens of
/// thousands of lines nobody reads in a conversation.</remarks>
public static class DiffLines
{
    public const int MaxLines = 1500;

    /// <summary>A patch git made of the files themselves.</summary>
    /// <remarks>The patch's own header — <c>diff --git</c>, <c>index</c>, <c>---</c>, <c>+++</c> — is
    /// dropped: it names the file, and the file is the row this patch is drawn under. Four lines of
    /// machinery above two lines of content is what it came to on a small change, and the one fact in
    /// them that is not on the row already is a pair of blob hashes. What is inside a hunk is kept
    /// whatever its kind, git's <c>\ No newline at end of file</c> included: that one is not the
    /// file's header but the only line that explains a change to its final newline.</remarks>
    public static IReadOnlyList<DiffLine> ParseFilePatch(string? diff) => Read(diff, dropFileHeader: true);

    /// <summary>A patch of anything: a fragment, whose own header is the only thing above it that says
    /// which file it is, so it is kept.</summary>
    public static IReadOnlyList<DiffLine> Parse(string? diff) => Read(diff, dropFileHeader: false);

    private static IReadOnlyList<DiffLine> Read(string? diff, bool dropFileHeader)
    {
        if (string.IsNullOrEmpty(diff)) return [];

        var lines = diff.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var result = new List<DiffLine>(Math.Min(lines.Length, MaxLines + 1));

        // The header is only machinery while there is something under it to read instead — a hunk, or
        // the notice git writes in place of one. A rename, a copy and a mode change are whole patches
        // git writes with neither, and their header *is* the change: dropped, the row would say no diff
        // came back for a file git described in full.
        var dropHeader = dropFileHeader && HasSomethingBelowTheHeader(lines);

        // Whether we are inside a hunk, which is the only thing that tells a line of the file from a
        // line of the format: "--" removed from a file is written "---" and is otherwise taken for the
        // patch's own `--- a/path`, losing its ground and every line of the hunk after it with it.
        var inHunk = false;

        for (var i = 0; i < lines.Length && i < MaxLines; i++)
        {
            var line = lines[i];

            // A hunk runs until a line that cannot be part of one. Every line of a hunk body begins
            // with a space, a `+`, a `-` or git's `\` marker — or is empty, which is how a trailing
            // space is sometimes stripped from a blank context line — so anything else is the next
            // file's own header (`diff --git …`, `index …`) and ends the hunk. Asking the shape of
            // the line rather than the kind it was given is what keeps a second file in one patch
            // from being read as the first one's body: inside a hunk a header can never be recognised
            // as one, because `--- a/b` is also what removing `-- a/b` looks like.
            if (line.StartsWith("@@", StringComparison.Ordinal)) inHunk = true;
            else if (inHunk && (!ContinuesHunk(line) || OpensAnotherFile(lines, i))) inHunk = false;

            var kind = KindOf(line, inHunk);
            // Only the headers above the hunks, which is what `dropFileHeader` asks for — and of those
            // only the ones that merely name the file this patch is drawn under. Inside a hunk the one
            // header there is is git's `\ No newline at end of file`, and that is the only line saying
            // the change is to the file's last character rather than to its text.
            if (dropHeader && !inHunk && NamesTheFile(line) && !NamesWhereTheFileCameFrom(line)) continue;

            result.Add(new DiffLine(WithoutMarker(line, kind), kind));
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

    /// <summary>Whether the patch carries anything but the lines naming the file, within the lines
    /// that will be read.</summary>
    /// <remarks>Bounded by the same cap the reading is, so a patch whose first hunk falls past it does
    /// not have its header dropped and come out as nothing but the note saying how much was left.</remarks>
    private static bool HasSomethingBelowTheHeader(string[] lines)
    {
        for (var i = 0; i < lines.Length && i < MaxLines; i++)
            if (lines[i].StartsWith("@@", StringComparison.Ordinal) || IsInsteadOfAHunk(lines[i])) return true;
        return false;
    }

    private static bool IsNoNewlineMarker(string line) => line.StartsWith("\\ ", StringComparison.Ordinal);

    /// <summary>The header lines git writes above a hunk that say nothing but which file this is.</summary>
    /// <remarks>Dropped from a file's patch because the row the patch is drawn under already names the
    /// file — and that is the whole of what they carry, down to a pair of blob hashes and a mode. The
    /// two notices git writes <em>instead</em> of a hunk are deliberately not here: a binary change has
    /// no other content at all, so dropping <c>Binary files a/x and b/x differ</c> would leave a row
    /// saying there is nothing to show about a file that changed.</remarks>
    private static bool NamesTheFile(string line) =>
        FileHeaderPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>The whole of git's file header: the pair of paths, the blob index, and the extended
    /// header lines a mode change, a rename or a copy adds to them.</summary>
    private static readonly string[] FileHeaderPrefixes =
    [
        "diff ", "index ", "--- ", "+++ ", "Index: ", "===",
        "old mode ", "new mode ", "new file mode ", "deleted file mode ",
        "similarity index ", "dissimilarity index ",
        "copy from ", "copy to ", "rename from ", "rename to ",
    ];

    /// <summary>The header lines that name the file's <em>other</em> name — where a rename or a copy
    /// took it from.</summary>
    /// <remarks>Kept even out of a file's own patch, and that is the one exception to dropping the
    /// header: the row above the patch draws the new path alone, so with these gone a rename that also
    /// changed the file says nothing anywhere about what it used to be called. <c>rename to</c> and
    /// <c>copy to</c> are not here — that name is the row's own.</remarks>
    private static bool NamesWhereTheFileCameFrom(string line) =>
        line.StartsWith("rename from ", StringComparison.Ordinal)
        || line.StartsWith("copy from ", StringComparison.Ordinal);

    /// <summary>The notices git writes in place of a hunk, which are content and not machinery.</summary>
    private static bool IsInsteadOfAHunk(string line) =>
        line.StartsWith("Binary files ", StringComparison.Ordinal)
        || line.StartsWith("GIT binary patch", StringComparison.Ordinal);

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
    /// so it is a header: it is git talking about the patch and not a line of anybody's file.</remarks>
    private static DiffLineKind KindOf(string line, bool inHunk) => line switch
    {
        _ when line.StartsWith("@@", StringComparison.Ordinal) => DiffLineKind.Hunk,
        _ when inHunk && IsNoNewlineMarker(line) => DiffLineKind.Header,
        _ when inHunk => line.StartsWith('+') ? DiffLineKind.Added
            : line.StartsWith('-') ? DiffLineKind.Removed
            : DiffLineKind.Context,
        _ when line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)
               || NamesTheFile(line) || IsInsteadOfAHunk(line)
            => DiffLineKind.Header,
        _ when line.StartsWith('+') => DiffLineKind.Added,
        _ when line.StartsWith('-') => DiffLineKind.Removed,
        _ => DiffLineKind.Context,
    };
}

/// <summary>
/// A patch as the markdown the transcript's own viewer renders. Pure.
/// </summary>
/// <remarks>
/// <para>The diff is drawn by the control that draws every message in this tile
/// (<c>Notepad.Avalonia</c>'s <c>MarkdownViewer</c>, through <c>GoalMarkdownView</c>), which is what
/// buys the one thing a patch on screen is for: its selection spans the whole document, so two lines
/// can be dragged through and copied together. One control per row could not do that — a selection
/// belongs to one <c>SelectableTextBlock</c> and nothing in Avalonia spans siblings.</para>
/// <para><b>The colour is the viewer's own.</b> Measured against Notepad.Avalonia 0.3.1: there is no
/// syntax highlighting in it at all — <c>CodeLanguage</c> is stored and never read, and the package
/// carries no language names — which is why the vendored copy carries <c>HighlightDiff</c>. It reads
/// the marker out of the line's own text, so the marker is put back at the head of each line here
/// rather than kept in a column of its own — and the hunk headers go with it, since they are what
/// tells a removed <c>--</c> from the patch's own <c>---</c>.</para>
/// </remarks>
public static class DiffMarkdown
{
    public static string For(IReadOnlyList<DiffLine>? lines)
    {
        if (lines is not { Count: > 0 }) return "";

        var body = new StringBuilder();
        foreach (var line in lines)
        {
            if (body.Length > 0) body.Append('\n');
            body.Append(line.Kind is DiffLineKind.Added or DiffLineKind.Removed or DiffLineKind.Context
                ? line.Marker.Length > 0 ? line.Marker + line.Text : " " + line.Text
                : line.Text);
        }

        var fence = new string('`', FenceLength(body));
        return $"{fence}diff\n{body}\n{fence}";
    }

    /// <summary>A fence longer than the longest run of backticks inside it.</summary>
    /// <remarks>Three is the usual answer and the wrong one here: a patch of a markdown file carries
    /// fences of its own, and one of three inside a fence of three closes it — the rest of the diff
    /// then renders as prose, with what follows it read as markdown somebody else wrote.</remarks>
    private static int FenceLength(StringBuilder body)
    {
        var longest = 0;
        var run = 0;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '`') run++;
            else run = 0;
            if (run > longest) longest = run;
        }

        return Math.Max(3, longest + 1);
    }
}
