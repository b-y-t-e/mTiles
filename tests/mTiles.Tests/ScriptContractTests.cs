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
    /// <summary>A real GUID, because that is what a tile id is and what `TileScript.Resolve` insists on:
    /// the value is substituted into a string handed to `shell -c`, so an id shaped like anything else
    /// is a command rather than an identifier.</summary>
    private const string TileId = "1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed";

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
        var lines = ShellStarter.BuildStartupInput("claude --session ${tileId}\necho ${tileId}", TileId);

        Assert.Equal([$"claude --session {TileId}\r", $"echo {TileId}\r"], lines);
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
        var commands = DirectLaunchSession.BuildCommands(LaunchScripts.FromProfile("claude --continue", "claude"), "t-1");

        Assert.Equal(["claude --continue", "claude"], commands);
    }

    [Fact]
    public void A_profile_with_only_a_fallback_still_has_a_command_to_run()
        => Assert.Equal(["claude"], DirectLaunchSession.BuildCommands(LaunchScripts.FromProfile(null, "claude"), "t-1"));

    [Fact]
    public void The_chain_substitutes_the_tile_id_in_both_scripts()
    {
        var commands = DirectLaunchSession.BuildCommands(LaunchScripts.FromProfile("claude -r ${tileId}", "claude ${tileId}"), TileId);

        Assert.Equal([$"claude -r {TileId}", $"claude {TileId}"], commands);
    }

    /// <summary>
    /// A blank tile id expands <c>${tileId}</c> to nothing, which does not produce a broken command —
    /// it produces a <em>different</em> one. <c>claude -r ${tileId}</c> becomes <c>claude -r</c>, which
    /// may well run and do something else entirely. A missing value must not fail that quietly.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_script_using_the_tile_id_refuses_to_run_without_one(string tileId)
        => Assert.Throws<ArgumentException>(() => TileScript.Resolve("claude -r ${tileId}", tileId));

    [Fact]
    public void A_script_that_never_mentions_the_tile_id_does_not_need_one()
        => Assert.Equal("claude", TileScript.Resolve("claude", ""));

    /// <summary>The id is read from the layout on disk and substituted into a string that is then handed
    /// to <c>shell -c</c>. Anything reaching there is executed, so an id shaped like a command is one.
    /// Every id the app makes is a GUID, so demanding exactly that costs nothing.</summary>
    [Theory]
    [InlineData("x; rm -rf ~")]
    [InlineData("$(whoami)")]
    [InlineData("abc-123")]
    // Every one of these is accepted by plain `Guid.TryParse`, and the first three carry braces,
    // parentheses and commas — a shell reads those as grouping and subshells, which is exactly what
    // this check exists to keep out. Only the plain hyphenated form is what the app ever produces.
    [InlineData("{1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed}")]
    [InlineData("(1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed)")]
    [InlineData("{0x1b9d6bcd,0xbbfd,0x4b2d,{0x9b,0x5d,0xab,0x8d,0xfb,0xbd,0x4b,0xed}}")]
    [InlineData("1b9d6bcdbbfd4b2d9b5dab8dfbbd4bed")]
    public void A_tile_id_that_is_not_a_guid_is_refused_before_it_reaches_a_shell(string tileId)
        => Assert.Throws<ArgumentException>(() => TileScript.Resolve("claude -r ${tileId}", tileId));

    // ---- a user's profile → what the tile will run -----------------------------

    /// <summary>"A profile that has a fallback launches commands" is the rule, and it lived in three
    /// places at once — two of which disagreed about whether a script of spaces counts. That decided
    /// whether a tile ran its command or silently started a bare shell.</summary>
    [Theory]
    [InlineData("claude", "claude --continue", true)]
    [InlineData("claude", null, false)]      // no fallback: an interactive shell with a startup script
    [InlineData(null, "claude", true)]       // fallback only is unusual, but it is still a command to run
    [InlineData("claude", "   ", false)]     // a blank fallback is no fallback
    [InlineData(null, null, false)]
    public void A_profile_launches_commands_exactly_when_it_has_a_fallback(
        string? startup, string? fallback, bool expected)
        => Assert.Equal(expected, LaunchScripts.FromProfile(startup, fallback).RunsCommandChain);

    [Fact]
    public void A_tile_with_no_profile_has_nothing_to_run()
    {
        Assert.Null(LaunchScripts.None.Startup);
        Assert.Null(LaunchScripts.None.Fallback);
        Assert.False(LaunchScripts.None.RunsCommandChain);
    }

    /// <summary><c>with</c> takes the copy constructor, which walks past field initialisers — so the
    /// normalisation has to be in the init setters as well, and this is what says so.</summary>
    [Fact]
    public void Rewriting_a_script_with_a_blank_one_still_reads_as_no_script()
    {
        var scripts = LaunchScripts.FromProfile("claude", "claude --continue") with { Startup = "  " };

        Assert.Null(scripts.Startup);
        Assert.Equal("claude --continue", scripts.Fallback);
    }

    [Fact]
    public void A_blank_script_is_read_as_no_script()
    {
        var scripts = LaunchScripts.FromProfile("  \t ", "\r\n");

        Assert.Null(scripts.Startup);
        Assert.Null(scripts.Fallback);
        Assert.False(scripts.RunsCommandChain);
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

        var (executable, args) = ShellCommandLine.For(profile, "echo hi");

        Assert.Equal("shell", executable);
        Assert.Equal([flag, "echo hi"], args);
    }

    /// <summary>
    /// What <c>cmd.exe</c> actually receives, pinned. It is the one shell that does not parse its
    /// command line by the <c>CommandLineToArgvW</c> rules the PTY backend quotes with — after
    /// <c>/c</c> it applies its own, where a quote is not an escape and <c>&amp; | ^ &lt; &gt; %</c>
    /// keep their meaning. Nothing here works around that; this records the shape so a change to the
    /// mapping cannot happen by accident, and the limitation stays written down next to it.
    /// </summary>
    [Fact]
    public void A_command_for_cmd_is_passed_through_untouched_quirks_and_all()
    {
        var profile = new ShellProfile { ExecutablePath = "cmd.exe", Type = ShellType.Cmd };

        var (executable, args) = ShellCommandLine.For(profile, "claude & echo \"hi\"");

        Assert.Equal("cmd.exe", executable);
        // Verbatim: no quoting is added here, and none can be — cmd would read added quotes literally.
        Assert.Equal(["/c", "claude & echo \"hi\""], args);
    }

    /// <summary>The profile's own arguments are the interactive-startup flags (<c>--login -i</c>), and
    /// this is the non-interactive form — <c>-i</c> with <c>-c</c> asks for both at once.</summary>
    [Fact]
    public void Wrapping_a_command_leaves_the_interactive_startup_flags_out()
    {
        var profile = new ShellProfile { ExecutablePath = "bash", Args = ["--login", "-i"], Type = ShellType.Bash };

        var (_, args) = ShellCommandLine.For(profile, "echo hi");

        Assert.Equal(["-c", "echo hi"], args);
    }
}
