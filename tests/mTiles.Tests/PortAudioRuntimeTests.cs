using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The one rule that keeps a rescan from ending the process.
/// </summary>
/// <remarks>
/// <para><c>Pa_Terminate</c> closes every open stream in the process, including from under a callback
/// running on the driver's realtime thread — an access violation, not an exception. So a rescan is only
/// safe while nothing anywhere has a stream open, and the count of those belongs to the library rather
/// than to whichever capture happens to hold one: the old count lived on <c>PortAudioCapture</c> and
/// answered "is <em>this</em> object recording", which is a different question, and the check sat in the
/// caller where the next caller could forget it.</para>
/// <para>No microphone is involved. Whether the teardown ran is invisible from outside portaudio, which
/// is what <c>Generation</c> is for: it counts the times the library was actually asked to enumerate the
/// machine, so "the rescan was refused" becomes something to assert rather than something to hope.</para>
/// </remarks>
public class PortAudioRuntimeTests
{
    /// <summary>Baseline. Without it every assertion below passes against a <c>Reinitialize</c> that
    /// does nothing at all, on any machine, for ever.</summary>
    [Fact]
    public void A_rescan_with_nothing_open_really_does_rescan()
    {
        var before = PortAudioRuntime.Generation;

        PortAudioRuntime.Reinitialize();

        Assert.True(PortAudioRuntime.Generation > before,
            "Reinitialize did not re-enumerate — the headset somebody just plugged in cannot appear.");
    }

    [Fact]
    public void A_rescan_is_refused_while_a_stream_is_open()
    {
        PortAudioRuntime.StreamOpened();
        try
        {
            var before = PortAudioRuntime.Generation;

            PortAudioRuntime.Reinitialize();

            Assert.Equal(before, PortAudioRuntime.Generation);
        }
        finally
        {
            PortAudioRuntime.StreamClosed();
        }

        // And the refusal lasts exactly as long as the stream does.
        var after = PortAudioRuntime.Generation;
        PortAudioRuntime.Reinitialize();
        Assert.True(PortAudioRuntime.Generation > after);
    }

    /// <summary>
    /// Two overlapping recordings both have to close before a rescan is allowed.
    /// </summary>
    /// <remarks>
    /// They do overlap: a stop hands the stream to the thread pool to be closed and clears the field
    /// naming it, so for the 50–150 ms that takes — up to two seconds when the consumer has to drain —
    /// the next recording can already have started. A flag would have been cleared by the first one to
    /// finish, which is precisely the window this is counted to cover.
    /// </remarks>
    [Fact]
    public void Overlapping_streams_are_counted_rather_than_flagged()
    {
        PortAudioRuntime.StreamOpened();
        PortAudioRuntime.StreamOpened();
        PortAudioRuntime.StreamClosed();

        var before = PortAudioRuntime.Generation;
        PortAudioRuntime.Reinitialize();
        Assert.Equal(before, PortAudioRuntime.Generation);

        PortAudioRuntime.StreamClosed();
        PortAudioRuntime.Reinitialize();
        Assert.True(PortAudioRuntime.Generation > before);
    }

    /// <summary>
    /// A close that is reported twice does not leave the count below zero.
    /// </summary>
    /// <remarks>
    /// <c>Finish</c> is reachable from a stop and from <c>Dispose</c>, and both are reachable while the
    /// application is closing. A negative count would be worse than a wrong one: it would make the next
    /// stream's <c>+1</c> look like nothing is open, and the refusal above would let a rescan through
    /// with a live callback running.
    /// </remarks>
    [Fact]
    public void Closing_more_than_was_opened_does_not_go_negative()
    {
        PortAudioRuntime.StreamClosed();
        PortAudioRuntime.StreamClosed();

        PortAudioRuntime.StreamOpened();
        var before = PortAudioRuntime.Generation;
        PortAudioRuntime.Reinitialize();
        Assert.Equal(before, PortAudioRuntime.Generation);

        PortAudioRuntime.StreamClosed();
    }
}
