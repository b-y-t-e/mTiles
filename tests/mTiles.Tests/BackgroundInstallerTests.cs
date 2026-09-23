using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

public class BackgroundInstallerTests
{
    [Theory]
    [InlineData("Found rtk", "Found rtk")]
    [InlineData("  ██ 1 MB / 5 MB\r  ████ 5 MB / 5 MB\r", "  ████ 5 MB / 5 MB")]
    [InlineData("\r   \r", null)]
    public void A_redrawn_line_keeps_only_its_last_frame(string line, string? expected) =>
        Assert.Equal(expected, BackgroundInstaller.LastRedrawOf(line));
}
