namespace mTiles.Tests;

/// <summary>A clock that moves only when a test tells it to.</summary>
/// <remarks>For code that asks how long something took — a session's lifetime is measured on the
/// terminal's <c>TimeProvider</c> — so a test can say "this ran for two minutes" without waiting two
/// minutes. It does not drive timers: a <c>Task.Delay</c> on it would never complete, so hand it only to
/// code that reads timestamps.</remarks>
internal sealed class ManualClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    public void Advance(int milliseconds) => Advance(TimeSpan.FromMilliseconds(milliseconds));
}
