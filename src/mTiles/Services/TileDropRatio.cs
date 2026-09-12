namespace mTiles.Services;

/// <summary>
/// How much room a tile dropped between others takes, and how the tiles already there give it up.
/// </summary>
/// <remarks>
/// <para>Pure, and argued in a table test, for the reason <see cref="ChainPolicy"/> and
/// <see cref="TileMinimumSize"/> are: it is an opinion about a layout rather than a fact about a
/// control, and reading it off a screenshot is how an opinion stops being checkable.</para>
/// <para><b>Why a third, and why taken from both sides.</b> A drop on a tile's own edge splits that one
/// tile in two, so half is what the gesture said and what <c>MoveToEdge</c> has always given. A drop on
/// a gutter or on the workspace's edge says something else — the tile goes <em>between</em> what is
/// already there, or beside all of it — and taking the room out of one neighbour alone makes the
/// gesture asymmetric in a way nothing on screen explains. A third, out of both in proportion, is the
/// reading that leaves the existing layout recognisable: two panes that were 80/20 come back 53/13 and
/// are still 80/20 of what is left to them.</para>
/// </remarks>
public static class TileDropRatio
{
    /// <summary>The share of a split that a tile dropped into it takes.</summary>
    public const double NewcomerShare = 1.0 / 3.0;

    /// <summary>
    /// The two ratios a split needs after a tile is inserted between its children.
    /// </summary>
    /// <param name="ratio">What the split's first child had before the drop.</param>
    /// <returns>
    /// <c>Outer</c> is what the split itself becomes — the first child's new share — and <c>Inner</c> is
    /// the ratio of the split that now holds the newcomer and the old second child.
    /// </returns>
    public static (double Outer, double Inner) Gutter(double ratio)
    {
        var first = Math.Clamp(ratio, 0, 1);
        var kept = 1 - NewcomerShare;

        var outer = first * kept;
        var second = (1 - first) * kept;

        // The denominator is NewcomerShare at worst, so this never divides by zero however degenerate
        // the stored ratio is.
        return (outer, NewcomerShare / (NewcomerShare + second));
    }

    /// <summary>The ratio of the split that puts a dropped tile along one edge of everything else.</summary>
    /// <param name="newcomerFirst">Whether the tile lands on the left or the top.</param>
    public static double Edge(bool newcomerFirst) =>
        newcomerFirst ? NewcomerShare : 1 - NewcomerShare;

    /// <summary>
    /// Where the dropped tile will end up, as a fraction of the extent it is being dropped into.
    /// </summary>
    /// <remarks>What the drop hint draws, so the band under the pointer is the room the tile actually
    /// gets rather than a marker somebody sized by eye.</remarks>
    public static (double Start, double Size) GutterBand(double ratio) =>
        (Gutter(ratio).Outer, NewcomerShare);

    /// <inheritdoc cref="GutterBand"/>
    public static (double Start, double Size) EdgeBand(bool newcomerFirst) =>
        (newcomerFirst ? 0 : 1 - NewcomerShare, NewcomerShare);
}
