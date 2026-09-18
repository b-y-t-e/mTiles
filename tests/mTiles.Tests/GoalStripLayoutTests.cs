using mTiles.Views;
using Xunit;
using static mTiles.Views.GoalStripLayout;

namespace mTiles.Tests;

/// <summary>
/// Which of the Goal tile's strip parts give way first as it narrows.
/// </summary>
/// <remarks>
/// The strip retreats in its own order: mode and effort, then the status (trimmed, then its dot), then
/// the agent. What the status
/// says is <see cref="GoalStatusTests"/>.
/// </remarks>
public class GoalStripLayoutTests
{
    // Agent, mode, effort, status, badges.
    private static readonly RowItemWidths AgentW = new(160, 30);
    private static readonly RowItemWidths ModeW = new(70, 28);
    private static readonly RowItemWidths EffortW = new(60, 28);
    private static readonly RowItemWidths StatusW = new(60, 17);
    private static readonly RowItemWidths BadgesW = new(40, 40);

    private static RowShape Fit(double width) =>
        RowRetreat.For(width, [AgentW, ModeW, EffortW, StatusW, BadgesW], GoalStripLayout.Steps);

    [Fact]
    public void A_wide_strip_changes_nothing() =>
        Assert.Equal([false, false, false, false, false], Fit(390).Compact);

    [Fact]
    public void Mode_and_effort_go_to_their_icons_first()
    {
        var shape = Fit(340);
        Assert.True(shape.Compact[Mode] && shape.Compact[Effort]);
        Assert.False(shape.Compact[Status] || shape.Compact[Agent]);
    }

    [Fact]
    public void Then_the_status_goes_to_its_dot() =>
        Assert.Equal([false, true, true, true, false], Fit(290).Compact);

    [Fact]
    public void Then_the_agent_trims_and_last_of_all_goes_to_its_icon()
    {
        Assert.Equal(220 - 28 - 28 - 17 - 40, Fit(220).MaxWidth[Agent]);
        Assert.Equal([true, true, true, true, false], Fit(150).Compact);
    }

    [Fact]
    public void A_long_status_trims_before_it_goes_to_its_dot()
    {
        var longStatus = new RowItemWidths(200, 17);
        var shape = RowRetreat.For(360, [AgentW, ModeW, EffortW, longStatus, BadgesW], GoalStripLayout.Steps);

        Assert.False(shape.Compact[Status] || shape.Compact[Agent]);
        Assert.Equal(360 - 160 - 28 - 28 - 40, shape.MaxWidth[Status]);
        Assert.Null(shape.MaxWidth[Agent]);
    }
}
