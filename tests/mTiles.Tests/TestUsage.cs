using mTiles.Models;

namespace mTiles.Tests;

/// <summary>
/// The <see cref="AiUsage"/> a test means when it is asking about a headless run that writes.
/// </summary>
/// <remarks>
/// Named once and imported with <c>using static</c>, because it appears in nearly every assertion about
/// a command line and <c>AiUsage.Headless(GoalPhase.Implement)</c> inline three times on a line buries
/// what the line is actually about. It is deliberately the phase that <em>writes</em>: the read-only
/// phases override what the tile asked for, so a test written against one of those would be asserting
/// the override rather than the mapping.
/// </remarks>
internal static class TestUsage
{
    public static readonly AiUsage Implementing = AiUsage.Headless(GoalPhase.Implement);

    /// <summary>A phase that only reads, for the tests that are about the override itself.</summary>
    /// <remarks>The criteria that would send it to a compiler are off, which is what leaves it read-only
    /// — see <see cref="ReviewingWithHealthChecks"/>.</remarks>
    public static readonly AiUsage Reviewing = AiUsage.Headless(GoalPhase.Review);

    /// <summary>The same phase with the build and the tests to establish, which is the tile's default.
    /// </summary>
    public static readonly AiUsage ReviewingWithHealthChecks =
        AiUsage.Headless(GoalPhase.Review, checksProjectHealth: true);
}
