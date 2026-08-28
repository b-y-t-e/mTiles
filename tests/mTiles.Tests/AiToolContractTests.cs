using System.Diagnostics;
using mTiles.Models;
using mTiles.Services;
using Xunit;

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
    private static List<string> Args(string binary, AiPermissionMode permission = AiPermissionMode.Auto,
        AiEffort effort = AiEffort.High)
    {
        var psi = new ProcessStartInfo("x");
        AiProcessRunner.GetRunner(binary).ConfigureProcess(psi, "the prompt", streaming: false, permission, effort);
        return [..psi.ArgumentList];
    }

    /// <summary>
    /// Antigravity is given <c>--print</c>, and that is not a preference.
    /// </summary>
    /// <remarks>
    /// Measured: <c>agy --print &lt;prompt&gt;</c> answers on stdout and exits 0, while the same prompt
    /// as a bare positional opens the interactive session and never returns. Before this runner existed
    /// Antigravity fell to <see cref="GenericToolRunner"/>, which passes exactly that bare positional —
    /// so a Goal run on it hung for ever, on a path that deliberately has no wall-clock timeout.
    /// </remarks>
    [Fact]
    public void Antigravity_is_run_in_print_mode_rather_than_interactively()
    {
        Assert.Equal(["--print", "the prompt"], Args("agy"));

        // The only permission control it has. The finer modes pass nothing rather than being rounded up
        // to it: asking for "auto" and being given "nothing is asked about at all" is the one direction
        // this must never round.
        Assert.Equal(["--dangerously-skip-permissions", "--print", "the prompt"],
            Args("agy", AiPermissionMode.BypassPermissions));
        Assert.DoesNotContain("--dangerously-skip-permissions", Args("agy", AiPermissionMode.AcceptEdits));

        // No effort flag: Antigravity spends effort through the model name (gemini-3.7-flash-high), so
        // a level here would have to rewrite the user's configured model.
        Assert.DoesNotContain("--effort", Args("agy", AiPermissionMode.Auto, AiEffort.Max));
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
            Args("pi", AiPermissionMode.Auto, AiEffort.Max));

        // tool default passes no flag at all, here as everywhere.
        Assert.Equal(["-p", "the prompt", "--mode", "text"],
            Args("pi", AiPermissionMode.Auto, AiEffort.ToolDefault));
    }

    /// <summary>The two that take the prompt as a positional after a subcommand, and have no flag for
    /// either setting — so neither is silently mapped to something adjacent.</summary>
    [Fact]
    public void Opencode_and_codex_take_the_prompt_after_their_subcommand()
    {
        Assert.Equal(["run", "the prompt"], Args("opencode"));
        Assert.Equal(["exec", "the prompt"], Args("codex"));
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

    /// <summary>
    /// The number of built-in tools is the number <c>CLAUDE.md</c> claims.
    /// </summary>
    /// <remarks>
    /// A count in prose is a fact that goes stale the first time an entry is added or removed, silently
    /// — removing Open Claude left the index saying eighteen. Pinned rather than trusted, and pinned
    /// against the document rather than against a literal, so the failure names both halves and says
    /// which one to change.
    /// </remarks>
    [Fact]
    public void The_documented_number_of_built_in_tools_is_the_real_one()
    {
        var tools = AiToolDetector.Detect([], []).Count(t => !t.IsUserDefined);

        var claudeMd = File.ReadAllText(Path.Combine(RepositoryRoot(), "CLAUDE.md"));

        Assert.Contains($"built-in list of {tools} tools", claudeMd, StringComparison.Ordinal);
    }

    /// <summary>The repository root, from this file's own compile-time path.</summary>
    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}