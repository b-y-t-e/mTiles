using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
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

    // ---- which shell a chain's commands are given to ---------------------------

    private static ShellProfile ShellOf(ShellType type, string name) =>
        new() { Name = name, ExecutablePath = name, Type = type };

    private static readonly ShellProfile GitBash = ShellOf(ShellType.Bash, "Git Bash");
    private static readonly ShellProfile PowerShell = ShellOf(ShellType.PowerShell, "PowerShell");
    private static readonly ShellProfile Cmd = ShellOf(ShellType.Cmd, "CMD");

    /// <summary>
    /// A profile's own shell runs its commands, and this is the ordinary case — nothing is second-guessed
    /// for the sake of it.
    /// </summary>
    [Theory]
    [InlineData(ShellType.PowerShell)]
    [InlineData(ShellType.Bash)]
    [InlineData(ShellType.Zsh)]
    [InlineData(ShellType.Fish)]
    [InlineData(ShellType.Other)]
    public void A_chains_commands_run_in_the_shell_the_profile_names(ShellType type)
    {
        var shell = ShellOf(type, "chosen");

        Assert.Same(shell, ShellDetector.ResolveForCommands(shell, [PowerShell, GitBash, Cmd]));
    }

    /// <summary>
    /// <c>cmd</c> is the exception, because it cannot run what these profiles are made of: it does not
    /// treat <c>;</c> as a separator, it runs the first line of a multi-line command only, and it parses
    /// quotes by rules the PTY backend does not write. All measured — and the first of them is what
    /// reduced the seeded OpenCode profile to a bare shell, its fallback being two commands in one.
    /// </summary>
    [Fact]
    public void A_cmd_profile_has_its_commands_run_by_powershell_instead()
        => Assert.Same(PowerShell, ShellDetector.ResolveForCommands(Cmd, [GitBash, PowerShell, Cmd]));

    [Fact]
    public void Without_powershell_a_posix_shell_takes_the_commands()
        => Assert.Same(GitBash, ShellDetector.ResolveForCommands(Cmd, [Cmd, GitBash]));

    /// <summary>A shell whose flag mapping is only guessed at (<c>-c</c>) still beats the one measured to
    /// mishandle every command it is handed.</summary>
    [Fact]
    public void Any_other_shell_is_preferred_to_cmd()
    {
        var unknown = ShellOf(ShellType.Other, "something");

        Assert.Same(unknown, ShellDetector.ResolveForCommands(Cmd, [Cmd, unknown]));
    }

    /// <summary>With nothing else installed the chain stays on <c>cmd</c> rather than not running: the
    /// limits are real but partial, and a tile that launches nothing teaches nobody anything.</summary>
    [Fact]
    public void With_nothing_else_installed_the_chain_stays_on_cmd()
        => Assert.Same(Cmd, ShellDetector.ResolveForCommands(Cmd, [Cmd]));

    /// <summary>
    /// And the profile editor says so, because the substitution overrules a setting the user made and a
    /// line in a log file is not where they will find that out. Only for a profile that runs commands:
    /// without a fallback the tile starts its shell interactively, <c>cmd</c> is left alone, and the
    /// notice would be untrue.
    /// </summary>
    [Theory]
    [InlineData(ShellType.Cmd, "claude", "claude -r", true)]
    [InlineData(ShellType.Cmd, "claude", null, false)]        // no chain: an interactive cmd, untouched
    [InlineData(ShellType.Cmd, "claude", "  ", false)]        // a blank fallback is no fallback
    [InlineData(ShellType.PowerShell, "claude", "claude -r", false)]
    [InlineData(ShellType.Bash, "claude", "claude -r", false)]
    public void The_profile_editor_says_when_a_profiles_commands_will_run_elsewhere(
        ShellType type, string? startup, string? fallback, bool expected)
        => Assert.Equal(expected, SettingsViewModel.CommandsRunElsewhere(
            LaunchScripts.FromProfile(startup, fallback), type));

    /// <summary>The seeded OpenCode fallback is two commands joined by <c>;</c>, which is exactly what
    /// cmd does not understand — so this is the case the substitution exists for, end to end.</summary>
    [Fact]
    public void The_opencode_fallback_reaches_a_shell_that_understands_it()
    {
        var command = TileScript.Resolve(
            "opencode import \"${opencodeSessionFile}\" ; opencode --session ses_${tileId}", TileId);

        var (executable, args) = ShellCommandLine.For(
            ShellDetector.ResolveForCommands(Cmd, [PowerShell, Cmd]), command);

        Assert.Equal("PowerShell", executable);
        Assert.Equal("-Command", args[0]);
        Assert.Contains(" ; opencode --session", args[1]);
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
