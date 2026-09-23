using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

public class ComposerHistoryTests
{
    [Fact]
    public void Walks_back_and_forward_and_gives_the_draft_back()
    {
        var history = new ComposerHistory(() => ["one", "two", "three"]);

        Assert.Equal("three", history.Older("draft"));
        Assert.Equal("two", history.Older("three"));
        Assert.Equal("one", history.Older("two"));
        Assert.Null(history.Older("one"));
        Assert.Equal("two", history.Newer());
        Assert.Equal("three", history.Newer());
        Assert.Equal("draft", history.Newer());
        Assert.False(history.IsWalking);
        Assert.Null(history.Newer());
    }

    [Fact]
    public void An_edit_starts_the_next_walk_from_the_newest()
    {
        var history = new ComposerHistory(() => ["one", "two"]);
        history.Older("");
        history.Older("two");
        history.Reset();

        Assert.Equal("two", history.Older("edited"));
        Assert.Equal("edited", history.Newer());
    }

    [Fact]
    public void A_message_picked_from_the_list_keeps_the_draft()
    {
        var history = new ComposerHistory(() => ["one", "two", "three"]);

        Assert.Equal("one", history.JumpTo(0, "half-written"));
        Assert.Equal("two", history.Newer());
        Assert.Equal("three", history.Newer());
        Assert.Equal("half-written", history.Newer());
    }

    [Fact]
    public void A_pick_during_a_walk_keeps_the_draft_the_walk_began_with()
    {
        var history = new ComposerHistory(() => ["one", "two"]);
        history.Older("draft");

        Assert.Equal("one", history.JumpTo(0, "two"));
        Assert.Equal("two", history.Newer());
        Assert.Equal("draft", history.Newer());
    }

    [Fact]
    public void A_pick_outside_the_list_changes_nothing()
    {
        var history = new ComposerHistory(() => ["one"]);

        Assert.Null(history.JumpTo(1, "draft"));
        Assert.False(history.IsWalking);
    }

    [Fact]
    public void Repeats_in_a_row_and_blanks_collapse_but_earlier_sends_stay()
    {
        var collapsed = ComposerHistory.CollapseRepeats(["continue", "continue", " ", "fix it", "", "continue"]);

        Assert.Equal(["continue", "fix it", "continue"], collapsed);
    }
}
