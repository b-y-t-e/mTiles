using Xunit;
using mTiles.ViewModels.AgentConversation;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// What a line of a patch is, and what the patch becomes when it is handed to the viewer. Pure, and
/// worth pinning because the one thing that cannot be seen to be right in a screenshot is where a hunk
/// begins and ends: inside one, the characters the format uses for its own headers are somebody's code.
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
    public void The_marker_column_is_taken_out_of_the_text()
    {
        var lines = DiffLines.ParseFilePatch(Diff);

        Assert.Equal(("keep", ""), (lines[1].Text, lines[1].Marker));
        Assert.Equal(("gone", "-"), (lines[2].Text, lines[2].Marker));
        Assert.Equal(("added", "+"), (lines[3].Text, lines[3].Marker));
    }

    [Fact]
    public void An_empty_added_line_keeps_its_place()
    {
        var lines = DiffLines.ParseFilePatch("@@ -1 +1 @@\n+");

        Assert.Equal(string.Empty, lines[1].Text);
        Assert.Equal(DiffLineKind.Added, lines[1].Kind);
    }

    /// <summary>A removed "--" is written "---", where the patch's own file header lives.</summary>
    [Fact]
    public void A_removed_line_of_two_dashes_is_a_removal_and_not_a_header()
    {
        var lines = DiffLines.Parse("@@ -5,2 +5,1 @@\n---\n keep");

        Assert.Equal(DiffLineKind.Removed, lines[1].Kind);
        Assert.Equal("--", lines[1].Text);
        Assert.Equal(DiffLineKind.Context, lines[2].Kind);
    }

    [Fact]
    public void An_added_line_of_two_pluses_is_an_addition_and_not_a_header()
    {
        var lines = DiffLines.Parse("@@ -5,1 +5,2 @@\n+++\n keep");

        Assert.Equal(DiffLineKind.Added, lines[1].Kind);
        Assert.Equal("++", lines[1].Text);
    }

    /// <summary>The no-newline marker belongs to neither side, and it does not end the hunk it sits
    /// in.</summary>
    [Fact]
    public void The_no_newline_marker_is_a_line_of_the_format()
    {
        var lines = DiffLines.Parse("@@ -1,2 +1,2 @@\n-gone\n\\ No newline at end of file\n+added\n keep");

        Assert.Equal(DiffLineKind.Header, lines[2].Kind);
        Assert.Equal(DiffLineKind.Added, lines[3].Kind);
        Assert.Equal(DiffLineKind.Context, lines[4].Kind);
    }

    /// <summary>A patch of two files: the second file's own headers end the first one's hunk, so they
    /// are headers again rather than lines of the first file.</summary>
    [Fact]
    public void A_second_file_in_one_patch_opens_with_headers_again()
    {
        var lines = DiffLines.Parse(string.Join('\n',
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
        Assert.Equal(DiffLineKind.Added, lines[10].Kind);
    }

    /// <summary>Several patches concatenated with nothing but their own <c>---</c>/<c>+++</c> pair
    /// between them — a multi-edit tool call — open a new file each time, so the pair is a header
    /// rather than a removed and an added line.</summary>
    [Fact]
    public void A_file_header_pair_ends_the_hunk_before_it()
    {
        var lines = DiffLines.Parse(string.Join('\n',
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
        Assert.Equal(DiffLineKind.Added, lines[8].Kind);
    }

    /// <summary>
    /// The patch's own header names the file, and the file is the row this patch is drawn under. Four
    /// lines of machinery above two lines of content is what it came to on a small change.
    /// </summary>
    [Fact]
    public void A_file_s_patch_is_drawn_without_the_lines_that_name_the_file()
    {
        Assert.DoesNotContain(DiffLines.ParseFilePatch(Diff), l => l.Kind == DiffLineKind.Header);

        // The fragment parse keeps them: nothing above it says which file it is.
        Assert.Contains(DiffLines.Parse(Diff), l => l.Kind == DiffLineKind.Header);
    }

    /// <summary>
    /// Git's extended header lines — the mode a new file was created with, the two names of a rename —
    /// are the file's header too, and left in they are drawn as unchanged lines of its content.
    /// </summary>
    [Fact]
    public void A_file_s_patch_drops_the_extended_header_a_creation_or_a_rename_adds()
    {
        var created = DiffLines.ParseFilePatch(
            "diff --git a/x b/x\nnew file mode 100644\nindex 0000000..e69de29\n--- /dev/null\n+++ b/x\n@@ -0,0 +1 @@\n+hello");

        Assert.Equal([DiffLineKind.Hunk, DiffLineKind.Added], created.Select(l => l.Kind));

        var renamed = DiffLines.ParseFilePatch(
            "diff --git a/a b/b\nsimilarity index 90%\nrename from a\nrename to b\nindex 1111111..2222222 100644\n@@ -1 +1 @@\n-old\n+new");

        // Everything but the one line saying what the file used to be called: the row above the patch
        // draws the new path only, so dropped it too there is nowhere the old name is said at all.
        Assert.Equal(
            ["rename from a", "@@ -1 +1 @@", "old", "new"],
            renamed.Select(l => l.Text));
    }

    /// <summary>A copy's source is kept for the same reason a rename's is.</summary>
    [Fact]
    public void A_file_s_patch_keeps_the_name_a_copy_came_from()
    {
        var copied = DiffLines.ParseFilePatch(
            "diff --git a/a b/b\nsimilarity index 90%\ncopy from a\ncopy to b\nindex 1111111..2222222 100644\n@@ -1 +1 @@\n-old\n+new");

        Assert.Equal(["copy from a", "@@ -1 +1 @@", "old", "new"], copied.Select(l => l.Text));
    }

    /// <summary>A patch with no hunk under the header is one whose header is the whole change — a
    /// rename, a copy, a mode change — so nothing is dropped and the row has something to show.</summary>
    [Fact]
    public void A_patch_that_is_only_a_header_keeps_it()
    {
        var renamed = DiffLines.ParseFilePatch(
            "diff --git a/a b/b\nsimilarity index 100%\nrename from a\nrename to b");

        Assert.Equal(
            ["diff --git a/a b/b", "similarity index 100%", "rename from a", "rename to b"],
            renamed.Select(l => l.Text));

        var chmod = DiffLines.ParseFilePatch(
            "diff --git a/s b/s\nold mode 100644\nnew mode 100755");

        Assert.Equal(["diff --git a/s b/s", "old mode 100644", "new mode 100755"], chmod.Select(l => l.Text));

        var copied = DiffLines.ParseFilePatch(
            "diff --git a/a b/b\nsimilarity index 100%\ncopy from a\ncopy to b");

        Assert.Equal(4, copied.Count);
    }

    /// <summary>What git writes in place of a hunk is the only content there is, so it stays.</summary>
    [Fact]
    public void A_binary_change_keeps_the_one_line_that_says_so()
    {
        var lines = DiffLines.ParseFilePatch("diff --git a/x b/x\nindex 111..222 100644\nBinary files a/x and b/x differ");

        Assert.Equal(["Binary files a/x and b/x differ"], lines.Select(l => l.Text));
    }

    /// <summary>Inside a hunk, the one header there is is git's no-newline marker — the only line saying
    /// the change is to the file's last character — so dropping the file's own header leaves it.</summary>
    [Fact]
    public void A_file_s_patch_keeps_the_no_newline_marker()
    {
        var lines = DiffLines.ParseFilePatch(
            "diff --git a/f b/f\n--- a/f\n+++ b/f\n@@ -1,1 +1,1 @@\n-gone\n\\ No newline at end of file\n+added");

        Assert.Equal(
            [DiffLineKind.Hunk, DiffLineKind.Removed, DiffLineKind.Header, DiffLineKind.Added],
            lines.Select(l => l.Kind));
        Assert.Equal("\\ No newline at end of file", lines[2].Text);
    }

    /// <summary>
    /// The patch is handed to the viewer that draws every message in the tile, so it is markdown — and
    /// a fence of three closes on the first fence of three inside it, which a patch of a markdown file
    /// carries as content.
    /// </summary>
    [Fact]
    public void A_patch_carrying_a_fence_is_wrapped_in_a_longer_one()
    {
        var markdown = DiffMarkdown.For(DiffLines.ParseFilePatch("@@ -1,1 +1,1 @@\n+```diff"));

        Assert.StartsWith("````diff\n", markdown);
        Assert.EndsWith("\n````", markdown);
        Assert.Contains("+```diff", markdown);
    }

    [Fact]
    public void An_ordinary_patch_takes_the_usual_fence_and_keeps_its_markers()
    {
        var markdown = DiffMarkdown.For(DiffLines.ParseFilePatch(Diff));

        Assert.Equal("""
            ```diff
            @@ -10,3 +12,4 @@ class A
             keep
            -gone
            +added
            +also
            ```
            """.ReplaceLineEndings("\n"), markdown);
    }

    /// <summary>The hunk headers are what the viewer tells a removed <c>--</c> by, so the markdown
    /// keeps them even though the file header goes.</summary>
    [Fact]
    public void The_markdown_keeps_the_hunk_header_that_says_where_the_code_begins()
    {
        var markdown = DiffMarkdown.For(DiffLines.ParseFilePatch("@@ -5,2 +5,1 @@\n---\n keep"));

        Assert.Equal("""
            ```diff
            @@ -5,2 +5,1 @@
            ---
             keep
            ```
            """.ReplaceLineEndings("\n"), markdown);
    }

    [Fact]
    public void Nothing_to_draw_is_no_block_at_all()
    {
        Assert.Equal("", DiffMarkdown.For(DiffLines.ParseFilePatch("")));
    }
}
