using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The three pure functions between a user's shell profile and what actually reaches a shell. They are
/// a contract with people who wrote their profiles by hand: <c>${tileId}</c> resolves, a multi-line
/// script arrives as separate commands, and a command is wrapped for the right shell.
/// </summary>
public class ScriptContractTests
{
    // ---- startup script → lines typed into the shell ---------------------------

    [Fact]
    public void Each_line_of_a_startup_script_becomes_one_command()
    {
        var lines = ShellStarter.BuildStartupInput("cd src\nnpm run dev", "t-1");

        Assert.Equal(["cd src\r", "npm run dev\r"], lines);
    }

    [Fact]
    public void The_tile_id_is_substituted_everywhere_it_appears()
    {
        var lines = ShellStarter.BuildStartupInput("claude --session ${tileId}\necho ${tileId}", "abc-123");

        Assert.Equal(["claude --session abc-123\r", "echo abc-123\r"], lines);
    }

    [Fact]
    public void Windows_line_endings_do_not_leave_a_stray_carriage_return()
    {
        // A profile edited on Windows carries CRLF; a doubled CR would submit an extra empty line.
        var lines = ShellStarter.BuildStartupInput("first\r\nsecond\r\n", "t-1");

        Assert.Equal(["first\r", "second\r"], lines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void A_script_with_nothing_in_it_is_no_script_at_all(string? script)
        => Assert.Null(ShellStarter.BuildStartupInput(script, "t-1"));

    // ---- profile scripts → the chain's commands --------------------------------

    [Fact]
    public void The_chain_runs_the_startup_command_before_the_fallback()
    {
        var commands = DirectLaunchSession.BuildCommands("claude --continue", "claude", "t-1");

        Assert.Equal(["claude --continue", "claude"], commands);
    }

    [Fact]
    public void A_profile_with_only_a_fallback_still_has_a_command_to_run()
        => Assert.Equal(["claude"], DirectLaunchSession.BuildCommands(null, "claude", "t-1"));

    [Fact]
    public void The_chain_substitutes_the_tile_id_in_both_scripts()
    {
        var commands = DirectLaunchSession.BuildCommands("claude -r ${tileId}", "claude ${tileId}", "abc-123");

        Assert.Equal(["claude -r abc-123", "claude abc-123"], commands);
    }

    // ---- a command → the shell that runs it ------------------------------------

    [Theory]
    [InlineData(ShellType.Cmd, "/c")]
    [InlineData(ShellType.PowerShell, "-Command")]
    [InlineData(ShellType.Bash, "-c")]
    [InlineData(ShellType.Zsh, "-c")]
    [InlineData(ShellType.Other, "-c")]
    public void A_command_is_wrapped_with_the_flag_its_shell_understands(ShellType type, string flag)
    {
        var profile = new ShellProfile { ExecutablePath = "shell", Type = type };

        var (executable, args) = profile.CommandLine("echo hi");

        Assert.Equal("shell", executable);
        Assert.Equal([flag, "echo hi"], args);
    }

    /// <summary>The profile's own arguments are the interactive-startup flags (<c>--login -i</c>), and
    /// this is the non-interactive form — <c>-i</c> with <c>-c</c> asks for both at once.</summary>
    [Fact]
    public void Wrapping_a_command_leaves_the_interactive_startup_flags_out()
    {
        var profile = new ShellProfile { ExecutablePath = "bash", Args = ["--login", "-i"], Type = ShellType.Bash };

        var (_, args) = profile.CommandLine("echo hi");

        Assert.Equal(["-c", "echo hi"], args);
    }
}
