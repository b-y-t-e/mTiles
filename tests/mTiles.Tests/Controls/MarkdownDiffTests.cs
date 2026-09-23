using mTiles.ViewModels.AgentConversation;
using Notepad.Avalonia.Controls;
using Notepad.Avalonia.Model;
using Xunit;

namespace mTiles.Tests.Controls;

/// <summary>
/// The rule that colours a <c>```diff</c> block. Two questions, both answered from what the author of
/// the document wrote and neither from guessing: is this block a patch, and is this line one that was
/// added, removed, or part of the format.
/// </summary>
public class MarkdownDiffTests
{
    [Theory]
    [InlineData("diff", true)]
    [InlineData("DIFF", true)]
    [InlineData("patch", true)]
    [InlineData("csharp", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_fence_that_says_so_is_a_patch(string? language, bool expected) =>
        Assert.Equal(expected, MarkdownViewer.IsDiffLanguage(language));

    [Theory]
    [InlineData("+added", MarkdownViewer.DiffLineRole.Added)]
    [InlineData("-gone", MarkdownViewer.DiffLineRole.Removed)]
    [InlineData(" kept", MarkdownViewer.DiffLineRole.Context)]
    [InlineData("", MarkdownViewer.DiffLineRole.Context)]
    // The file header wears the same two characters as the content and says the least in the block:
    // read as content it takes the two loudest bands in it.
    [InlineData("+++ b/a.cs", MarkdownViewer.DiffLineRole.Meta)]
    [InlineData("--- a/a.cs", MarkdownViewer.DiffLineRole.Meta)]
    [InlineData("@@ -1,2 +1,2 @@", MarkdownViewer.DiffLineRole.Meta)]
    [InlineData("diff --git a/a b/a", MarkdownViewer.DiffLineRole.Meta)]
    [InlineData("index 8cae149..b69fe9b", MarkdownViewer.DiffLineRole.Meta)]
    // A rename, a copy and a mode change are whole patches with no hunk at all: their header is the
    // change, and every line of it begins with a letter, so nothing but the words tells it from code.
    [InlineData("similarity index 100%", MarkdownViewer.DiffLineRole.Meta)]
    [InlineData("dissimilarity index 40%", MarkdownViewer.DiffLineRole.Meta)]
    [InlineData("rename from a.cs", MarkdownViewer.DiffLineRole.Meta)]
    [InlineData("rename to b.cs", MarkdownViewer.DiffLineRole.Meta)]
    [InlineData("copy from a.cs", MarkdownViewer.DiffLineRole.Meta)]
    [InlineData("copy to b.cs", MarkdownViewer.DiffLineRole.Meta)]
    [InlineData("old mode 100644", MarkdownViewer.DiffLineRole.Meta)]
    [InlineData("new mode 100755", MarkdownViewer.DiffLineRole.Meta)]
    [InlineData("new file mode 100644", MarkdownViewer.DiffLineRole.Meta)]
    [InlineData("deleted file mode 100644", MarkdownViewer.DiffLineRole.Meta)]
    // The two notices git writes instead of a hunk.
    [InlineData("Binary files a/x.png and b/x.png differ", MarkdownViewer.DiffLineRole.Meta)]
    [InlineData("GIT binary patch", MarkdownViewer.DiffLineRole.Meta)]
    internal void A_line_says_what_it_is_with_its_first_character(string line, MarkdownViewer.DiffLineRole expected) =>
        Assert.Equal(expected, MarkdownViewer.DiffRoleOf(line));

    /// <summary>Inside a hunk the same characters are somebody's code: a removed line of two dashes is
    /// written <c>---</c> and an added line of two pluses <c>+++</c>. Read one line at a time both come
    /// out as the file header and lose their band, which is the loudest thing the block says.</summary>
    [Fact]
    internal void Inside_a_hunk_the_header_s_own_characters_are_content() =>
        Assert.Equal(
            [
                MarkdownViewer.DiffLineRole.Meta,
                MarkdownViewer.DiffLineRole.Removed,
                MarkdownViewer.DiffLineRole.Added,
                MarkdownViewer.DiffLineRole.Context,
            ],
            MarkdownViewer.DiffRolesOf(["@@ -5,2 +5,2 @@", "---", "+++", " keep"]));

    /// <summary>The pair that opens the next file ends the hunk before it, which is the one shape a
    /// removed <c>-- a/x</c> cannot be told from.</summary>
    [Fact]
    internal void A_file_header_pair_ends_the_hunk_before_it() =>
        Assert.Equal(
            [
                MarkdownViewer.DiffLineRole.Meta,
                MarkdownViewer.DiffLineRole.Removed,
                MarkdownViewer.DiffLineRole.Meta,
                MarkdownViewer.DiffLineRole.Meta,
                MarkdownViewer.DiffLineRole.Meta,
                MarkdownViewer.DiffLineRole.Added,
            ],
            MarkdownViewer.DiffRolesOf(
                ["@@ -1,1 +1,1 @@", "-gone", "--- a/b.cs", "+++ b/b.cs", "@@ -40,0 +40,1 @@", "+fresh"]));

    /// <summary>Git's no-newline marker belongs to neither side and does not end the hunk it sits
    /// in.</summary>
    [Fact]
    internal void The_no_newline_marker_is_the_format_talking() =>
        Assert.Equal(
            [
                MarkdownViewer.DiffLineRole.Meta,
                MarkdownViewer.DiffLineRole.Removed,
                MarkdownViewer.DiffLineRole.Meta,
                MarkdownViewer.DiffLineRole.Added,
            ],
            MarkdownViewer.DiffRolesOf(
                ["@@ -1,1 +1,1 @@", "-gone", "\\ No newline at end of file", "+added"]));
}

/// <summary>
/// The bridge between the two halves of reading a patch: <see cref="DiffLines"/> says what each line
/// of a patch is, <see cref="DiffMarkdown"/> writes those lines back out as a <c>```diff</c> block,
/// and the viewer reads them again to colour them.
/// </summary>
/// <remarks>Two readings of one format, in two projects that may not depend on each other — the
/// control has to understand a patch on its own, and the tile has to know the kinds to build the
/// markdown. So what is pinned is not that the two are written alike but that they <em>agree</em>:
/// every shape either of them has ever been taught goes through the whole round trip here, and a rule
/// added to one and forgotten in the other fails this rather than arriving on screen as a header drawn
/// like code.</remarks>
public class DiffRoundTripTests
{
    private static MarkdownViewer.DiffLineRole RoleOf(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => MarkdownViewer.DiffLineRole.Added,
        DiffLineKind.Removed => MarkdownViewer.DiffLineRole.Removed,
        DiffLineKind.Context => MarkdownViewer.DiffLineRole.Context,
        _ => MarkdownViewer.DiffLineRole.Meta,
    };

    /// <summary>The block's own lines, with the fence that carries them taken off.</summary>
    private static string[] BodyOf(string markdown)
    {
        var lines = markdown.Split('\n');
        return lines[1..^1];
    }

    [Theory]
    // A patch of whole files, as git writes one.
    [InlineData("diff --git a/a.cs b/a.cs\nindex 8cae149..b69fe9b 100644\n--- a/a.cs\n+++ b/a.cs\n@@ -1,2 +1,2 @@\n-gone\n+fresh\n kept")]
    // The same header in the shape `diff -u` and Subversion write it.
    [InlineData("Index: a.cs\n===================================================================\n--- a.cs\n+++ a.cs\n@@ -1,1 +1,1 @@\n-gone\n+fresh")]
    // A fragment with no header at all, which is what a tool call's detail usually is.
    [InlineData("-gone\n+fresh")]
    // The format's own characters as somebody's code, and the no-newline marker.
    [InlineData("@@ -5,2 +5,2 @@\n---\n+++\n keep\n\\ No newline at end of file")]
    // Two files concatenated, the pair between them being the only separator there is.
    [InlineData("@@ -1,1 +1,1 @@\n-gone\n--- a/b.cs\n+++ b/b.cs\n@@ -40,0 +40,1 @@\n+fresh")]
    // A rename with no hunk under it: the whole patch is the header, so every line of it has to be
    // read as one by both halves or the change comes out drawn like code.
    [InlineData("diff --git a/a.cs b/b.cs\nsimilarity index 100%\nrename from a.cs\nrename to b.cs")]
    // A mode change, which is the same shape one line shorter.
    [InlineData("diff --git a/a.sh b/a.sh\nold mode 100644\nnew mode 100755")]
    // A file git will not quote, whose notice stands where the hunk would.
    [InlineData("diff --git a/x.png b/x.png\nindex 8cae149..b69fe9b 100644\nBinary files a/x.png and b/x.png differ")]
    // A new file, whose mode line is the one thing above the hunk that is not a path.
    [InlineData("diff --git a/n.cs b/n.cs\nnew file mode 100644\nindex 0000000..b69fe9b\n--- /dev/null\n+++ b/n.cs\n@@ -0,0 +1,1 @@\n+fresh")]
    public void The_viewer_reads_back_the_kinds_the_tile_wrote(string patch)
    {
        var parsed = DiffLines.Parse(patch);
        var roles = MarkdownViewer.DiffRolesOf(BodyOf(DiffMarkdown.For(parsed)));

        Assert.Equal(parsed.Select(line => RoleOf(line.Kind)), roles);
    }

    /// <summary>
    /// The patch reaches the viewer as it was written. The body of a fenced block is content and
    /// nothing else, so none of the parser's rules for prose may reach into it: a leading <c>&gt;</c>
    /// is one of the file's own characters and not a quote marker, a tab is the file's indentation and
    /// not four columns of alignment, and a fence inside a longer fence is text.
    /// </summary>
    /// <remarks>Every one of the three showed a line that is not in the file, or lost one that is —
    /// which is the whole of what a patch on screen is for, and what is copied out of it.</remarks>
    [Theory]
    // A markdown file's own quotes, a doctest, a heredoc: all `>` at the head of a line.
    [InlineData("@@ -1,2 +1,2 @@\n > quoted\n->>> old()\n+>>> new()")]
    // A file indented with tabs — Go, a Makefile.
    [InlineData("@@ -1,2 +1,2 @@\n\tif err != nil {\n-\t\treturn err\n+\t\treturn nil")]
    // A patch of a markdown file, fences and a link definition included.
    [InlineData("@@ -1,4 +1,4 @@\n ```\n-code\n+better code\n ```\n [ref]: https://example.com")]
    public void A_patch_survives_the_parser_it_is_drawn_by(string patch)
    {
        var expected = BodyOf(DiffMarkdown.For(DiffLines.Parse(patch)));

        var block = Assert.Single(MarkdownParser.Parse(DiffMarkdown.For(DiffLines.Parse(patch))));
        Assert.Equal(MarkdownBlockType.CodeBlock, block.Type);
        Assert.Equal(string.Join("\n", expected), block.Code);
    }
}
