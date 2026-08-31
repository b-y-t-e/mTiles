using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a stored id may become when it is used as a directory or file name.
/// </summary>
/// <remarks>The rule was written twice — once for a sign-in's directory, once for the generated
/// opencode config — and only one copy had a test. Two copies of a path sanitiser is a traversal
/// waiting for one of them to drift, so there is one now, and the table is the argument.</remarks>
public class SafePathComponentTests
{
    [Theory]
    // What actually arrives: generated ids, unchanged.
    [InlineData("abc123", "abc123")]
    [InlineData("claude", "claude")]
    [InlineData("a-b_c", "a-b_c")]
    // What a hand-edited settings.json could arrive with. Nothing here may address another directory.
    [InlineData("../../escape", "------escape")]
    [InlineData("a/b", "a-b")]
    [InlineData("a\\b", "a-b")]
    [InlineData("C:", "C-")]
    [InlineData("..", "unnamed")]
    [InlineData(".", "unnamed")]
    [InlineData("", "unnamed")]
    [InlineData("   ", "unnamed")]
    public void A_component_is_safe(string given, string expected) =>
        Assert.Equal(expected, SafePathComponent.Of(given));

    /// <summary>Whatever comes out is one component, and never one that walks upwards.</summary>
    /// <remarks>The property rather than the spelling: the exact substitution matters less than that no
    /// input can produce a separator or a parent reference.</remarks>
    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("/absolute/path")]
    [InlineData("with space")]
    [InlineData("emoji-\U0001F600")]
    public void Nothing_can_leave_its_directory(string given)
    {
        var safe = SafePathComponent.Of(given);

        Assert.DoesNotContain(Path.DirectorySeparatorChar, safe);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, safe);
        Assert.DoesNotContain("..", safe);
        Assert.Equal(safe, Path.GetFileName(safe));
    }
}
