using mTiles.Views;
using Xunit;
using static mTiles.Views.GoalStripLayout;

namespace mTiles.Tests;

/// <summary>
/// Which of the Goal tile's strip parts give way first as it narrows, and how the status bar under the
/// composer does.
/// </summary>
/// <remarks>
/// The strip retreats in its own order: mode and effort, then the agent. The status lives in the bar
/// under the composer and trims before it goes to its dot. What the status says is
/// <see cref="GoalStatusTests"/>.
/// </remarks>
public class GoalStripLayoutTests
{
    // Agent, mode, effort.
    private static readonly RowItemWidths AgentW = new(160, 30);
    private static readonly RowItemWidths ModeW = new(70, 28);
    private static readonly RowItemWidths EffortW = new(60, 28);

    // Status, badges.
    private static readonly RowItemWidths StatusW = new(60, 17);
    private static readonly RowItemWidths BadgesW = new(40, 40);

    private static RowShape Fit(double width) =>
        RowRetreat.For(width, [AgentW, ModeW, EffortW], GoalStripLayout.Steps);

    private static RowShape FitBar(double width, RowItemWidths status) =>
        RowRetreat.For(width, [status, BadgesW], GoalStatusBarLayout.Steps);

    [Fact]
    public void A_wide_strip_changes_nothing() =>
        Assert.Equal([false, false, false], Fit(300).Compact);

    [Fact]
    public void Mode_and_effort_go_to_their_icons_first()
    {
        var shape = Fit(260);
        Assert.True(shape.Compact[Mode] && shape.Compact[Effort]);
        Assert.False(shape.Compact[Agent]);
    }

    [Fact]
    public void Then_the_agent_trims_and_last_of_all_goes_to_its_icon()
    {
        Assert.Equal(180 - 28 - 28, Fit(180).MaxWidth[Agent]);
        Assert.Equal([true, true, true], Fit(100).Compact);
    }

    [Fact]
    public void A_wide_bar_changes_nothing() =>
        Assert.Equal([false, false], FitBar(200, StatusW).Compact);

    [Fact]
    public void A_long_status_trims_before_it_goes_to_its_dot()
    {
        var shape = FitBar(200, new RowItemWidths(300, 17));
        Assert.False(shape.Compact[GoalStatusBarLayout.Status]);
        Assert.Equal(200 - 40, shape.MaxWidth[GoalStatusBarLayout.Status]);
    }

    [Fact]
    public void A_bar_with_no_room_for_the_words_keeps_the_dot_and_the_badges() =>
        Assert.Equal([true, false], FitBar(90, StatusW).Compact);
}
