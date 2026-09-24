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

    // Badges beside the status.
    private static readonly RowItemWidths BadgesW = new(40, 40);

    /// <summary>"c" compact, "-" in full, one letter per part.</summary>
    private static bool[] Compact(string shape) => shape.Select(c => c == 'c').ToArray();

    [Theory]
    [InlineData(300, "---", null)]  // wide: nothing changes
    [InlineData(260, "-cc", null)]  // mode and effort go to their icons first
    [InlineData(180, "-cc", 180 - 28 - 28.0)]  // then the agent trims into what is left
    [InlineData(100, "ccc", null)]  // and last of all goes to its icon
    public void The_strip_gives_way_mode_and_effort_first_then_the_agent(
        double width, string compact, double? agentMax)
    {
        var shape = RowRetreat.For(width, [AgentW, ModeW, EffortW], GoalStripLayout.Steps);

        Assert.Equal(Compact(compact), shape.Compact);
        Assert.Equal(agentMax, shape.MaxWidth[Agent]);
    }

    [Theory]
    [InlineData(200, 60, "--", null)]         // wide: nothing changes
    [InlineData(200, 300, "--", 200 - 40.0)]  // a long status trims before it goes to its dot
    [InlineData(90, 60, "c-", null)]          // no room for the words: the dot and the badges stay
    public void The_status_bar_trims_the_status_before_it_goes_to_its_dot(
        double width, double statusWidth, string compact, double? statusMax)
    {
        var shape = RowRetreat.For(width, [new RowItemWidths(statusWidth, 17), BadgesW],
            GoalStatusBarLayout.Steps);

        Assert.Equal(Compact(compact), shape.Compact);
        Assert.Equal(statusMax, shape.MaxWidth[GoalStatusBarLayout.Status]);
    }
}
