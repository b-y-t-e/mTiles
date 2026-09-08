using mTiles.Models;
using Terminal.Avalonia;

namespace mTiles.Services.Activity;

/// <summary>
/// One way of finding out what a tile is doing.
/// </summary>
/// <remarks>
/// <para>A source knows how to <em>read</em> one signal and nothing else: it does not compare itself
/// with the others, does not smooth, and does not decide when its own answer has gone stale. All three
/// belong to <see cref="ActivityPolicy"/>, which is why they are testable without a terminal.</para>
/// <para><b>A source may answer <see cref="TileActivity.Unknown"/> and often should.</b> It is the
/// difference between a rule that matched and a rule that did not, and only the first is allowed to
/// silence a lower-ranked source. A source that has nothing to say says so; it never guesses at
/// <see cref="TileActivity.Idle"/> to fill the gap.</para>
/// </remarks>
public interface IActivitySource : IDisposable
{
    /// <summary>How much this source's answers are worth against the others'.</summary>
    ActivityAuthority Authority { get; }

    /// <summary>Raised on the UI thread whenever this source has something new to say.</summary>
    event EventHandler<ActivityReading>? Reported;

    /// <summary>Starts watching a terminal, letting go of whatever was watched before.</summary>
    void Attach(TerminalControl terminal);
}
