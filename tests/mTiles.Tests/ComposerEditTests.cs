using Xunit;
using mTiles.Services;
using mTiles.ViewModels;

namespace mTiles.Tests;

/// <summary>The one rule both composers edit their text by: an insertion stays a word of its own, and a
/// removal moves the caret only by what was taken out in front of it.</summary>
public class ComposerEditTests
{
    [Theory]
    [InlineData("check", 5, "check @src/x.cs ", 16)]
    [InlineData("check ", 6, "check @src/x.cs ", 16)]
    [InlineData("", 0, "@src/x.cs ", 10)]
    [InlineData("a b", 2, "a @src/x.cs b", 12)]
    public void An_insertion_is_never_welded_onto_a_neighbouring_word(string text, int caret, string expected, int expectedCaret)
    {
        var edit = new ComposerEdit(text, caret).Insert("@src/x.cs");

        Assert.Equal(expected, edit.Text);
        Assert.Equal(expectedCaret, edit.Caret);
    }

    [Fact]
    public void Removing_a_marker_after_the_caret_leaves_the_caret_where_it_was()
    {
        var edit = new ComposerEdit("abcde [Image #1] xyz", 3).DropImageMarkersExcept([]);

        Assert.Equal("abcde xyz", edit.Text);
        Assert.Equal(3, edit.Caret);
    }

    [Fact]
    public void Removing_a_marker_before_the_caret_moves_it_back_by_the_marker()
    {
        var edit = new ComposerEdit("ab [Image #1] cd", 16).DropImageMarkersExcept([]);

        Assert.Equal("ab cd", edit.Text);
        Assert.Equal(5, edit.Caret);
    }

    [Fact]
    public void Removing_a_file_before_the_caret_moves_it_back_by_the_mention()
    {
        var file = new ComposerFile(3, 5, "@a.cs", "/w/a.cs", IsDirectory: false);

        var edit = new ComposerEdit("ab @a.cs cd", 11).RemoveFile(file);

        Assert.Equal("ab cd", edit.Text);
        Assert.Equal(5, edit.Caret);
    }
}
