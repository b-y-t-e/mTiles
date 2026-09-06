using mTiles;
using Xunit;

namespace mTiles.Tests;

/// <summary>That the application can be composed at all.</summary>
/// <remarks>
/// <para>0.4.43 shipped an application that aborted on its first line: <c>UseWaylandWithFallback</c>
/// throws unless a fallback backend is registered first, and the two calls were the wrong way
/// round.</para>
/// <para><b>This test does not catch that on Windows, and that was measured rather than assumed</b>
/// — put the calls back in the wrong order and it still passes here, because the guard inside
/// <c>UseWaylandWithFallback</c> only fires on Linux. What it does catch is anything that makes the
/// composition throw outright, and it is written with the branch as a parameter so that the run on
/// a Linux agent takes the Wayland path. That run is the half that has teeth, and it is why
/// release.yml now runs the suite on Linux as well.</para>
/// </remarks>
public class AppBuilderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_application_can_be_composed(bool useWayland)
    {
        var builder = Program.BuildAvaloniaApp(useWayland);
        Assert.NotNull(builder);
    }
}
