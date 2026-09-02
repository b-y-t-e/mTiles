using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Whether a window is being spent faster than it is passing.
/// </summary>
/// <remarks>
/// A table, because the interesting cases are the ones a running screen cannot be made to show: a window
/// that has just reset, one about to, and one already past its limit. Each is one comparison away from
/// a card confidently reporting a week that ended in 1601.
/// </remarks>
public class UsagePaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static AiUsageWindow Week(double? used, DateTimeOffset? resets) =>
        new("7d", TimeSpan.FromDays(7), UsedPercent: used, ResetsAt: resets);

    [Fact]
    public void HalfWayThroughAndHalfSpentIsOnPace()
    {
        var pace = UsagePace.For(Week(50, Now.AddDays(3.5)), Now);

        Assert.Equal(UsagePaceState.OnPace, pace.State);
        Assert.Equal(50, pace.ExpectedPercent!.Value, 3);
        Assert.Equal(0, pace.DeltaPoints!.Value, 3);
    }

    [Fact]
    public void SpendingFasterThanTheClockIsAhead()
    {
        var pace = UsagePace.For(Week(80, Now.AddDays(3.5)), Now);

        Assert.Equal(UsagePaceState.Ahead, pace.State);
        Assert.Equal(30, pace.DeltaPoints!.Value, 3);
    }

    [Fact]
    public void SpendingSlowerThanTheClockIsBehind()
    {
        var pace = UsagePace.For(Week(20, Now.AddDays(3.5)), Now);

        Assert.Equal(UsagePaceState.Behind, pace.State);
    }

    /// <summary>The dead band is what keeps a card from flipping between two words every refresh.</summary>
    [Theory]
    [InlineData(52, UsagePaceState.OnPace)]
    [InlineData(53, UsagePaceState.OnPace)]
    [InlineData(53.5, UsagePaceState.Ahead)]
    [InlineData(47, UsagePaceState.OnPace)]
    [InlineData(46.5, UsagePaceState.Behind)]
    public void TheDeadBandIsThreePointsEitherSide(double used, UsagePaceState expected) =>
        Assert.Equal(expected, UsagePace.For(Week(used, Now.AddDays(3.5)), Now).State);

    /// <summary>A window that has just reset has spent nothing over no time, and there is no rate in
    /// that to project from.</summary>
    [Fact]
    public void AJustResetWindowProjectsNothing()
    {
        var pace = UsagePace.For(Week(0, Now.AddDays(7)), Now);

        Assert.Equal(0, pace.ExpectedPercent!.Value, 3);
        Assert.Null(pace.EmptyAt);
    }

    [Fact]
    public void AWindowAboutToResetHasSpentAllOfItsTime()
    {
        var pace = UsagePace.For(Week(90, Now.AddMinutes(1)), Now);

        Assert.True(pace.ExpectedPercent > 99);
        Assert.Equal(UsagePaceState.Behind, pace.State);
    }

    /// <summary>Past the limit is empty now, which is the honest answer rather than an instant in the
    /// past.</summary>
    [Fact]
    public void AnExhaustedWindowIsEmptyNow()
    {
        var pace = UsagePace.For(Week(100, Now.AddDays(3.5)), Now);

        Assert.Equal(Now, pace.EmptyAt);
    }

    /// <summary>A rate that outlasts the window is nothing to warn about, and answering a date for it
    /// is also what would overflow a <c>TimeSpan</c> on the way to the year 40 000.</summary>
    [Fact]
    public void ARateThatOutlastsTheWindowProjectsNothing() =>
        Assert.Null(UsagePace.For(Week(1, Now.AddDays(3.5)), Now).EmptyAt);

    /// <summary>A rate small enough to overflow the multiplication answers null like any other rate
    /// that outlasts its window, rather than throwing on the UI thread.</summary>
    [Fact]
    public void ARateTooSmallToMultiplyProjectsNothing() =>
        Assert.Null(UsagePace.For(Week(1e-9, Now.AddDays(3.5)), Now).EmptyAt);

    [Fact]
    public void ARateThatRunsOutInsideTheWindowProjectsADate()
    {
        var pace = UsagePace.For(Week(70, Now.AddDays(3.5)), Now);

        Assert.NotNull(pace.EmptyAt);
        Assert.InRange(pace.EmptyAt!.Value, Now, Now.AddDays(3.5));
    }

    [Fact]
    public void NoResetInstantIsUnknownRatherThanZero() =>
        Assert.Equal(UsagePaceState.Unknown, UsagePace.For(Week(40, null), Now).State);

    [Fact]
    public void NoPercentageIsUnknown() =>
        Assert.Equal(UsagePaceState.Unknown, UsagePace.For(Week(null, Now.AddDays(1)), Now).State);

    [Fact]
    public void AWindowOfUnknownLengthIsUnknown() =>
        Assert.Equal(UsagePaceState.Unknown,
            UsagePace.For(new AiUsageWindow("?", TimeSpan.Zero, UsedPercent: 40,
                ResetsAt: Now.AddDays(1)), Now).State);

    /// <summary>The elapsed share is clamped, so a reset instant further out than the window's own
    /// length cannot produce a negative expectation.</summary>
    [Fact]
    public void AResetBeyondTheWindowClampsToZeroElapsed() =>
        Assert.Equal(0, UsagePace.For(Week(10, Now.AddDays(30)), Now).ExpectedPercent!.Value, 3);
}
