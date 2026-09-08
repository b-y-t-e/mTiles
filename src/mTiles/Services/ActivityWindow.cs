namespace mTiles.Services;

/// <summary>
/// The activity smoothing window's value, kept as a named constant rather than a literal in
/// <c>ActivityPolicy</c>.
/// </summary>
/// <remarks>
/// A terminal produces output in bursts, so the raw signal flickers many times a second and says
/// nothing on its own. What a reader of the workspace list wants to know is whether the tile is
/// <em>working</em>, which is the burst smoothed over a window — the same idea as a network activity
/// light, and for the same reason. See <c>ActivityPolicy.FreshnessOf</c> for where this is spent.
/// </remarks>
public static class ActivityWindow
{
    /// <summary>How long after the last sign of work a tile still counts as working.</summary>
    /// <remarks>Long enough that a command printing line by line does not blink, short enough that a
    /// finished command stops claiming the light while the user is still looking at it.</remarks>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(2);
}
