using mTiles.AgentSessions.Events;
using mTiles.ViewModels;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The bar at the foot of an agent tile: what it says, and when it is drawn at all.
/// </summary>
/// <remarks>One gauge serves both agent tiles from two different sources, so what is pinned here is
/// that the two cannot come out saying different things about the same figures.</remarks>
public class ContextGaugeViewModelTests
{
    [Fact]
    public void A_window_the_agent_did_not_name_leaves_the_figures_and_takes_the_bar()
    {
        // Four of the five CLIs that count tokens never name the limit. A bar drawn anyway would either
        // never move (window read as zero) or always be full (window read as the count).
        var gauge = new ContextGaugeViewModel();

        gauge.Show(used: 106_800, window: null, cost: null);

        Assert.Equal("106.8k tokens", gauge.Text);
        Assert.Null(gauge.UsedPercent);
        Assert.True(gauge.HasAnythingToSay);
    }

    [Fact]
    public void A_window_from_the_provider_stands_in_for_one_the_agent_did_not_name()
    {
        // This is what turns a count into a bar for claude, pi, opencode and grok: the provider says how
        // large the model's context is, which is the same figure codex names for itself.
        var gauge = new ContextGaugeViewModel();

        gauge.Show(used: 106_800, window: null, cost: 1.15m, fallbackWindow: 1_000_000);

        Assert.Equal("106.8k / 1M tokens · $1.15", gauge.Text);
        Assert.Equal(10.68, gauge.UsedPercent!.Value, 3);
    }

    [Fact]
    public void An_agent_that_names_its_own_window_is_not_overruled_by_the_provider()
    {
        // codex is told what it is being served and says so in its rollout; the provider's catalogue is
        // a general answer about the model. The one closer to the session wins.
        var gauge = new ContextGaugeViewModel();

        gauge.Show(used: 17_491, window: 258_400, cost: null, fallbackWindow: 1_000_000);

        Assert.Equal("17.5k / 258.4k tokens", gauge.Text);
    }

    [Fact]
    public void Nothing_said_draws_nothing()
    {
        // The ordinary state of a tile whose agent has not spoken yet, and the permanent state of one
        // whose CLI keeps no readable store. A bar at zero would claim the first turn had happened.
        var gauge = new ContextGaugeViewModel();

        gauge.Show(used: null, window: null, cost: null);

        Assert.Equal("", gauge.Text);
        Assert.False(gauge.HasAnythingToSay);
        Assert.Null(gauge.UsedPercent);
    }

    [Fact]
    public void A_tile_that_will_get_a_reading_keeps_the_row_and_says_nothing_is_known()
    {
        // The row is there from the first frame, so the first turn does not also push the terminal up a
        // line — which is not a nudge here: the cell grid is remeasured and the shell reflows.
        var gauge = new ContextGaugeViewModel { KeepsItsPlace = true };

        Assert.True(gauge.IsDrawn);
        Assert.False(gauge.HasAnythingToSay);
        Assert.Equal("context not known yet", gauge.BarText);
        Assert.Null(gauge.UsedPercent);

        gauge.Show(used: 500, window: 1000, cost: null);

        Assert.Equal("500 / 1k tokens", gauge.BarText);

        // And back to the sentence when the tile leaves the conversation, rather than to the last
        // figure or to nothing where the row had been.
        gauge.Clear();

        Assert.True(gauge.IsDrawn);
        Assert.Equal("context not known yet", gauge.BarText);
    }

    [Fact]
    public void A_tile_that_can_never_get_one_draws_no_row_at_all()
    {
        // agy and Grok keep nothing this application can read, so the sentence would stand there for the
        // life of the session. A blank is the honest answer; a line that never resolves is not.
        var gauge = new ContextGaugeViewModel();

        Assert.False(gauge.IsDrawn);
    }

