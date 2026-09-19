using mTiles.Services;
using mTiles.Services.Shells;
using Xunit;

namespace mTiles.Tests;

public class DroppedPathTextTests
{
    private static readonly IShellTerminal Bash = new BashTerminal();
    private static readonly IShellTerminal PowerShell = new PowerShellTerminal();

    [Theory]
    [InlineData(new[] { "/tmp/a.png" }, "'/tmp/a.png' ")]
    [InlineData(new[] { "/tmp/b c.png" }, "'/tmp/b c.png' ")]
    [InlineData(new[] { "/tmp/a.png", "/tmp/b c.png" }, "'/tmp/a.png' '/tmp/b c.png' ")]
    [InlineData(new[] { "/tmp/a\u0003b.png" }, "'/tmp/ab.png' ")]
    [InlineData(new[] { "", "  " }, "")]
    [InlineData(new string[0], "")]
    public void A_drop_is_typed_as_paths_the_shell_reads_as_paths(string[] paths, string expected) =>
        Assert.Equal(expected, DroppedPathText.For(paths, Bash));

    /// <summary>A file name is somebody else's text, and a shell reads it: it must arrive as one word.</summary>
    [Theory]
    [InlineData("/tmp/$(id) x.png")]
    [InlineData("/tmp/a;rm-rf.png")]
    [InlineData("/tmp/a&b.png")]
    [InlineData("/tmp/it's here.png")]
    [InlineData("/tmp/`id`.png")]
    public void A_shell_metacharacter_in_a_name_is_quoted_and_not_run(string path)
    {
        var bash = DroppedPathText.For([path], Bash);
        Assert.StartsWith("'", bash);
        Assert.EndsWith("' ", bash);
        // Every quote in the name is escaped, so nothing in it can close the quoting and start a command.
        Assert.Equal(path.Replace("'", "'\\''"), bash[1..^2]);
    }

    [Fact]
    public void Each_shell_quotes_the_way_it_reads()
    {
        Assert.Equal(@"'C:\Users\Jan Kowalski\a.png' ",
            DroppedPathText.For([@"C:\Users\Jan Kowalski\a.png"], PowerShell));
        Assert.Equal("'/tmp/it''s here.png' ", DroppedPathText.For(["/tmp/it's here.png"], PowerShell));
    }

    /// <summary>What an AI CLI's prompt is handed: nothing there strips a shell's quotes, so a path is
    /// quoted only where a space would otherwise split it — Windows Terminal's own rule.</summary>
    [Theory]
    [InlineData(new[] { @"C:\Users\x\shot.png" }, @"C:\Users\x\shot.png ")]
    [InlineData(new[] { @"C:\Users\Jan Kowalski\shot.png" }, @"""C:\Users\Jan Kowalski\shot.png"" ")]
    [InlineData(new[] { "/tmp/a;b.png" }, "/tmp/a;b.png ")]
    [InlineData(new string[0], "")]
    public void A_prompt_is_handed_a_path_and_not_a_shell_word(string[] paths, string expected) =>
        Assert.Equal(expected, DroppedPathText.ForPrompt(paths));

    /// <summary>A quote in the name cannot close the one we opened around it.</summary>
    [Fact]
    public void A_quote_in_a_name_is_escaped_rather_than_dropped() =>
        Assert.Equal("\"/tmp/a\\\" b.png\" ", DroppedPathText.ForPrompt(["/tmp/a\" b.png"]));

    /// <summary>A Windows path is made of backslashes, and doubling them spells a different path.</summary>
    [Fact]
    public void A_windows_path_with_a_space_keeps_its_backslashes() =>
        Assert.Equal(@"""C:\a b\shot.png"" ", DroppedPathText.ForPrompt([@"C:\a b\shot.png"]));
}
