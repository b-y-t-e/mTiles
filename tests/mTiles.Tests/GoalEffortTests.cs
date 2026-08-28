using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The effort flag, which is somebody else's CLI contract stated in one place.
/// </summary>
public class GoalEffortTests
{
    /// <summary>
    /// The spellings are what `claude --effort` accepts, and the default is the tile's own opinion
    /// rather than the tool's.
    /// </summary>
    /// <remarks>
    /// High rather than whatever the tool would choose, because a goal run is left alone for an hour
    /// and the budget is in attempts: an attempt spent on a shallow answer costs exactly as much of it
    /// as a careful one. The tool's own default is tuned for interactive use, where a person is
    /// watching and can redirect.
    /// </remarks>
    [Fact]
    public void The_levels_are_the_ones_the_tool_accepts_and_the_default_is_high()
    {
        Assert.Equal(AiEffort.High, new AppSettings().GoalEffort);

        // Measured against `claude --effort`: low, medium, high, xhigh, max.
        Assert.Equal(["low", "medium", "high", "xhigh", "max"],
            AiEfforts.All.Select(AiEfforts.Flag).Where(f => f != null));

        // One level passes no flag at all, which is the way out on a Claude Code older than the option.
        Assert.Null(AiEfforts.Flag(AiEffort.ToolDefault));
        Assert.Equal("tool default", AiEfforts.Label(AiEffort.ToolDefault));

        // A label round-trips, and an unrecognised one is the default rather than an exception while a
        // tile is being built.
        foreach (var effort in AiEfforts.All)
            Assert.Equal(effort, AiEfforts.FromLabel(AiEfforts.Label(effort)));
        Assert.Equal(AiEffort.High, AiEfforts.FromLabel("something else entirely"));
    }

    /// <summary>
    /// A tool refusing the flag is told apart from a tool refusing the work.
    /// </summary>
    /// <remarks>
    /// Measured, and the two cases are not alike: an unknown <em>value</em> is forgiving — the tool
    /// warns and carries on with its own default — while an unknown <em>flag</em> stops the run dead.
    /// So on a Claude Code older than the option, every goal fails, on the default setting, over a flag
    /// the user never typed and cannot see. This is what turns that into a sentence naming the way out.
    /// </remarks>
    [Fact]
    public void A_tool_too_old_for_the_flag_is_recognised_rather_than_blamed_on_the_work()
    {
        Assert.True(AiEfforts.LooksLikeRejectedEffort("error: unknown option '--effort'", "--effort", "--permission-mode"));
        Assert.True(AiEfforts.LooksLikeRejectedEffort("Usage: claude [options]\n  --effort <level>", "--effort", "--permission-mode"));

        // A warning about the value is not a rejection of the flag: the run carried on and produced an
        // answer, and calling that a configuration problem would send the user to fix what works.
        Assert.False(AiEfforts.LooksLikeRejectedEffort(
            "Warning: Unknown --effort value 'bogus' — ignoring it and using the default effort.", "--effort", "--permission-mode"));

        // The flag's name alone is not enough either. It appears in plenty of output that is about the
        // work, and a false positive tells somebody their settings are wrong when they are not.
        Assert.False(AiEfforts.LooksLikeRejectedEffort("I set --effort in the script as you asked.", "--effort", "--permission-mode"));
        Assert.False(AiEfforts.LooksLikeRejectedEffort("error: unknown option '--permission-mode'", "--effort", "--permission-mode"));
        Assert.False(AiEfforts.LooksLikeRejectedEffort(null, "--effort", "--permission-mode"));
    }

    /// <summary>
    /// The flag a tool refused is the flag <em>this run</em> was given.
    /// </summary>
    /// <remarks>
    /// <para>The spelling belongs to the tool: Claude Code calls it <c>--effort</c> and pi calls the
    /// same idea <c>--thinking</c>. Held as a constant, the matcher recognised Claude Code's refusal
    /// and left pi's as a bare "the AI tool reported a failure" — over a usage message about a flag the
    /// user never typed, on every run, at the default setting.</para>
    /// <para>And it is asked <b>for the settings this run used</b>, because every runner adds its flags
    /// conditionally. Told a tool's flag unconditionally, the weaker rule — a usage message naming the
    /// flag — fires on a flag that was never on the command line.</para>
    /// </remarks>
    [Fact]
    public void Each_tool_is_asked_about_its_own_flag()
    {
        var pi = AiProcessRunner.GetRunner("pi");
        var claude = AiProcessRunner.GetRunner("claude");

        const string refusedThinking = "error: unknown option '--thinking'";

        Assert.True(AiEfforts.LooksLikeRejectedEffort(refusedThinking,
            pi.EffortFlagFor(AiEffort.High), pi.PermissionFlagFor(AiPermissionMode.Auto)));

        // The same output against the tool that was never given that flag says nothing.
        Assert.False(AiEfforts.LooksLikeRejectedEffort(refusedThinking,
            claude.EffortFlagFor(AiEffort.High), claude.PermissionFlagFor(AiPermissionMode.Auto)));
    }

