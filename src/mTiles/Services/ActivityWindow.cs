namespace mTiles.Services;

/// <summary>
/// "Something happened recently" as a value: stamped when work is seen, asked whether that still counts.
/// </summary>
/// <remarks>
/// <para>A terminal produces output in bursts, so the raw signal flickers many times a second and says
/// nothing on its own. What a reader of the workspace list wants to know is whether the tile is
/// <em>working</em>, which is the burst smoothed over a window — the same idea as a network activity
/// light, and for the same reason.</para>
/// <para>Pure, and time is passed in rather than read: the window is a policy that has to be readable in
/// a test without a timer, a dispatcher or a sleep.</para>
/// </remarks>
public sealed class ActivityWindow
{
    /// <summary>How long after the last sign of work a tile still counts as working.</summary>
    /// <remarks>Long enough that a command printing line by line does not blink, short enough that a
    /// finished command stops claiming the light while the user is still looking at it.</remarks>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(2);

    /// <summary>How often the window has to be re-asked for the answer to expire on time.</summary>
    public static readonly TimeSpan ExpiryCheckInterval = TimeSpan.FromMilliseconds(500);

    private readonly TimeSpan _window;
    private DateTime? _lastActivity;

    public ActivityWindow(TimeSpan? window = null) => _window = window ?? DefaultWindow;

    /// <summary>Records that work was seen at <paramref name="now"/>.</summary>
    public void Stamp(DateTime now) => _lastActivity = now;

    /// <summary>Whether the last stamp is still inside the window.</summary>
    public bool IsActive(DateTime now) => _lastActivity is { } last && now - last < _window;
}
