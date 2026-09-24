using mTiles.Services;
using mTiles.Services.Agents.SessionLogs;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>The brief a terminal agent tile hands over, and when the line pointing at it is typed.</summary>
public class TerminalHandoverTests
{
    [Fact]
    public void Nothing_the_user_said_is_nothing_to_hand_over() =>
        Assert.Null(TerminalHandover.Write("Claude", [new TranscriptTurn(false, "Hello, how can I help?")]));

    [Fact]
    public void The_brief_carries_the_first_request_and_the_conversation_after_it()
    {
        var brief = TerminalHandover.Write("Claude",
        [
            new TranscriptTurn(true, "Make the sidebar collapsible."),
            new TranscriptTurn(false, "Done in Sidebar.axaml."),
            new TranscriptTurn(true, "Now remember its state."),
        ])!;

        Assert.Contains("## What was asked for\n\nMake the sidebar collapsible.", brief);
        Assert.Contains("**Claude:**\n\nDone in Sidebar.axaml.", brief);
        Assert.Contains("**User:**\n\nNow remember its state.", brief);
        Assert.DoesNotContain("left out", brief);
    }

    /// <remarks>A brief that quietly loses the middle reads as a complete account of a smaller task.</remarks>
    [Fact]
    public void What_does_not_fit_is_dropped_oldest_first_and_counted()
    {
        var long_ = new string('x', TerminalHandover.MessageCap);
        var turns = new List<TranscriptTurn> { new(true, "The goal.") };
        for (var i = 0; i < 20; i++) turns.Add(new TranscriptTurn(i % 2 == 0, $"{i} {long_}"));
        turns.Add(new TranscriptTurn(false, "The last word."));

        var brief = TerminalHandover.Write("Codex", turns)!;

        Assert.Contains("The goal.", brief);
        Assert.Contains("The last word.", brief);
        Assert.DoesNotContain("0 xxx", brief);
        Assert.Matches(@"_\d+ earlier messages were left out", brief);
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_is_typed_before_a_command_has_run_and_settled()
    {
        var delivery = new HandoverDelivery("Read it.");
        Assert.Null(delivery.Tick(T0));

        delivery.OnCommandStarting(ownCommand: true, T0);
        delivery.OnOutput(T0.AddSeconds(3));
        Assert.Null(delivery.Tick(T0.AddSeconds(3.5)));
        Assert.Null(delivery.Tick(T0.AddSeconds(4)));
        Assert.Equal("Read it.", delivery.Tick(T0.AddSeconds(5)));
        Assert.Null(delivery.Tick(T0.AddSeconds(6)));
    }

    [Fact]
    public void A_command_that_never_goes_quiet_is_typed_at_in_the_end()
    {
        var delivery = new HandoverDelivery("Read it.");
        delivery.OnCommandStarting(ownCommand: true, T0);
        for (var s = 1; s < 20; s++) delivery.OnOutput(T0.AddSeconds(s));

        Assert.Null(delivery.Tick(T0.AddSeconds(19)));
        Assert.Equal("Read it.", delivery.Tick(T0.AddSeconds(21)));
    }

    /// <remarks>The first link of a chain is often a resume that fails, and what was typed into it
    /// reached nobody.</remarks>
    [Fact]
    public void A_command_that_ends_straight_after_is_typed_at_again_in_the_next()
    {
        var delivery = new HandoverDelivery("Read it.");
        delivery.OnCommandStarting(ownCommand: true, T0);
        Assert.NotNull(delivery.Tick(T0.AddSeconds(20)));

        delivery.OnCommandStarting(ownCommand: true, T0.AddSeconds(25));
        Assert.False(delivery.IsFinished);
        Assert.Equal("Read it.", delivery.Tick(T0.AddSeconds(45)));

        Assert.Null(delivery.Tick(T0.AddSeconds(80)));
        Assert.True(delivery.IsFinished);
        Assert.False(delivery.Abandoned);
    }

    [Fact]
    public void The_plain_shell_at_the_end_of_the_chain_is_never_typed_at()
    {
        var delivery = new HandoverDelivery("Read it.");
        delivery.OnCommandStarting(ownCommand: false, T0);

        Assert.True(delivery.Abandoned);
        Assert.Null(delivery.Tick(T0.AddSeconds(30)));
    }

    [Fact]
    public void A_launch_that_never_starts_is_given_up_on()
    {
        var delivery = new HandoverDelivery("Read it.");
        Assert.Null(delivery.Tick(T0));
        Assert.False(delivery.IsFinished);

        Assert.Null(delivery.Tick(T0 + HandoverDelivery.NoLaunchWithin + TimeSpan.FromSeconds(1)));
        Assert.True(delivery.IsFinished);
        Assert.True(delivery.Abandoned);
    }

    [Fact]
    public void Two_tiles_switched_in_the_same_second_get_two_briefs()
    {
        var now = new DateTime(2026, 9, 23, 12, 0, 0);
        Assert.NotEqual(TerminalHandover.FileName(now, "claude", "tile-a"),
            TerminalHandover.FileName(now, "claude", "tile-b"));
    }

    [Fact]
    public void A_hand_edited_tile_id_cannot_leave_the_directory_or_end_the_typed_line()
    {
        var name = TerminalHandover.FileName(new DateTime(2026, 9, 24), "claude", "../x\nrm");
        Assert.DoesNotContain("..", name);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain("\n", name);
    }

    [Fact]
    public async Task Saving_keeps_the_directory_ignored_and_only_the_newest_briefs()
    {
        using var directory = new TempDirectory();
        for (var i = 0; i < TerminalHandover.Kept + 2; i++)
            await TerminalHandover.SaveAsync(directory.Path, $"2026092{i}-claude-t.md", $"brief {i}");

        var handover = TerminalHandover.DirectoryIn(directory.Path);
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(handover, ".gitignore")));
        var kept = Directory.GetFiles(handover, "*.md").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(TerminalHandover.Kept, kept.Count);
        Assert.DoesNotContain("20260920-claude-t.md", kept);
        Assert.Contains($"2026092{TerminalHandover.Kept + 1}-claude-t.md", kept);
    }
}
