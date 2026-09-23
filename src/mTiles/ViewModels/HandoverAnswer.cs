namespace mTiles.ViewModels;

/// <summary>
/// What the user answered when a tile's agent changes and the work so far could travel with it.
/// </summary>
/// <remarks><see cref="Cancel"/> is zero so an unanswered question — no window to ask in, Escape — is the
/// switch not made, the rule every other question about somebody's work keeps here.</remarks>
public enum HandoverAnswer
{
    /// <summary>Stay where the tile is.</summary>
    Cancel = 0,

    /// <summary>Switch, and give the arriving agent a brief of the work so far.</summary>
    WithContext,

    /// <summary>Switch, and let the arriving agent start knowing nothing of it.</summary>
    WithoutContext,
}
