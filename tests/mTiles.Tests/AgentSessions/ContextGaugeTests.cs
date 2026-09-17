using mTiles.AgentSessions.Events;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// How full the context is, and the three ways it cannot be said.
/// </summary>
/// <remarks>A table test for the reason <c>UsagePace</c> has one: this is the arithmetic behind a bar,
/// and the interesting half of it is the answers that are <c>null</c> — each of which, read as a number,
/// draws a bar that lies.</remarks>
public class ContextGaugeTests
{
    [Theory]
    // The ordinary case, and the figures from the tile's own strip.
    [InlineData(105_200, 1_000_000, 10.52)]
    [InlineData(0, 200_000, 0)]
    [InlineData(200_000, 200_000, 100)]
    // Over its own end: an agent that compacts reports the tokens it held before the compaction landed,
    // and a bar drawn past its end is a rendering artefact where a full bar is the truth.
    [InlineData(260_000, 200_000, 100)]
    public void A_window_that_was_named_is_a_share_of_it(long used, long window, double expected) =>
        Assert.Equal(expected, ContextGauge.PercentUsed(new TokenUsage(used, window)) ?? -1, 2);

    [Theory]
    // Claude Code, opencode, pi and agy report the tokens and not the window. Read as zero that is a bar
    // that never moves; read as the tokens themselves it is one that is always full. It is neither.
    // The literals carry `L`: xUnit hands an InlineData value to the method by reflection, and an int
    // does not widen to a long? on that route — it throws rather than failing an assertion.
    [InlineData(105_200L, null)]
    [InlineData(null, 200_000L)]
    [InlineData(null, null)]
    // A window reported as nothing is a field that is there and carries no claim, which is the same
    // answer as not being there — and the only one that does not divide by zero.
    [InlineData(105_200L, 0L)]
    public void A_window_nobody_named_is_no_bar_at_all(long? used, long? window) =>
        Assert.Null(ContextGauge.PercentUsed(new TokenUsage(used, window)));

    [Fact]
    public void No_usage_at_all_is_no_bar_either() => Assert.Null(ContextGauge.PercentUsed(null));
}
