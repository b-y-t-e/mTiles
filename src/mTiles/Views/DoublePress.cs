namespace mTiles.Views;

/// <summary>
/// Whether a key press is the second of two close enough together to count as one gesture.
/// </summary>
/// <remarks>A third press straight after is the first of a new pair rather than a second double, so
/// Escape held down or pressed three times clears once and not repeatedly.</remarks>
public sealed class DoublePress(TimeSpan window)
{
    /// <summary>Claude Code's own feel for Escape twice: quick, but not a race.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

    private DateTime? _first;

    /// <summary>Records a press at <paramref name="now"/>; true when it completes a pair.</summary>
    public bool Press(DateTime now)
    {
        if (_first is { } first && now - first <= window)
        {
            _first = null;
            return true;
        }

        _first = now;
        return false;
    }

    /// <summary>Forgets a first press, so the next one starts a pair.</summary>
    public void Reset() => _first = null;
}
