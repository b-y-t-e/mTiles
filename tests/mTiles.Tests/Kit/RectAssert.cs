using Avalonia;
using Xunit;

namespace mTiles.Tests;

/// <summary>Rectangles compared to six decimal places, which is as far as layout arithmetic is exact.</summary>
internal static class RectAssert
{
    public static void Close(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, precision: 6);
        Assert.Equal(expected.Y, actual.Y, precision: 6);
        Assert.Equal(expected.Width, actual.Width, precision: 6);
        Assert.Equal(expected.Height, actual.Height, precision: 6);
    }
}
