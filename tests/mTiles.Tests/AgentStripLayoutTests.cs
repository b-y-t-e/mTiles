using mTiles.Views;
using Xunit;
using static mTiles.Views.AgentStripLayout;

namespace mTiles.Tests;

/// <summary>
/// Which parts of the Agent tile's top strip give up their words, and when.
/// </summary>
/// <remarks>
/// The strip used to wrap, putting the status alone on a second line under a half-cut agent name. What
/// is protected here is the order: the conversation's title goes first (the transcript under the strip
/// shows its opening words), the status goes to its dot next, and the agent — said nowhere else on the
/// tile — trims before it goes to its icon, last.
/// </remarks>
public class AgentStripLayoutTests
{
    // Agent, status, stop, conversation, new conversation.
    private static readonly RowItemWidths AgentW = new(180, 30);
    private static readonly RowItemWidths StatusW = new(60, 17);
    private static readonly RowItemWidths StopW = new(26, 26);
    private static readonly RowItemWidths ConversationW = new(170, 30);
    private static readonly RowItemWidths NewW = new(26, 26);

    private static RowShape Fit(double width, bool working = true) =>
        RowRetreat.For(width,
            [AgentW, StatusW, working ? StopW : RowItemWidths.Hidden, ConversationW, NewW],
            AgentStripLayout.Steps);

    [Fact]
    public void A_wide_strip_changes_nothing() =>
        Assert.Equal([false, false, false, false, false], Fit(462).Compact);

    [Fact]
    public void The_conversation_goes_to_its_icon_first()
    {
        var shape = Fit(400);
        Assert.True(shape.Compact[Conversation]);
        Assert.False(shape.Compact[Status]);
        Assert.False(shape.Compact[Agent]);
    }

    [Fact]
    public void Then_the_status_goes_to_its_dot()
    {
        var shape = Fit(300);
        Assert.True(shape.Compact[Conversation]);
        Assert.True(shape.Compact[Status]);
        Assert.False(shape.Compact[Agent]);
        Assert.Null(shape.MaxWidth[Agent]);
    }

    [Fact]
    public void Then_the_agent_trims_into_what_is_left()
    {
        var shape = Fit(220);
        Assert.False(shape.Compact[Agent]);
        Assert.Equal(220 - 17 - 26 - 30 - 26, shape.MaxWidth[Agent]);
    }

    [Fact]
    public void Last_of_all_the_agent_goes_to_its_icon()
    {
        var shape = Fit(150);
        Assert.Equal([true, true, false, true, false], shape.Compact);
        Assert.Null(shape.MaxWidth[Agent]);
    }

    [Fact]
    public void Stop_leaving_at_the_end_of_a_turn_gives_its_room_back() =>
        Assert.False(Fit(436, working: false).Compact[Conversation]);
}
