using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

public class NewestInstallationTests
{
    [Theory]
    [InlineData("2.1.280 (Claude Code)", "2.1.280")]
    [InlineData("codex-cli 0.141.0", "0.141.0")]
    [InlineData("v1.18.18\n", "1.18.18")]
    [InlineData("1.1.22", "1.1.22")]
    public void A_version_is_the_first_dotted_number(string output, string expected) =>
        Assert.Equal(Version.Parse(expected), NewestInstallation.ParseVersion(output));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("error: unknown option")]
    public void Output_without_a_version_answers_nothing(string? output) =>
        Assert.Null(NewestInstallation.ParseVersion(output));

    [Fact]
    public void The_highest_version_wins_wherever_it_is_on_the_path() =>
        Assert.Equal(1, NewestInstallation.Pick([new Version(2, 1, 140), new Version(2, 1, 280)]));

    [Fact]
    public void Of_equal_versions_the_first_on_the_path_wins() =>
        Assert.Equal(0, NewestInstallation.Pick([new Version(2, 1, 280), new Version(2, 1, 280)]));

    [Fact]
    public void An_installation_that_answers_beats_one_that_does_not() =>
        Assert.Equal(1, NewestInstallation.Pick([null, new Version(0, 1)]));

    [Fact]
    public void When_none_answers_the_first_on_the_path_is_kept() =>
        Assert.Equal(0, NewestInstallation.Pick([null, null]));
}
