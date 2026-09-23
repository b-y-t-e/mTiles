using Xunit;
using mTiles.Views;

namespace mTiles.Tests;

/// <summary>What Alt presses on a dialog whose buttons are labelled by their caller.</summary>
public class AccessKeyLabelTests
{
    [Theory]
    // The ordinary pair, and the one everybody expects: Alt+Y and Alt+N.
    [InlineData("Yes", null, "_Yes")]
    [InlineData("No", 'y', "_No")]
    // A caller's own words, which is why the letter is not hard-coded.
    [InlineData("Discard", null, "_Discard")]
    [InlineData("OK", null, "_OK")]
    // The clash: two buttons offering the same key is Avalonia cycling between them rather than
    // pressing either, so the second takes the next free letter.
    [InlineData("Don't", 'd', "D_on't")]
    // Nothing left to take. A button without a mnemonic is still a button Tab reaches.
    [InlineData("Dd", 'd', "Dd")]
    // A leading non-letter is skipped rather than marked: "_…" before a space marks the space.
    [InlineData("  Yes", null, "  _Yes")]
    // An underscore in the label is a word the caller wrote, not a mark.
    [InlineData("A_B", null, "_A__B")]
    [InlineData("_x", null, "___x")]
    public void The_key_is_the_first_free_letter(string label, char? taken, string expected) =>
        Assert.Equal(expected, AccessKeyLabel.Mark(label, taken));

    [Theory]
    [InlineData("Yes", 'y')]
    [InlineData("  Discard", 'd')]
    [InlineData("...", null)]
    public void The_key_reported_is_the_key_marked(string label, char? expected) =>
        Assert.Equal(expected, AccessKeyLabel.KeyOf(label));
}
