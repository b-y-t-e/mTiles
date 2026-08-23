using System.Diagnostics;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// How a prompt reaches the tool. Untested until now, and it is where the two failures a user cannot
/// recover from live: a prompt too long for the command line, and a tool handed flags meant for a
/// different one.
/// </summary>
public class AiProcessRunnerTests
{
    [Fact]
    public void An_unknown_tool_gets_its_prompt_as_an_argument_and_claims_nothing_about_stdin()
    {
        // The fallback used to be ClaudeToolRunner, which was survivable while everything went on the
        // command line and became a hang when Claude moved to stdin: a custom tool was launched with
        // Claude's flags, no prompt anywhere on its command line, and a pipe it never agreed to read.
        var runner = AiProcessRunner.GetRunner("some-tool-nobody-here-knows");

        Assert.IsType<GenericToolRunner>(runner);
        Assert.False(runner.AcceptsPromptOnStdin);

        var psi = new ProcessStartInfo();
        runner.ConfigureProcess(psi, "the prompt", maxTurns: 20, streaming: false);

        Assert.Contains("the prompt", psi.ArgumentList);
    }

    [Fact]
    public void Claude_leaves_the_prompt_off_the_command_line_because_it_reads_stdin()
    {
        var psi = new ProcessStartInfo();
        new ClaudeToolRunner().ConfigureProcess(psi, "the prompt", maxTurns: 20, streaming: false);

        Assert.DoesNotContain("the prompt", psi.ArgumentList);
        Assert.Contains("-p", psi.ArgumentList);
    }

    [Fact]
    public void A_prompt_too_long_for_the_command_line_is_refused_by_length_not_by_Process_Start()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Through a .cmd shim — what npm installs, and what AiToolDetector looks for first — the limit
        // is 8 191, not 32 767. Over it Process.Start throws a Win32Exception whose text says nothing
        // about length, so the tile reported that the tool had failed and offered to try again, which
        // could only fail identically.
        var ex = Assert.Throws<InvalidOperationException>(() => Run("tool.cmd", new string('x', 10_000)));

        Assert.Contains("command line", ex.Message);
        Assert.Contains(".cmd shim", ex.Message);
    }

    [Fact]
    public void A_prompt_that_fits_a_real_executable_is_not_refused_for_a_shim_it_is_not_going_through()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 10 000 is over cmd.exe's limit and well under CreateProcess's. The distinction has to be made
        // or every prompt of any size is refused on the strength of the tighter of the two.
        var ex = Record.Exception(() => Run("tool.exe", new string('x', 10_000)));

        Assert.IsNotType<InvalidOperationException>(ex);
    }

    [Fact]
    public void The_length_that_counts_is_the_quoted_one()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A prompt of code is full of quotes and backslashes, every one of which is escaped on the way
        // onto the command line. Measuring the raw string let prompts through that then threw.
        var justUnder = new string('"', 4_400);

        var ex = Assert.Throws<InvalidOperationException>(() => Run("tool.cmd", justUnder));

        Assert.Contains("once quoted", ex.Message);
    }

    [Fact]
    public void A_tool_that_reads_stdin_is_not_measured_at_all()
    {
        // The whole point of stdin: there is no command line to overflow.
        //
        // Not "claude.cmd": on a machine with Claude Code installed from npm that name resolves, and
        // this test would launch the real thing with a 200 KB prompt and wait for it.
        var ex = Record.Exception(() =>
            AiProcessRunner.RunPlainAsync("mtiles-no-such-tool.cmd", new string('x', 200_000), ".",
                    new ClaudeToolRunner())
                .GetAwaiter().GetResult());

        Assert.IsNotType<InvalidOperationException>(ex);
    }

    /// <summary>Starts a run and lets the guard throw before anything is launched. Whatever happens
    /// after that — no such executable — is not what these are asking about.</summary>
    private static void Run(string executable, string prompt) =>
        AiProcessRunner.RunPlainAsync(executable, prompt, ".", new GenericToolRunner())
            .GetAwaiter().GetResult();
}
