namespace mTiles.Models;

/// <summary>Which child of a split is held at a size in pixels rather than given a share.</summary>
/// <remarks>
/// <para>A share is right for tiles that should grow with the window: two terminals side by side stay
/// half and half however wide it gets. It is wrong for a tile whose size is a fact about its content —
/// a list of names wants the width a name needs, and a strip of tabs wants one row — and on the window's
/// own layout both of those sit beside a tile that should take whatever is left.</para>
/// <para>One side at most. Two fixed sides would be a split that cannot follow the window at all, and
/// the minimum sizes would then have nowhere to take their room from.</para>
/// </remarks>
public enum SplitFixedSide
{
    /// <summary>Both children share the split by its ratio.</summary>
    None,

    /// <summary>The first child (left or top) has a size in pixels; the second takes the rest.</summary>
    First,

    /// <summary>The second child (right or bottom) has a size in pixels; the first takes the rest.</summary>
    Second
}
