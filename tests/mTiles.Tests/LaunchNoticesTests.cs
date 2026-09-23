using Xunit;
using mTiles.Services;

namespace mTiles.Tests;

/// <summary>
/// The tile's notice bar holds more than one fact, and neither writer may take the other's away.
/// </summary>
public class LaunchNoticesTests
{
    private const string Substitution = "Running Claude Code instead of Codex.";

    [Fact]
    public void The_first_notice_is_the_whole_bar() =>
        Assert.Equal(Substitution, LaunchNotices.With("", Substitution));

    [Fact]
    public void A_second_notice_stands_beside_the_first_rather_than_over_it()
    {
        var bar = LaunchNotices.With(Substitution, SkillChangePolicy.Notice);

        Assert.Contains(Substitution, bar);
        Assert.Contains(SkillChangePolicy.Notice, bar);
    }

    [Fact]
    public void The_same_notice_twice_is_said_once() =>
        Assert.Equal(SkillChangePolicy.Notice,
            LaunchNotices.With(LaunchNotices.With("", SkillChangePolicy.Notice), SkillChangePolicy.Notice));

    [Fact]
    public void Taking_one_notice_down_leaves_the_other_standing()
    {
        var bar = LaunchNotices.With(Substitution, SkillChangePolicy.Notice);

        Assert.Equal(SkillChangePolicy.Notice, LaunchNotices.Without(bar, Substitution));
    }

    [Fact]
    public void Taking_the_last_notice_down_empties_the_bar() =>
        Assert.Equal("", LaunchNotices.Without(Substitution, Substitution));

    /// <summary>The user has dismissed it: withdrawing a notice must not put anything back.</summary>
    [Fact]
    public void A_notice_that_is_not_there_is_not_missed() =>
        Assert.Equal("", LaunchNotices.Without("", Substitution));
}
