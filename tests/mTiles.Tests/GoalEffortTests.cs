using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The effort flag, which is somebody else's CLI contract stated in one place.
/// </summary>
public class GoalEffortTests
{
    /// <summary>
    /// The spellings are what `claude --effort` accepts, and the default is the tile's own opinion
    /// rather than the tool's.
    /// </summary>
    /// <remarks>
    /// High rather than whatever the tool would choose, because a goal run is left alone for an hour
    /// and the budget is in attempts: an attempt spent on a shallow answer costs exactly as much of it
    /// as a careful one. The tool's own default is tuned for interactive use, where a person is
    /// watching and can redirect.
    /// </remarks>
    [Fact]
    public void The_levels_are_the_ones_the_tool_accepts_and_the_default_is_high()
    {
        Assert.Equal(AiEffort.High, new AppSettings().GoalEffort);

        // Measured against `claude --effort`: low, medium, high, xhigh, max.
        Assert.Equal(["low", "medium", "high", "xhigh", "max"],
            AiEfforts.All.Select(AiEfforts.Flag).Where(f => f != null));

        // One level passes no flag at all, which is the way out on a Claude Code older than the option.
        Assert.Null(AiEfforts.Flag(AiEffort.ToolDefault));
        Assert.Equal("tool default", AiEfforts.Label(AiEffort.ToolDefault));

        // A label round-trips, and an unrecognised one is the default rather than an exception while a
        // tile is being built.
        foreach (var effort in AiEfforts.All)
            Assert.Equal(effort, AiEfforts.FromLabel(AiEfforts.Label(effort)));
        Assert.Equal(AiEffort.High, AiEfforts.FromLabel("something else entirely"));
    }

    /// <summary>
    /// A tool refusing the flag is told apart from a tool refusing the work.
    /// </summary>
    /// <remarks>
    /// Measured, and the two cases are not alike: an unknown <em>value</em> is forgiving — the tool
    /// warns and carries on with its own default — while an unknown <em>flag</em> stops the run dead.
    /// So on a Claude Code older than the option, every goal fails, on the default setting, over a flag
    /// the user never typed and cannot see. This is what turns that into a sentence naming the way out.
    /// </remarks>
    [Fact]
    public void A_tool_too_old_for_the_flag_is_recognised_rather_than_blamed_on_the_work()
    {
        Assert.True(AiEfforts.LooksLikeRejectedEffort("error: unknown option '--effort'"));
        Assert.True(AiEfforts.LooksLikeRejectedEffort("Usage: claude [options]\n  --effort <level>"));

        // A warning about the value is not a rejection of the flag: the run carried on and produced an
        // answer, and calling that a configuration problem would send the user to fix what works.
        Assert.False(AiEfforts.LooksLikeRejectedEffort(
            "Warning: Unknown --effort value 'bogus' — ignoring it and using the default effort."));

        // The flag's name alone is not enough either. It appears in plenty of output that is about the
        // work, and a false positive tells somebody their settings are wrong when they are not.
        Assert.False(AiEfforts.LooksLikeRejectedEffort("I set --effort in the script as you asked."));
        Assert.False(AiEfforts.LooksLikeRejectedEffort("error: unknown option '--permission-mode'"));
        Assert.False(AiEfforts.LooksLikeRejectedEffort(null));
    }
}