    /// <summary>Both tiles say the waiting state in the same words.</summary>
    /// <remarks>The Agent tile puts its own cost after it — it is told what the conversation spent,
    /// while this one is told only what some CLIs write down — so what is shared is the sentence, and
    /// the difference is the half that is a real figure there and a guess here.</remarks>
    [Fact]
    public void Both_agent_tiles_say_that_nothing_is_known_the_same_way()
    {
        Assert.StartsWith(ContextGaugeViewModel.NothingKnownYet,
            new ContextGaugeViewModel { KeepsItsPlace = true }.BarText, StringComparison.Ordinal);
        Assert.Equal("context not known yet · $0.00",
            ContextGaugeViewModel.NothingKnownYet + " · $0.00");
    }

    [Fact]
    public void A_cost_of_nothing_is_not_reported_as_free()
    {
        // claude and codex record no price at all, and opencode writes 0 for a turn on a subscription.
        // "$0.00" beside a conversation that has certainly cost something is worse than saying nothing.
        var gauge = new ContextGaugeViewModel();

        gauge.Show(used: 500, window: 1000, cost: 0m);

        Assert.Equal("500 / 1k tokens", gauge.Text);
    }

    [Fact]
    public void Clearing_it_takes_the_bar_away_rather_than_freezing_it()
    {
        // What "New session" calls. A figure left over from the conversation the tile has just left is
        // worse than none, because nothing on screen says it is about something else.
        var gauge = new ContextGaugeViewModel();
        gauge.Show(used: 500, window: 1000, cost: 1m);

        gauge.Clear();

        Assert.False(gauge.HasAnythingToSay);
        Assert.Null(gauge.UsedPercent);
    }

    [Fact]
    public void Both_agent_tiles_say_a_figure_the_same_way()
    {
        // The Agent tile is told its figures by the protocol and the terminal agent tile reads them off
        // disk. Two spellings of one number is how a reader comes to think they are two numbers.
        var usage = new TokenUsage(42_000, 200_000, CostUsd: 0.12m);
        var gauge = new ContextGaugeViewModel();

        gauge.Show(usage.UsedTokens, usage.ContextWindow, usage.CostUsd);

        Assert.Equal(ContextGaugeViewModel.Describe(usage), gauge.Text);
        Assert.Equal("42k / 200k tokens · $0.12", gauge.Text);
    }

    [Fact]
    public void An_amount_under_a_cent_is_not_rounded_down_to_free()
    {
        // Measured on a live pi session: a turn cost $0.0024, and at two decimal places the bar said the
        // conversation had cost nothing — the one thing it had not.
        var gauge = new ContextGaugeViewModel();

        gauge.Show(used: 31_948, window: null, cost: 0.0023971500000000002m);

        Assert.Equal("31.9k tokens · <$0.01", gauge.Text);
    }

    [Fact]
    public void Claudes_own_stream_names_no_window_either()
    {
        // The gap was in both tiles and for one reason. Only codex (modelContextWindow) and ACP (size)
        // report a window over the wire, so the Agent tile's bar — the tile it was built for — was drawn
        // for two agents out of six, and never on a subscription. Asserted at the mapper's own shape so
        // that an agent which starts reporting one is noticed.
        var fromClaude = new TokenUsage(147_900, null);

        Assert.Null(ContextGauge.PercentUsed(fromClaude));
        Assert.Equal(73.95, ContextGauge.PercentUsed(fromClaude with { ContextWindow = 200_000 })!.Value, 2);
    }

    [Fact]
    public void A_window_nobody_can_source_is_not_guessed_at()
    {
        // Measured 2026-09-18 across this machine's own Claude Code transcripts: claude-opus-5 has been
        // seen at 1 000 782 tokens and z-ai/glm-5.3-flash at 534 018, so the CLI's documented 200 000
        // assumption is not the window for either its own models or a third-party one. Falling back to
        // it drew a full bar over a conversation of 234k — and a bar pinned at 100% reads as "about to
        // run out", which is the one thing it must not say wrongly.
        var gauge = new ContextGaugeViewModel();

        gauge.Show(used: 234_100, window: null, cost: null, fallbackWindow: null);

        Assert.Equal("234.1k tokens", gauge.Text);
        Assert.Null(gauge.UsedPercent);
    }
}
