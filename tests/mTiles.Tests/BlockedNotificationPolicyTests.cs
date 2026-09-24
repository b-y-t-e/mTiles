using mTiles.Models;
using mTiles.Services.Notifications;
using Xunit;

namespace mTiles.Tests;

/// <summary>When a tile waiting for the user is worth a desktop notification — see
/// <see cref="BlockedNotificationPolicy"/>.</summary>
public class BlockedNotificationPolicyTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    // Off screen, not yet announced: the one case the feature exists for.
    [InlineData(true, false, false, true)]
    // Switched off.
    [InlineData(false, false, false, false)]
    // Already in front of the user: the header's mark says it.
    [InlineData(true, true, false, false)]
    // The same question, however long it has been waiting, is announced once.
    [InlineData(true, false, true, false)]
    public void Decides(bool enabled, bool onScreen, bool questionAlreadyAnnounced, bool expected) =>
        Assert.Equal(expected, BlockedNotificationPolicy.ShouldNotify(enabled, onScreen, questionAlreadyAnnounced));

    [Theory]
    // Nothing has ended since the last question: a stale reading coming back.
    [InlineData(null, false)]
    // A prompt redrawn while it is answered.
    [InlineData(1.0, false)]
    // Answered, and then asked again.
    [InlineData(5.0, true)]
    public void A_question_must_have_been_over_for_a_moment(double? secondsSinceEnded, bool expected)
    {
        DateTime? ended = secondsSinceEnded is { } s ? Now.AddSeconds(-s) : null;
        Assert.Equal(expected, BlockedNotificationPolicy.IsNewQuestion(ended, Now));
    }

    private sealed class Script
    {
        private readonly BlockedQuestionTracker _tracker = new();
        public DateTime Clock = Now;

        public bool Report(TileActivity activity, bool onScreen = false, bool enabled = true) =>
            _tracker.Observe(activity, onScreen, enabled, Clock);

        public void Wait(double seconds) => Clock = Clock.AddSeconds(seconds);
    }

    [Fact]
    public void A_question_is_announced_once_however_often_its_reading_goes_stale()
    {
        var tile = new Script();
        Assert.True(tile.Report(TileActivity.Blocked));
        tile.Wait(60);
        // An unchanged state is not a new arrival...
        Assert.False(tile.Report(TileActivity.Blocked));
        // ...and neither is one that went stale in between.
        Assert.False(tile.Report(TileActivity.Unknown));
        Assert.False(tile.Report(TileActivity.Blocked));
    }

    [Fact]
    public void A_prompt_redrawn_while_it_is_answered_is_the_same_question()
    {
        var tile = new Script();
        Assert.True(tile.Report(TileActivity.Blocked));
        Assert.False(tile.Report(TileActivity.Working));
        tile.Wait(1);
        Assert.False(tile.Report(TileActivity.Blocked));
    }

    /// <summary>The regression: a new question shortly after the last one was answered used to fall inside a
    /// cooldown measured from the notification, and nothing ever asked again.</summary>
    [Fact]
    public void A_new_question_soon_after_the_last_was_answered_is_announced()
    {
        var tile = new Script();
        Assert.True(tile.Report(TileActivity.Blocked));
        tile.Wait(2);
        Assert.False(tile.Report(TileActivity.Working));
        tile.Wait(10);
        Assert.True(tile.Report(TileActivity.Blocked));
    }

    [Fact]
    public void A_question_seen_on_screen_gets_no_toast_later()
    {
        var tile = new Script();
        Assert.False(tile.Report(TileActivity.Blocked, onScreen: true));
        Assert.False(tile.Report(TileActivity.Unknown));
        Assert.False(tile.Report(TileActivity.Blocked));
    }

    [Theory]
    // A stale reading is the absence of an answer, not the end of the question.
    [InlineData(TileActivity.Unknown, false)]
    [InlineData(TileActivity.Blocked, false)]
    [InlineData(TileActivity.Working, true)]
    [InlineData(TileActivity.Idle, true)]
    public void Only_an_affirmative_state_ends_the_question(TileActivity current, bool expected) =>
        Assert.Equal(expected, BlockedNotificationPolicy.EndsQuestion(current));

    /// <summary>A tile's name is typed by the user and lands inside a PowerShell script: a quote in it must
    /// stay text, never end the string.</summary>
    [Fact]
    public void A_quote_in_a_tile_name_cannot_escape_the_toast_script()
    {
        var script = WindowsToastNotifier.Script("it's'); Remove-Item x; ('", "<b>&</b>");

        var load = script.Split('\n').Single(line => line.StartsWith("$xml.LoadXml("));

        // The one pair of quotes on that line is the string's own.
        Assert.Equal(2, load.Count(c => c == '\''));
        Assert.Contains("it&apos;s&apos;); Remove-Item x; (&apos;", load);
        Assert.Contains("&lt;b&gt;&amp;&lt;/b&gt;", load);
    }
}
