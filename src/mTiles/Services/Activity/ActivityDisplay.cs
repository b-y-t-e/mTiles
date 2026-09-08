using mTiles.Models;

namespace mTiles.Services.Activity;

/// <summary>
/// What a tile's activity is called, for the one place words are wanted.
/// </summary>
/// <remarks>
/// <para>The mark is drawn twice — on the workspace's row and on the tile's own header — and both say
/// the same thing in a tooltip. Written out in each of them, the two would drift, and a workspace
/// reporting "Working" over a tile reporting something else is worse than either.</para>
/// <para>A tooltip and not a label, in both places: the meaning is wanted once, by somebody who has
/// already noticed the mark, and a word beside a name would move the name every time something
/// printed — which is the reason this was a light rather than a caption in the first place.</para>
/// </remarks>
public static class ActivityDisplay
{
    /// <summary>The words for a state, or nothing for a state with nothing to say.</summary>
    /// <remarks><see cref="TileActivity.Idle"/> and <see cref="TileActivity.Unknown"/> answer with an
    /// empty string rather than "Idle": neither draws a mark, so a tooltip on them would be a label for
    /// something that is not there.</remarks>
    public static string Tip(TileActivity activity) => activity switch
    {
        TileActivity.Blocked => "Waiting for you",
        TileActivity.Working => "Working",
        _ => "",
    };
}
