using System.Diagnostics;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using Xunit;
using static mTiles.Tests.TestUsage;

namespace mTiles.Tests;

/// <summary>
/// What each supported CLI is actually launched with.
/// </summary>
/// <remarks>
/// These are claims about somebody else's contract, measured from each tool's own <c>--help</c> and
/// pinned here so a change to one is a failing build rather than a Goal tile that quietly stops
/// working. The interesting one is Antigravity, where getting it wrong does not fail — it hangs.
/// </remarks>
public class AiToolContractTests
{
    private static List<string> Args(string binary, AiBehaviour permission = AiBehaviour.Auto,
        AiEffort effort = AiEffort.High, string model = "")
    {
        var psi = new ProcessStartInfo("x");
        AiProcessRunner.GetRunner(binary).ConfigureProcess(psi, "the prompt", streaming: false, Implementing, permission, effort, model);
        return [..psi.ArgumentList];
    }

    /// <summary>
    /// A headless run is told the instance's model too, not only the interactive tile.
    /// </summary>
    /// <remarks>The Goal tile runs the agent through <c>AiProcessRunner</c>, which took the instance
    /// and dropped its model — so a goal ran on the CLI's default model while the environment pointed
    /// the CLI at a provider that usually serves a different one.</remarks>
    [Theory]
    [InlineData("opencode")]
    [InlineData("codex")]
    [InlineData("pi")]
    [InlineData("agy")]
    public void The_model_reaches_a_headless_run(string binary)
    {
        var args = Args(binary, model: "some/model");

        Assert.Contains("--model", args);
        Assert.Contains("some/model", args);
    }

    /// <summary>No model asked for, nothing passed — an empty model is "whatever the agent picks", and
    /// a bare <c>--model</c> with nothing after it is a usage error on every one of them.</summary>
    [Theory]
    [InlineData("opencode")]
    [InlineData("codex")]
    [InlineData("pi")]
    [InlineData("agy")]
    public void No_model_asked_for_is_no_flag_at_all(string binary) =>
        Assert.DoesNotContain("--model", Args(binary));

    /// <summary>
    /// Antigravity is given <c>--print</c>, and that is not a preference.
    /// </summary>
    /// <remarks>
    /// Measured: <c>agy --print &lt;prompt&gt;</c> answers on stdout and exits 0, while the same prompt
    /// as a bare positional opens the interactive session and never returns. Before this runner existed
    /// Antigravity fell to <see cref="GenericAgent"/>, which passes exactly that bare positional —
    /// so a Goal run on it hung for ever, on a path that deliberately has no wall-clock timeout.
    /// </remarks>
    [Fact]
    public void Antigravity_is_run_in_print_mode_rather_than_interactively()
    {
        // `--mode accept-edits` is agy's only working mode, so canonical auto maps to it. That is a
        // round-down inside the mapping — asking for auto gets something weaker — and never the other
        // way, which is the one direction this must not go.
        Assert.Equal(["--mode", "accept-edits", "--effort", "high", "--print", "the prompt"],
            Args("agy"));

        Assert.Equal(["--dangerously-skip-permissions", "--effort", "high", "--print", "the prompt"],
            Args("agy", AiBehaviour.BypassPermissions));
        Assert.DoesNotContain("--dangerously-skip-permissions", Args("agy", AiBehaviour.AcceptEdits));

        // It does have an effort flag — measured, `--effort low|medium|high` — which this application
        // used to say it did not, so every agy run went out at whatever the model name implied. Three
        // levels, so the canonical top two round down rather than being passed and rejected.
        Assert.Equal(["--effort", "high"],
            new AntigravityAgent().EffortArgs(AiEffort.Max, Implementing));
        Assert.Empty(new AntigravityAgent().EffortArgs(AiEffort.ToolDefault, Implementing));
    }

    /// <summary>
    /// pi understands the tile's effort levels under its own name for them.
    /// </summary>
    /// <remarks>
    /// Measured from <c>pi --help</c>: <c>--thinking off|minimal|low|medium|high|xhigh|max</c>. Every
    /// level <see cref="AiEffort"/> names exists there under the same word, so the setting means the
    /// same thing here as it does for Claude Code instead of being silently ignored.
    /// </remarks>
    [Fact]
    public void Pi_is_given_the_thinking_level_the_tile_asked_for()
    {
        Assert.Equal(["-p", "the prompt", "--mode", "text", "--thinking", "high"], Args("pi"));
        Assert.Equal(["-p", "the prompt", "--mode", "text", "--thinking", "max"],
            Args("pi", AiBehaviour.Auto, AiEffort.Max));

        // tool default passes no flag at all, here as everywhere.
        Assert.Equal(["-p", "the prompt", "--mode", "text"],
            Args("pi", AiBehaviour.Auto, AiEffort.ToolDefault));
    }

    /// <summary>
    /// The two that take the prompt as a positional, after their subcommand and after their flags.
    /// </summary>
    /// <remarks>
    /// <para>opencode's control is a <em>boolean</em> and its name is a trap: <c>--auto</c> is
    /// documented as "auto-approve permissions that are not explicitly denied (dangerous!)", which is
    /// this application's bypass. Canonical auto therefore passes nothing, and asking for bypass is the
    /// only thing that puts the flag on the line.</para>
    /// <para>codex has two orthogonal axes and no single flag, and its effort is a <em>config key</em>
    /// — <c>-c model_reasoning_effort=high</c>. Both were passing nothing at all, so every goal run on
    /// codex used whatever the user's <c>config.toml</c> happened to say. Only one of the two axes
    /// reaches <c>exec</c>, which refuses <c>-a</c> outright.</para>
    /// </remarks>
    [Fact]
    public void Opencode_leaves_the_prompt_to_stdin_and_codex_takes_it_after_its_subcommand()
    {
        // Measured 2026-09-01: `opencode run` with no message argument answers the prompt it is
        // piped, so the prompt stays off the command line — which is what removes the npm `.cmd`
        // shim's re-parsing of it, and the ~8 000 characters past which cmd.exe refuses the line.
        Assert.Equal(["run"], Args("opencode"));
        Assert.Equal(["run", "--auto"], Args("opencode", AiBehaviour.BypassPermissions));

        // No -a: measured, codex exec refuses it — the axis exists on codex and codex resume alone.
        Assert.Equal(
            ["exec", "--sandbox", "workspace-write", "-c", "model_reasoning_effort=high", "the prompt"],
            Args("codex"));
        Assert.Equal(["exec", "--dangerously-bypass-approvals-and-sandbox", "the prompt"],
            Args("codex", AiBehaviour.BypassPermissions, AiEffort.ToolDefault));
    }

    /// <summary>
    /// A binary nothing here knows still gets its prompt, and gets it the one way every CLI accepts.
    /// </summary>
    [Fact]
    public void An_unknown_tool_falls_back_to_the_prompt_as_a_plain_argument()
    {
        Assert.Equal(["the prompt"], Args("something-nobody-has-heard-of"));

        // And openclaude is now one of those: it was a Claude Code fork whose support was removed, so
        // it must not still be answered with Claude's own flags and standard input.
        Assert.Equal(["the prompt"], Args("openclaude"));
        Assert.False(AiProcessRunner.GetRunner("openclaude").AcceptsPromptOnStdin);
    }

}