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
    private static readonly PickerWidths Model = new(200, 30);
    private static readonly PickerWidths Effort = new(90, 28);
    private static readonly PickerWidths Mode = new(85, 28);

    [Fact]
    public void A_row_not_yet_measured_is_drawn_in_full() =>
        Assert.Equal(ComposerPickerShape.Full, ComposerPickerLayout.For(0, Model, Effort, Mode));

    [Fact]
    public void Room_for_all_three_changes_nothing() =>
        Assert.Equal(ComposerPickerShape.Full, ComposerPickerLayout.For(375, Model, Effort, Mode));

    [Fact]
    public void Effort_and_permission_go_to_their_icons_first_and_together() =>
        Assert.Equal(new ComposerPickerShape(false, true, true, null),
            ComposerPickerLayout.For(300, Model, Effort, Mode));

    [Fact]
    public void Then_the_model_trims_into_what_is_left() =>
        Assert.Equal(new ComposerPickerShape(false, true, true, 144),
            ComposerPickerLayout.For(200, Model, Effort, Mode));

    [Fact]
    public void Below_a_stub_of_its_name_the_model_goes_to_its_icon_too() =>
        Assert.Equal(new ComposerPickerShape(true, true, true, null),
            ComposerPickerLayout.For(140, Model, Effort, Mode));

    [Fact]
    public void A_hidden_picker_costs_nothing() =>
        Assert.Equal(ComposerPickerShape.Full,
            ComposerPickerLayout.For(200, Model, PickerWidths.Hidden, PickerWidths.Hidden));
}
