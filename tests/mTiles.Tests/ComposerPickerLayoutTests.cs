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

    private static RowShape Fit(double width, RowItemWidths effort, RowItemWidths mode) =>
        RowRetreat.For(width, [Model, effort, mode], ComposerPickerLayout.Steps);

    private static RowShape Fit(double width) => Fit(width, Effort, Mode);

    [Fact]
    public void A_row_not_yet_measured_is_drawn_in_full()
    {
        var shape = Fit(0);
        Assert.Equal([false, false, false], shape.Compact);
        Assert.Equal([null, null, null], shape.MaxWidth);
    }

    [Fact]
    public void Room_for_all_three_changes_nothing() =>
        Assert.Equal([false, false, false], Fit(375).Compact);

    [Fact]
    public void Effort_and_permission_go_to_their_icons_first_and_together()
    {
        var shape = Fit(300);
        Assert.Equal([false, true, true], shape.Compact);
        Assert.Null(shape.MaxWidth[ComposerPickerLayout.Model]);
    }

    [Fact]
    public void Then_the_model_trims_into_what_is_left()
    {
        var shape = Fit(200);
        Assert.Equal([false, true, true], shape.Compact);
        Assert.Equal(144, shape.MaxWidth[ComposerPickerLayout.Model]);
    }

    [Fact]
    public void Below_a_stub_of_its_name_the_model_goes_to_its_icon_too()
    {
        var shape = Fit(140);
        Assert.Equal([true, true, true], shape.Compact);
        Assert.Null(shape.MaxWidth[ComposerPickerLayout.Model]);
    }

    [Fact]
    public void A_hidden_picker_costs_nothing() =>
        Assert.Equal([false, false, false], Fit(200, RowItemWidths.Hidden, RowItemWidths.Hidden).Compact);
}
