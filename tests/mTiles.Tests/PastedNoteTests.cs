using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// A paste too long for a composer becomes a note beside the workspace's other attachments, named for what
/// it is — see <see cref="PastedNote"/>.
/// </summary>
public class PastedNoteTests
{
    [Theory]
    // Two measures because they fail apart: a diff is forty short lines, a wall of prose is one long one.
    [InlineData("a short remark", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void What_counts_as_long(string? text, bool expected) => Assert.Equal(expected, PastedNote.IsLong(text));

    [Fact]
    public void A_wall_of_words_and_a_wall_of_lines_are_both_long()
    {
        Assert.True(PastedNote.IsLong(new string('x', PastedNote.LongEnoughCharacters + 1)));
        Assert.False(PastedNote.IsLong(new string('x', PastedNote.LongEnoughCharacters)));

        Assert.True(PastedNote.IsLong(string.Join('\n', Enumerable.Repeat("x", PastedNote.LongEnoughLines + 1))));
        Assert.False(PastedNote.IsLong(string.Join('\n', Enumerable.Repeat("x", PastedNote.LongEnoughLines))));
    }

    [Fact]
    public void The_word_count_is_the_files_own_name_and_is_read_back_off_it()
    {
        Assert.Equal(4, PastedNote.WordsIn(" see  these\nnotes\tplease "));
        Assert.Equal("notes-4-words.md", PastedNote.FileNameFor(4));
        Assert.Equal("4 words", PastedNote.LabelFor("20260921-143022-notes-4-words.md"));
        Assert.Equal("4 words", PastedNote.LabelFor("20260921-143022-2-notes-4-words.md"));
        Assert.Null(PastedNote.LabelFor("Cart.cs"));
        Assert.Null(PastedNote.LabelFor("notes.md"));
    }

    [Fact]
    public async Task A_note_lands_beside_the_other_attachments_and_the_chip_says_how_long_it_was()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"mtiles-note-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            const string text = "see these notes";
            var path = await PastedNote.WriteAsync(text, workspace);

            Assert.NotNull(path);
            Assert.Equal(text, File.ReadAllText(path!));
            Assert.True(AttachmentStore.IsInside(path!, AttachmentStore.CopiesDirectory(workspace)));

            // Something to read, never part of the work's scope - the rule the Goal tile's filter asks.
            var mention = Path.GetRelativePath(workspace, path!).Replace(Path.DirectorySeparatorChar, '/');
            Assert.True(AttachmentStore.IsContextOnly(mention, workspace));

            var chip = new ComposerFile(0, 0, "@" + mention, path!, IsDirectory: false);
            Assert.True(chip.IsPastedNote);
            Assert.Equal("3 words", chip.Label);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>Nowhere to write it is a null, so the paste goes into the box rather than being lost.</summary>
    [Fact]
    public async Task A_tile_with_no_workspace_takes_no_note() =>
        Assert.Null(await PastedNote.WriteAsync("anything", ""));
}
