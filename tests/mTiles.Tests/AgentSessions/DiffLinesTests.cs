using Xunit;
using mTiles.ViewModels.AgentConversation;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// The gutter a diff row is drawn with. Pure, and worth pinning because the two counters are the one
/// part of it that cannot be seen to be right by looking at a screenshot: a removed line is numbered in
/// the file it left and everything else in the file as it now stands.
/// </summary>
public class DiffLinesTests
{
    private const string Diff = """
        diff --git a/a.cs b/a.cs
        @@ -10,3 +12,4 @@ class A
         keep
        -gone
        +added
        +also
        """;

    [Fact]
    public void A_hunk_header_sets_both_counters()
    {
        var lines = DiffLines.ParseFilePatch(Diff);

        Assert.Equal(DiffLineKind.Header, lines[0].Kind);
        Assert.Null(lines[0].Number);
        Assert.Null(lines[1].Number);                       // the hunk header itself

        Assert.Equal(12, lines[2].Number);                  // context, numbered in the new file
        Assert.Equal(11, lines[3].Number);                  // removed, numbered in the old one
        Assert.Equal(13, lines[4].Number);
        Assert.Equal(14, lines[5].Number);
    }

    [Fact]
    public void The_marker_column_is_taken_out_of_the_text()
    {
        var lines = DiffLines.ParseFilePatch(Diff);

        Assert.Equal(("keep", ""), (lines[2].Text, lines[2].Marker));
        Assert.Equal(("gone", "-"), (lines[3].Text, lines[3].Marker));
        Assert.Equal(("added", "+"), (lines[4].Text, lines[4].Marker));
    }

    [Fact]
    public void A_header_this_cannot_read_numbers_from_one_rather_than_throwing()
    {
        var lines = DiffLines.ParseFilePatch("@@ what @@\n+first");

        Assert.Equal(1, lines[1].Number);
    }

    [Fact]
    public void An_empty_added_line_keeps_its_place()
    {
        var lines = DiffLines.ParseFilePatch("@@ -1 +1 @@\n+");

        Assert.Equal(string.Empty, lines[1].Text);
        Assert.Equal(1, lines[1].Number);
    }
    [Fact]
    public void The_function_context_after_the_closing_markers_is_not_read_as_a_line_number()
    {
        var lines = DiffLines.ParseFilePatch("@@ -10,3 +12,4 @@ x = -12\n-gone\n+added");

        Assert.Equal(10, lines[1].Number);
        Assert.Equal(12, lines[2].Number);
    }

    /// <summary>A removed "--" is written "---", where the patch's own file header lives.</summary>
    [Fact]
    public void A_removed_line_of_two_dashes_is_a_removal_and_not_a_header()
    {
        var lines = DiffLines.ParseFilePatch("@@ -5,2 +5,1 @@\n---\n keep");

        Assert.Equal(DiffLineKind.Removed, lines[1].Kind);
        Assert.Equal(5, lines[1].Number);
        Assert.Equal("--", lines[1].Text);
        Assert.Equal(5, lines[2].Number);   // context, numbered in the new file
    }

    [Fact]
    public void An_added_line_of_two_pluses_is_an_addition_and_not_a_header()
    {
        var lines = DiffLines.ParseFilePatch("@@ -5,1 +5,2 @@\n+++\n keep");

        Assert.Equal(DiffLineKind.Added, lines[1].Kind);
        Assert.Equal("++", lines[1].Text);
    }

    /// <summary>The no-newline marker belongs to neither side, so it takes no number and moves neither
    /// counter — and it does not end the hunk it sits in.</summary>
    [Fact]
    public void The_no_newline_marker_is_not_counted_as_a_line()
    {
        var lines = DiffLines.ParseFilePatch("@@ -1,2 +1,2 @@\n-gone\n\\ No newline at end of file\n+added\n keep");

        Assert.Equal(DiffLineKind.Header, lines[2].Kind);
        Assert.Null(lines[2].Number);
        Assert.Equal(1, lines[3].Number);
        Assert.Equal(2, lines[4].Number);
    }

    /// <summary>A patch of two files: the second file's own headers end the first one's hunk, so they
    /// are headers again and the second file's lines are numbered from its own <c>@@</c>.</summary>
    [Fact]
    public void A_second_file_in_one_patch_starts_its_own_numbering()
    {
        var lines = DiffLines.ParseFilePatch(string.Join('\n',
            "diff --git a/a.cs b/a.cs",
            "--- a/a.cs",
            "+++ b/a.cs",
            "@@ -1,1 +1,1 @@",
            "-gone",
            "+added",
            "diff --git a/b.cs b/b.cs",
            "--- /dev/null",
            "+++ b/b.cs",
            "@@ -0,0 +40,1 @@",
            "+fresh"));

        Assert.Equal(DiffLineKind.Header, lines[6].Kind);   // diff --git of the second file
        Assert.Equal(DiffLineKind.Header, lines[7].Kind);   // --- /dev/null
        Assert.Equal(DiffLineKind.Header, lines[8].Kind);   // +++ b/b.cs
        Assert.All(lines.Skip(6).Take(3), l => Assert.Null(l.Number));
        Assert.Equal(DiffLineKind.Added, lines[10].Kind);
        Assert.Equal(40, lines[10].Number);
    }

    /// <summary>Several patches concatenated with nothing but their own <c>---</c>/<c>+++</c> pair
    /// between them — a multi-edit tool call — open a new file each time, so the pair is a header
    /// rather than a removed and an added line.</summary>
    [Fact]
    public void A_file_header_pair_ends_the_hunk_before_it()
    {
        var lines = DiffLines.ParseFilePatch(string.Join('\n',
            "--- a/a.cs",
            "+++ b/a.cs",
            "@@ -1,1 +1,1 @@",
            "-gone",
            "+added",
            "--- a/b.cs",
            "+++ b/b.cs",
            "@@ -40,0 +40,1 @@",
            "+fresh"));

        Assert.Equal(DiffLineKind.Header, lines[5].Kind);
        Assert.Equal(DiffLineKind.Header, lines[6].Kind);
        Assert.All(lines.Skip(5).Take(2), line => Assert.Null(line.Number));
        Assert.Equal(40, lines[8].Number);
    }

    /// <summary>A fragment — an Edit tool's old and new text — is numbered from its own first line,
    /// which is not where it sits in the file, so it is drawn with no numbers at all.</summary>
    [Fact]
    public void A_patch_nobody_can_number_is_read_without_numbers()
    {
        var lines = DiffLines.Parse(Diff);

        Assert.All(lines, line => Assert.Null(line.Number));
        Assert.Equal(DiffLineKind.Removed, lines[3].Kind);
        Assert.Equal("gone", lines[3].Text);
    }
}
