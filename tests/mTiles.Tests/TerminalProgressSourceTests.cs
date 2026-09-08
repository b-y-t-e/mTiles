using mTiles.Models;
using mTiles.Services.Activity;
using Terminal.Emulation;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a child's own progress report (OSC 9;4) means for the tile it is running in.
/// </summary>
/// <remarks>The only source here that needs to know nothing about what is running, which is why a plain
/// shell tile gets a real answer out of it: <c>npm</c>, <c>cargo</c> and <c>winget</c> all report
/// progress, and none of them has a status bar anybody could have written a rule for.</remarks>
public class TerminalProgressSourceTests
{
    /// <summary>The two states that say a job is under way. Indeterminate is the one that matters: it is
    /// what a tool emits when it is working and cannot say for how much longer.</summary>
    [Theory]
    [InlineData(TerminalProgressState.Indeterminate)]
    [InlineData(TerminalProgressState.Normal)]
    public void A_job_under_way_is_working(TerminalProgressState state) =>
        Assert.Equal(TileActivity.Working, TerminalProgressSource.StateOf(state));

    /// <summary>State 0 is the only affirmative "finished" a child sends, and the only report here
    /// allowed to put a light out.</summary>
    [Fact]
    public void Finishing_is_the_one_report_that_says_idle() =>
        Assert.Equal(TileActivity.Idle, TerminalProgressSource.StateOf(TerminalProgressState.None));

    /// <summary>
    /// Error and Warning answer nothing, on purpose. They say what became of the job, not whether the
    /// tile is busy — a tool can report either and carry on, or report either and stop — so they fall
    /// through to the output light instead of asserting a state that would silence it.
    /// </summary>
    [Theory]
    [InlineData(TerminalProgressState.Error)]
    [InlineData(TerminalProgressState.Warning)]
    public void An_outcome_says_nothing_about_whether_the_tile_is_busy(TerminalProgressState state) =>
        Assert.Equal(TileActivity.Unknown, TerminalProgressSource.StateOf(state));

    /// <summary>The figure is shown only where it means something. For every other state 0 and "did not
    /// say" are the same word, and a tooltip reading "0%" against an indeterminate job is worse than one
    /// saying nothing.</summary>
    [Fact]
    public void The_figure_is_only_offered_where_it_has_a_meaning()
    {
        Assert.Equal("42%",
            TerminalProgressSource.DetailOf(new TerminalProgress(TerminalProgressState.Normal, 42)));
        Assert.Null(
            TerminalProgressSource.DetailOf(new TerminalProgress(TerminalProgressState.Indeterminate)));
    }

    /// <summary>
    /// It is ranked with the title, because it is the same kind of evidence — something the child chose
    /// to send rather than something we noticed about it — and the two then behave as one instrument.
    /// </summary>
    [Fact]
    public void It_speaks_with_the_same_authority_as_the_title() =>
        Assert.Equal(ActivityAuthority.Osc, new TerminalProgressSource().Authority);
}
