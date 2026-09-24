using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Which of the composer's pickers give up their words, and when.
/// </summary>
/// <remarks>
/// The row is three pickers — model, effort, permission — and it used to wrap the permission onto a line
/// of its own in a narrow tile. What is protected here is the order of retreat: the two whose icons say
/// what they are go first, the model's name trims next, and it goes to its icon only when not even a stub
/// of the name fits.
/// </remarks>
public class ComposerPickerLayoutTests
{
    private static readonly RowItemWidths Model = new(200, 30);
    private static readonly RowItemWidths Effort = new(90, 28);
    private static readonly RowItemWidths Mode = new(85, 28);

    [Theory]
    [InlineData(0, false, "---", null)]    // a row not yet measured is drawn in full
    [InlineData(375, false, "---", null)]  // room for all three
    [InlineData(300, false, "-cc", null)]  // effort and permission go to their icons first, together
    [InlineData(200, false, "-cc", 144.0)] // then the model trims into what is left
    [InlineData(140, false, "ccc", null)]  // below a stub of its name the model goes to its icon too
    [InlineData(200, true, "---", null)]   // a hidden picker costs nothing
    public void The_pickers_give_up_their_words_in_order(
        double width, bool othersHidden, string compact, double? modelMax)
    {
        var shape = othersHidden
            ? RowRetreat.For(width, [Model, RowItemWidths.Hidden, RowItemWidths.Hidden], ComposerPickerLayout.Steps)
            : RowRetreat.For(width, [Model, Effort, Mode], ComposerPickerLayout.Steps);

        Assert.Equal(compact.Select(c => c == 'c').ToArray(), shape.Compact);
        Assert.Equal([modelMax, null, null], shape.MaxWidth);
    }
}
