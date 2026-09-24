using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

public class WindowTitleTests
{
    [Theory]
    // The workspace leads, because a taskbar button truncates from the right and the beginning is
    // the half that differs between two windows.
    [InlineData("mterminal", "mterminal - mTiles")]
    [InlineData(WorkspaceDisplayName.Home, "Home directory - mTiles")]
    // Nothing open is the application's own name and nothing else — not a dangling separator.
    [InlineData(null, "mTiles")]
    [InlineData("", "mTiles")]
    [InlineData("   ", "mTiles")]
    // A stored name with stray whitespace must not push the separator away from the word.
    [InlineData("  spaced  ", "spaced - mTiles")]
    public void NamesTheOpenWorkspaceFirst(string? workspaceName, string expected) =>
        Assert.Equal(expected, WindowTitle.For(workspaceName));

    [Theory]
    // The version rides on the application's name, so it is the first thing a truncated taskbar
    // button drops and the workspace is the last.
    [InlineData("mterminal", "0.4.114", "mterminal - mTiles 0.4.114")]
    [InlineData(null, "0.4.114", "mTiles 0.4.114")]
    // A build that cannot say what it is says nothing rather than leaving a dangling space.
    [InlineData("mterminal", null, "mterminal - mTiles")]
    [InlineData("mterminal", "   ", "mterminal - mTiles")]
    [InlineData(null, "", "mTiles")]
    public void CarriesTheVersionAfterTheApplicationName(string? workspaceName, string? version, string expected) =>
        Assert.Equal(expected, WindowTitle.For(workspaceName, version));
}
