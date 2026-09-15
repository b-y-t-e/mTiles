using Avalonia;

namespace mTiles.Views;

/// <summary>
/// What <see cref="TileDragSession"/> asks about a tile drag's pointer, innermost surface first.
/// </summary>
/// <remarks>The two questions and nothing else, so the walk that asks them can be driven without a tile
/// tree behind every surface it passes.</remarks>
internal interface ITileDragSurface
{
    /// <summary>The pointer of a tile drag is at <paramref name="point"/>, measured against
    /// <paramref name="relativeTo"/>.</summary>
    /// <returns>Whether this surface answered; false leaves the drag to the surface around it.</returns>
    bool DragOver(Visual relativeTo, Point point);

    /// <summary>A tile drag was released at <paramref name="point"/>.</summary>
    /// <returns>Whether this surface took the drop; false leaves it to the surface around it.</returns>
    bool Drop(Visual relativeTo, Point point);
}
