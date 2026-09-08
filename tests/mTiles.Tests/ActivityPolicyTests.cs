using mTiles.Models;
using mTiles.Services.Activity;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The three rules a tile's light is decided by. Each one is an opinion and each was chosen against a
/// failure, so each is argued here rather than in a comment beside the code that carries it out.
/// </summary>
public class ActivityPolicyTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ActivityReading Reading(
        TileActivity state, ActivityAuthority authority, double atSeconds = 0, string? detail = null) =>
        new(state, authority, T0.AddSeconds(atSeconds), detail);

    private static TileActivity Settle(
        IReadOnlyList<ActivityReading> readings, double nowSeconds, TileActivity current = TileActivity.Unknown)
        => ActivityPolicy.Decide(readings, T0.AddSeconds(nowSeconds), current, null).State;

    // ---- Rank ------------------------------------------------------------------------------------

    /// <summary>The whole point of ranking: an agent that says it has finished is believed over its own
    /// terminal still writing the last frame of what it finished.</summary>
    [Fact]
    public void A_higher_authority_silences_a_lower_one()
    {
        ActivityReading[] readings =
        [
            Reading(TileActivity.Working, ActivityAuthority.Output),
            Reading(TileActivity.Idle, ActivityAuthority.Lifecycle),
        ];

        Assert.Equal(TileActivity.Idle, Settle(readings, 0.5));
    }

    /// <summary>And the ordering is not "whichever spoke last": output arriving after a lifecycle
    /// report is the tool printing its own summary, not a new turn starting.</summary>
    [Fact]
    public void A_lower_authority_speaking_later_still_loses()
    {
        ActivityReading[] readings =
        [
            Reading(TileActivity.Idle, ActivityAuthority.Lifecycle),
            Reading(TileActivity.Working, ActivityAuthority.Output, atSeconds: 1),
        ];

        Assert.Equal(TileActivity.Idle, Settle(readings, 1.2));
    }

    /// <summary>Within one rank the newer reading wins, so a title and a progress report behave as one
    /// instrument rather than as a race.</summary>
    [Fact]
    public void Within_one_rank_the_newer_reading_wins()
    {
        ActivityReading[] readings =
        [
            Reading(TileActivity.Working, ActivityAuthority.Osc),
            Reading(TileActivity.Idle, ActivityAuthority.Osc, atSeconds: 1),
        ];

        Assert.Equal(TileActivity.Idle, Settle(readings, 1.2));
    }

    // ---- Unknown is not an answer ---------------------------------------------------------------

    /// <summary>
    /// The rule the whole design rests on: a source with no matching rule must not be able to put a
    /// light out. "No rule matched this screen" and "the agent reports it is idle" are different
    /// statements, and reading the first as the second is how a working tile goes dark.
    /// </summary>
    [Fact]
    public void An_unknown_from_a_higher_source_does_not_silence_a_lower_one()
    {
        ActivityReading[] readings =
        [
            Reading(TileActivity.Working, ActivityAuthority.Output),
            Reading(TileActivity.Unknown, ActivityAuthority.Lifecycle),
        ];

        Assert.Equal(TileActivity.Working, Settle(readings, 0.5));
    }

    /// <summary>A tile nothing has answered about says nothing, rather than claiming to be at rest.
    /// </summary>
    [Fact]
    public void Nothing_answering_is_unknown_and_not_idle()
    {
        Assert.Equal(TileActivity.Unknown, Settle([], 0));
    }

    // ---- Freshness -------------------------------------------------------------------------------

    /// <summary>A reading has a shelf life, and a stale one is not read at all — which is what lets a
    /// source report only what it can see and never have to report its own silence.</summary>
    [Fact]
    public void A_stale_reading_stops_counting()
    {
        ActivityReading[] readings = [Reading(TileActivity.Working, ActivityAuthority.Output)];

        Assert.Equal(TileActivity.Working, Settle(readings, 1));
        Assert.Equal(TileActivity.Unknown, Settle(readings, 30));
    }

    /// <summary>Each rank keeps its answer for as long as that kind of answer is worth: a statement
    /// outlives a symptom by a wide margin, because a statement is not repeated.</summary>
    [Fact]
    public void A_statement_outlives_a_symptom()
    {
        Assert.True(ActivityPolicy.FreshnessOf(ActivityAuthority.Lifecycle)
                    > ActivityPolicy.FreshnessOf(ActivityAuthority.Osc));
        Assert.True(ActivityPolicy.FreshnessOf(ActivityAuthority.Osc)
                    > ActivityPolicy.FreshnessOf(ActivityAuthority.Screen));
        Assert.True(ActivityPolicy.FreshnessOf(ActivityAuthority.Screen)
                    > ActivityPolicy.FreshnessOf(ActivityAuthority.Output));
    }

    // ---- The asymmetric debounce -----------------------------------------------------------------

    /// <summary>Up is immediate. A tile that looks asleep while it builds is the failure this replaced.
    /// </summary>
    [Fact]
    public void Working_is_reported_the_moment_it_is_seen()
    {
        var decision = ActivityPolicy.Decide(
            [Reading(TileActivity.Working, ActivityAuthority.Output)], T0, TileActivity.Idle, null);

        Assert.Equal(TileActivity.Working, decision.State);
        Assert.Null(decision.PendingSince);
    }

    /// <summary>
    /// Down has to be proved, and this is the rule that matters most: the transition that gets missed
    /// is working to idle, so the state that turns the light <em>off</em> is the one that has to hold.
    /// </summary>
    [Fact]
    public void Going_quiet_has_to_hold_before_the_light_goes_out()
    {
        var readings = new[] { Reading(TileActivity.Idle, ActivityAuthority.Osc) };

        var first = ActivityPolicy.Decide(readings, T0, TileActivity.Working, null);
        Assert.Equal(TileActivity.Working, first.State);
        Assert.Equal(T0, first.PendingSince);

        var tooSoon = ActivityPolicy.Decide(
            readings, T0.Add(ActivityPolicy.IdleConfirmation) - TimeSpan.FromMilliseconds(1),
            TileActivity.Working, first.PendingSince);
        Assert.Equal(TileActivity.Working, tooSoon.State);

        var due = ActivityPolicy.Decide(
            readings, T0.Add(ActivityPolicy.IdleConfirmation), TileActivity.Working, first.PendingSince);
        Assert.Equal(TileActivity.Idle, due.State);
        Assert.Null(due.PendingSince);
    }

    /// <summary>Work resuming inside the window cancels the fall outright, so a tool that pauses between
    /// two tool calls does not blink.</summary>
    [Fact]
    public void Work_resuming_inside_the_window_cancels_the_fall()
    {
        var resumed = ActivityPolicy.Decide(
            [Reading(TileActivity.Working, ActivityAuthority.Output, atSeconds: 1)],
            T0.AddSeconds(1), TileActivity.Working, T0);

        Assert.Equal(TileActivity.Working, resumed.State);
        Assert.Null(resumed.PendingSince);
    }

    /// <summary>Blocked is a claim about work in progress too, so leaving it is proved the same way. A
    /// permission prompt that scrolls out of a screen window must not put the row out a frame later.
    /// </summary>
    [Fact]
    public void Leaving_blocked_is_proved_the_same_way()
    {
        var readings = new[] { Reading(TileActivity.Idle, ActivityAuthority.Osc) };

        var first = ActivityPolicy.Decide(readings, T0, TileActivity.Blocked, null);
        Assert.Equal(TileActivity.Blocked, first.State);
        Assert.NotNull(first.PendingSince);
    }

    /// <summary>Blocked replacing working is immediate, though: it is not a fall, it is the one
    /// transition somebody has to be told about at once.</summary>
    [Fact]
    public void Working_becoming_blocked_is_immediate()
    {
        var decision = ActivityPolicy.Decide(
            [Reading(TileActivity.Blocked, ActivityAuthority.Screen)], T0, TileActivity.Working, null);

        Assert.Equal(TileActivity.Blocked, decision.State);
        Assert.Null(decision.PendingSince);
    }

    /// <summary>Nothing to nothing is immediate. Making anyone wait two seconds for one kind of nothing
    /// to become another is a delay with nothing behind it.</summary>
    [Fact]
    public void Unknown_becoming_idle_needs_no_confirmation()
    {
        var decision = ActivityPolicy.Decide(
            [Reading(TileActivity.Idle, ActivityAuthority.Osc)], T0, TileActivity.Unknown, null);

        Assert.Equal(TileActivity.Idle, decision.State);
        Assert.Null(decision.PendingSince);
    }

    // ---- The sentence ----------------------------------------------------------------------------

    /// <summary>The detail belongs to the state that was settled on, taken from the best source that
    /// has one — including while a fall is being held, when the reason is still the right one to show.
    /// </summary>
    [Fact]
    public void The_detail_follows_the_state_that_was_settled_on()
    {
        ActivityReading[] readings =
        [
            Reading(TileActivity.Blocked, ActivityAuthority.Screen, detail: "Waiting for permission"),
            Reading(TileActivity.Working, ActivityAuthority.Output),
        ];

        Assert.Equal("Waiting for permission",
            ActivityPolicy.DetailFor(readings, T0, TileActivity.Blocked));
        Assert.Null(ActivityPolicy.DetailFor(readings, T0, TileActivity.Working));
        Assert.Null(ActivityPolicy.DetailFor(readings, T0, TileActivity.Unknown));
    }
}
