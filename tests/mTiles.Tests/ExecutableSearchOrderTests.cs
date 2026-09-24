using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Which copy of a program a bare name finds: directory by directory, then extension by extension — the
/// order a shell searches in — so a tile runs the same CLI the user's own terminal does.
/// </summary>
public class ExecutableSearchOrderTests
{
    private static string PathOf(params string[] directories) => string.Join(Path.PathSeparator, directories);

    private static string? Find(string path, params string[] files) =>
        ExecutableFinder.OnPathRunnable("claude", path, windows: true,
            exists: candidate => files.Contains(candidate, StringComparer.OrdinalIgnoreCase));

    public static TheoryData<string[], string[], string?> Cases => new()
    {
        // The reported case: npm's shim earlier on PATH, a forgotten .exe later. The shim wins.
        { ["npm", "old"], [Path.Combine("npm", "claude.cmd"), Path.Combine("old", "claude.exe")],
            Path.Combine("npm", "claude.cmd") },
        // Within one directory the extension order still decides.
        { ["npm"], [Path.Combine("npm", "claude.cmd"), Path.Combine("npm", "claude.exe")],
            Path.Combine("npm", "claude.exe") },
        // npm's extensionless script beside its shim is never what runs.
        { ["npm"], [Path.Combine("npm", "claude"), Path.Combine("npm", "claude.cmd")],
            Path.Combine("npm", "claude.cmd") },
        // An extensionless file earlier on PATH does not beat a runnable one later.
        { ["a", "b"], [Path.Combine("a", "claude"), Path.Combine("b", "claude.exe")],
            Path.Combine("b", "claude.exe") },
        // ...but is still the last resort when nothing else exists.
        { ["a"], [Path.Combine("a", "claude")], Path.Combine("a", "claude") },
        { ["a"], [], null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_bare_name_finds_what_a_shell_would(string[] directories, string[] files, string? expected) =>
        Assert.Equal(expected, Find(PathOf(directories), files));

    [Fact]
    public void One_copy_says_nothing() =>
        Assert.Equal("", AiAgentInstanceViewModel.DuplicatesOf([@"C:\npm\claude.cmd"]));

    [Fact]
    public void Several_copies_are_listed_with_the_one_that_runs_first()
    {
        var note = AiAgentInstanceViewModel.DuplicatesOf([@"C:\npm\claude.cmd", @"C:\winget\claude.exe"]);

        Assert.Contains("2 times", note);
        Assert.Contains(@"→ C:\npm\claude.cmd", note);
        Assert.Contains(@"C:\winget\claude.exe", note);
        Assert.True(note.IndexOf(@"C:\npm", StringComparison.Ordinal)
                    < note.IndexOf(@"C:\winget", StringComparison.Ordinal));
    }
}