    /// <summary>
    /// A flag the run did not pass cannot have been refused by it.
    /// </summary>
    /// <remarks>
    /// Antigravity passes <c>--dangerously-skip-permissions</c> only on bypass and has no effort flag
    /// at all — so on <em>Auto</em> there is nothing of ours on its command line. With the flag named
    /// unconditionally, and no second flag to disambiguate against, every failure of <c>agy</c> that
    /// printed its usage was read as a refusal of a flag that was never passed, and the user was told
    /// to stop passing it.
    /// </remarks>
    [Fact]
    public void A_flag_the_run_did_not_pass_is_not_reported_as_refused()
    {
        var agy = AiProcessRunner.GetRunner("agy");

        var usage = string.Join(Environment.NewLine,
            "error: something went wrong",
            "usage: agy [options]",
            "  --dangerously-skip-permissions");

        Assert.Null(agy.PermissionFlagFor(AiPermissionMode.Auto));
        Assert.False(AiPermissionModes.LooksLikeRejectedMode(usage,
            agy.PermissionFlagFor(AiPermissionMode.Auto), agy.EffortFlagFor(AiEffort.High)));

        // On bypass the flag really is passed — and a usage dump is *still* not evidence it was
        // refused. agy has no second flag, so "the usage names this flag alone" is unanswerable: its
        // usage names every flag it has, and it prints usage for any bad argument at all. A tool with
        // one flag would otherwise blame that flag for every failure it ever had.
        Assert.Equal("--dangerously-skip-permissions",
            agy.PermissionFlagFor(AiPermissionMode.BypassPermissions));
        Assert.False(AiPermissionModes.LooksLikeRejectedMode(usage,
            agy.PermissionFlagFor(AiPermissionMode.BypassPermissions),
            agy.EffortFlagFor(AiEffort.High)));

        // An error line naming the flag is a different matter: that is the tool saying so itself.
        Assert.True(AiPermissionModes.LooksLikeRejectedMode(
            "error: unknown option '--dangerously-skip-permissions'",
            agy.PermissionFlagFor(AiPermissionMode.BypassPermissions),
            agy.EffortFlagFor(AiEffort.High)));
    }

    /// <summary>The flags a runner names are the ones it actually puts on the command line.</summary>
    /// <remarks>
    /// Two statements of the same fact — what <c>ConfigureProcess</c> adds and what the runner reports
    /// — and nothing but this keeps them in step. Their disagreeing is silent: the run works, and only
    /// the explanation of its failure is wrong.
    /// </remarks>
    [Theory]
    [InlineData("claude", AiPermissionMode.Auto)]
    [InlineData("claude", AiPermissionMode.BypassPermissions)]
    [InlineData("pi", AiPermissionMode.Auto)]
    [InlineData("agy", AiPermissionMode.Auto)]
    [InlineData("agy", AiPermissionMode.BypassPermissions)]
    public void A_runner_names_exactly_the_flags_it_passes(string binary, AiPermissionMode permission)
    {
        var runner = AiProcessRunner.GetRunner(binary);

        var psi = new System.Diagnostics.ProcessStartInfo("x");
        runner.ConfigureProcess(psi, "the prompt", streaming: false, permission, AiEffort.High);

        Assert.Equal(psi.ArgumentList.Contains("--effort") || psi.ArgumentList.Contains("--thinking"),
            runner.EffortFlagFor(AiEffort.High) is not null);

        Assert.Equal(
            psi.ArgumentList.Contains("--permission-mode")
            || psi.ArgumentList.Contains("--dangerously-skip-permissions"),
            runner.PermissionFlagFor(permission) is not null);
    }

    /// <summary>
    /// A tool with one flag is never blamed on the strength of a usage message alone.
    /// </summary>
    /// <remarks>
    /// The weaker rule reads a usage dump as a refusal when it names this flag and not the other one.
    /// With no other one, that test is vacuously true and the rule fires on every failure the tool
    /// prints usage for — which for <c>pi</c> is a bad argument, a missing key or a crash, each
    /// answered with "change your Effort setting". Withdrawing the rule costs the diagnosis for a tool
    /// that refuses silently; keeping it costs a confident wrong answer on every other failure.
    /// </remarks>
    [Fact]
    public void One_flag_alone_is_not_enough_to_read_a_usage_message()
    {
        var pi = AiProcessRunner.GetRunner("pi");

        var usage = string.Join(Environment.NewLine,
            "error: missing API key",
            "usage: pi [options]",
            "  --thinking <level>");

        Assert.Null(pi.PermissionFlagFor(AiPermissionMode.Auto));
        Assert.False(AiEfforts.LooksLikeRejectedEffort(usage,
            pi.EffortFlagFor(AiEffort.High), pi.PermissionFlagFor(AiPermissionMode.Auto)));

        // Said outright, it still counts.
        Assert.True(AiEfforts.LooksLikeRejectedEffort("error: unknown option '--thinking'",
            pi.EffortFlagFor(AiEffort.High), pi.PermissionFlagFor(AiPermissionMode.Auto)));
    }
}