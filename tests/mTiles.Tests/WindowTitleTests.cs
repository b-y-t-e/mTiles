using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

public class WindowTitleTests
{
    [Theory]
    // The workspace leads, because a taskbar button truncates from the right and the beginning is
    // the half that differs between two windows.
    [InlineData("mterminal", "mterminal — mTiles")]
    [InlineData(WorkspaceDisplayName.Home, "Home directory — mTiles")]
    // Nothing open is the application's own name and nothing else — not a dangling separator.
    [InlineData(null, "mTiles")]
    [InlineData("", "mTiles")]
    [InlineData("   ", "mTiles")]
    // A stored name with stray whitespace must not push the separator away from the word.
    [InlineData("  spaced  ", "spaced — mTiles")]
    public void NamesTheOpenWorkspaceFirst(string? workspaceName, string expected) =>
        Assert.Equal(expected, WindowTitle.For(workspaceName));
}
