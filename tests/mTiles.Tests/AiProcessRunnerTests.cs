using System.Diagnostics;
using mTiles.Models;
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
    /// <summary>
    /// A runner that records what it was configured with and then stops the run.
    /// </summary>
    /// <remarks>
    /// It throws on purpose. <see cref="AiProcessRunner.RunPlainAsync"/> configures the process and
    /// then starts it, and this test is about the first half only — a real launch would need a real
    /// executable and would make the assertion depend on somebody's PATH. Throwing from
    /// <c>ConfigureProcess</c> ends the call before <c>Process.Start</c> with the values already
    /// captured, which is the whole of what is being asked.
    /// </remarks>
    private sealed class RecordingRunner : IAiToolRunner
    {
        public AiPermissionMode Permission { get; private set; } = (AiPermissionMode)(-1);
        public AiEffort Effort { get; private set; } = (AiEffort)(-1);

        public sealed class Stop : Exception;

        public void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
            AiPermissionMode permission = AiPermissionMode.Auto,
            AiEffort effort = AiEffort.High)
        {
            Permission = permission;
            Effort = effort;
            throw new Stop();
        }

        public IReadOnlyList<AiOutputChunk> ParseLine(string line) => [];
    }

    /// <summary>
    /// What the caller chose reaches the tool — both of it.
    /// </summary>
    /// <remarks>
    /// The one failure the flag-spelling tests below cannot see, and the one that happened: the effort
    /// was accepted by <c>RunPlainAsync</c> and left out of the call to <c>ConfigureProcess</c>, so the
    /// runner used its own default of <see cref="AiEffort.High"/>. Every run went out at high effort
    /// whatever the strip said, and — because "tool default" is the setting that passes no flag at all
    /// — a Claude Code too old for <c>--effort</c> failed every goal with no way to turn it off. Both
    /// parameters are asserted together because both are optional at every hop between here and the
    /// command line, and an optional parameter that nobody passes is invisible in every other test.
    /// </remarks>
    [Fact]
    public async Task The_permission_mode_and_the_effort_both_reach_the_runner()
    {
        var runner = new RecordingRunner();

        await Assert.ThrowsAsync<RecordingRunner.Stop>(() => AiProcessRunner.RunPlainAsync(
            "some-tool", "the prompt", Path.GetTempPath(), runner,
            permission: AiPermissionMode.AcceptEdits,
            effort: AiEffort.Low));

        Assert.Equal(AiPermissionMode.AcceptEdits, runner.Permission);
        Assert.Equal(AiEffort.Low, runner.Effort);
    }

    /// <summary>Each level is spelled the way the CLI spells it, and one of them passes no flag.
    /// </summary>
    /// <remarks>
    /// Measured against Claude Code 2.1.247, which has both <c>--effort &lt;level&gt;</c> and
    /// <c>--permission-mode &lt;mode&gt;</c>. This is somebody else's contract and it has moved once
    /// already; a test that states it is what turns the next move into a red build rather than a tile
    /// that fails every run over a flag the user never typed.
    /// </remarks>
    [Theory]
    [InlineData(AiEffort.Low, "low")]
    [InlineData(AiEffort.Medium, "medium")]
    [InlineData(AiEffort.High, "high")]
    [InlineData(AiEffort.XHigh, "xhigh")]
    [InlineData(AiEffort.Max, "max")]
    public void The_effort_goes_out_as_the_flag_the_tool_knows(AiEffort effort, string expected)
    {
        var psi = new ProcessStartInfo();
        new ClaudeToolRunner().ConfigureProcess(psi, "the prompt", streaming: true, effort: effort);

        var at = psi.ArgumentList.IndexOf("--effort");
        Assert.True(at >= 0, "the effort flag is not on the command line");
        Assert.Equal(expected, psi.ArgumentList[at + 1]);
    }

    [Fact]
    public void The_tools_own_effort_is_asked_for_by_passing_no_flag()
    {
        var psi = new ProcessStartInfo();
        new ClaudeToolRunner().ConfigureProcess(psi, "the prompt", streaming: false,
            effort: AiEffort.ToolDefault);

        Assert.DoesNotContain("--effort", psi.ArgumentList);
    }

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
        runner.ConfigureProcess(psi, "the prompt", streaming: false);

        Assert.Contains("the prompt", psi.ArgumentList);
    }

    [Fact]
    public void Claude_leaves_the_prompt_off_the_command_line_because_it_reads_stdin()
    {
        var psi = new ProcessStartInfo();
        new ClaudeToolRunner().ConfigureProcess(psi, "the prompt", streaming: false);

        Assert.DoesNotContain("the prompt", psi.ArgumentList);
        Assert.Contains("-p", psi.ArgumentList);

        // And it is the only one that opted in. Opting in is a claim about somebody else's CLI, and a
        // tool that does not read stdin sits waiting for input that never arrives.
        // Through the interface: the default lives there, so asking the concrete type would not see it.
        Assert.True(((IAiToolRunner)new ClaudeToolRunner()).AcceptsPromptOnStdin);
        Assert.False(((IAiToolRunner)new CodexToolRunner()).AcceptsPromptOnStdin);
        Assert.False(((IAiToolRunner)new OpenCodeToolRunner()).AcceptsPromptOnStdin);
        Assert.False(((IAiToolRunner)new PiToolRunner()).AcceptsPromptOnStdin);
    }


    [Fact]
    public void Claude_is_given_no_turn_limit_at_all()
    {
        // Twice now a number of mine has cut a real implementation off: 20, raised to 200, and the 200
        // was reached half way through one. Every ceiling here is a guess about how long somebody
        // else's task takes, applied to work already in their files — and being able to *report* the
        // truncation, which the stream does, does not make it less of a truncation.
        //
        // Which leaves one run unbounded, and that is the accepted risk: the attempt budget bounds how
        // many runs a goal gets rather than how long one lasts, RunPlainAsync has no wall-clock timeout
        // on purpose, and Pause is the whole of the stop. Not "the user can set a ceiling in their
        // settings" — measured against Claude Code 2.1.251, maxTurns is a hidden CLI flag, an agent
        // file's front matter and an SDK option, and settings.json has no equivalent. A ceiling that is
        // hit is still read as an error rather than as an answer (see below), for anyone who adds one.
        var psi = new ProcessStartInfo();
        new ClaudeToolRunner().ConfigureProcess(psi, "the prompt", streaming: true);

        Assert.DoesNotContain("--max-turns", psi.ArgumentList);
    }

    [Fact]
    public async Task Running_out_of_turns_is_reported_as_an_error_rather_than_as_an_answer()
    {
        // This tile asks for no ceiling, but the user's own settings can carry one — and read as an
        // answer, a truncated run becomes a plan or an implementation that stops mid-thought and is
        // reviewed as if it were finished.
        var (answer, failed, _) = await Drain(Said,
            """{"type":"result","subtype":"error_max_turns","is_error":true,"result":"turn limit"}""");

        Assert.StartsWith("partial", answer);
        Assert.Contains("[error]", answer);
        Assert.True(failed);
    }

    [Fact]
    public void Streaming_asks_for_json_and_for_the_verbosity_it_requires()
    {
        var psi = new ProcessStartInfo();
        new ClaudeToolRunner().ConfigureProcess(psi, "the prompt", streaming: true);

        Assert.Contains("stream-json", psi.ArgumentList);

        // Not decoration: print mode refuses stream-json without it, so forgetting this is a run that
        // fails before it starts.
        Assert.Contains("--verbose", psi.ArgumentList);

        // And only where it is asked for. The plain path is what the tile used for a year and what the
        // other three tools still use.
        var plain = new ProcessStartInfo();
        new ClaudeToolRunner().ConfigureProcess(plain, "the prompt", streaming: false);
        Assert.Contains("text", plain.ArgumentList);
        Assert.DoesNotContain("--verbose", plain.ArgumentList);
    }

    [Fact]
    public void Only_claude_claims_it_can_say_what_it_is_doing()
    {
        // Like AcceptsPromptOnStdin, this is a claim about somebody else's CLI, so it is opted into per
        // tool by somebody who has checked. A tool wrongly marked as streaming is run with flags it does
        // not understand.
        Assert.True(((IAiToolRunner)new ClaudeToolRunner()).SupportsStreaming);
        Assert.False(((IAiToolRunner)new CodexToolRunner()).SupportsStreaming);
        Assert.False(((IAiToolRunner)new OpenCodeToolRunner()).SupportsStreaming);
        Assert.False(((IAiToolRunner)new PiToolRunner()).SupportsStreaming);
        Assert.False(((IAiToolRunner)new GenericToolRunner()).SupportsStreaming);
    }

    [Theory]
    // The name alone says almost nothing — "Edit" tells you it is editing something — so the one
    // field that says which thing goes with it.
    [InlineData("""{"type":"tool_use","name":"Read","input":{"file_path":"src/Cart.cs"}}""",
        "Read src/Cart.cs")]
    [InlineData("""{"type":"tool_use","name":"Bash","input":{"command":"dotnet build"}}""",
        "Bash dotnet build")]
    [InlineData("""{"type":"tool_use","name":"Skill","input":{"skill":"code-review"}}""",
        "Skill code-review")]
    // No input worth naming is still worth reporting: something is happening.
    [InlineData("""{"type":"tool_use","name":"TodoWrite","input":{}}""", "TodoWrite")]
    public void A_tool_call_is_reported_as_what_it_is_and_what_it_is_about(string block, string expected)
    {
        var line = """{"type":"assistant","message":{"content":[""" + block + """]}}""";

        var chunk = Assert.Single(new ClaudeToolRunner().ParseLine(line));

        Assert.Equal(AiChunkKind.Activity, chunk.Kind);
        Assert.Equal(expected, chunk.Content);
    }

    [Fact]
    public void A_long_subject_keeps_the_end_of_itself()
    {
        // A path is told apart by its last segment, not its first, so this trims from the left.
        var line = """
            {"type":"assistant","message":{"content":[
              {"type":"tool_use","name":"Edit","input":{"file_path":"
            """.Trim() + new string('d', 80) + """
            /Cart.cs"}}]}}
            """.Trim();

        var chunk = Assert.Single(new ClaudeToolRunner().ParseLine(line));

        Assert.EndsWith("Cart.cs", chunk.Content);
        Assert.True(chunk.Content.Length < 60, chunk.Content);
    }

    [Fact]
    public void A_message_that_says_something_and_does_something_reports_both()
    {
        // It used to report the tool call and drop the sentence, on the grounds that the answer comes
        // from the result line anyway. True until the run is interrupted — and that is the one case
        // where what the tool managed to say is the whole of what there is to show for it.
        const string line = """
            {"type":"assistant","message":{"content":[
              {"type":"text","text":"Let me look at the cart."},
              {"type":"tool_use","name":"Read","input":{"file_path":"src/Cart.cs"}}]}}
            """;

        var chunks = new ClaudeToolRunner().ParseLine(line);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(AiChunkKind.Text, chunks[0].Kind);
        Assert.Equal("Let me look at the cart.", chunks[0].Content);
        Assert.Equal(AiChunkKind.Activity, chunks[1].Kind);
        Assert.Equal("Read src/Cart.cs", chunks[1].Content);
    }

    [Fact]
    public async Task An_interrupted_run_keeps_the_prose_from_a_message_that_also_used_a_tool()
    {
        // One line, because the reader splits on newlines: a pretty-printed fixture is a JSON document
        // torn into pieces that parse as nothing, and the test then passes or fails for the wrong reason.
        const string both = """{"type":"assistant","message":{"content":[{"type":"text","text":"Renaming the totals helper."},{"type":"tool_use","name":"Edit","input":{"file_path":"src/Cart.cs"}}]}}""";

        // No result line: the run was killed. What is left is the fallback, and dropping the prose left
        // an attempt with nothing at all to say for itself.
        var (answer, _, doing) = await Drain(both);

        Assert.Equal("Renaming the totals helper.", answer.Trim());
        Assert.Equal(["Edit src/Cart.cs"], doing);
    }

    [Fact]
    public async Task The_tools_own_final_text_is_the_answer()
    {
        var (answer, _, doing) = await Drain(Used, Said, Result);

        // The result line, not this side's reassembly of the pieces — and the pieces are still read,
        // because that is what says which tool call is running right now.
        Assert.Equal("the final answer", answer);
        Assert.Equal(["Read a.cs"], doing);
    }

    [Fact]
    public async Task Without_a_final_line_what_the_tool_managed_to_say_is_kept()
    {
        // A run that was killed part way through has no result line at all, and the text it did produce
        // is better than nothing to show for it.
        var (answer, _, _) = await Drain(Said);

        Assert.Equal("partial", answer.Trim());
    }

    [Fact]
    public async Task An_empty_final_line_does_not_beat_the_text_before_it()
    {
        // What a failed or killed run leaves behind. Taking it anyway threw the answer away in favour
        // of the absence of one — and an empty answer is judged "the tool returned nothing", which
        // pauses the tile over a run that had in fact said something.
        var (answer, _, _) = await Drain(Said, EmptyResult);

        Assert.Equal("partial", answer.Trim());
    }

    [Fact]
    public async Task An_error_is_kept_apart_from_the_answer_and_never_dropped()
    {
        // Labelled and after the answer, not glued into it: the tool's account of its own failure is
        // not a paragraph of what it produced, and inside a half-finished answer it becomes a sentence
        // the review prompt reads as something the implementation decided.
        var (withText, failedWithText, _) = await Drain(Said, Failed);
        Assert.StartsWith("partial", withText);
        Assert.Contains("[error]", withText);

        // And the fact travels beside the words. Without it the loop judges the text alone, and text is
        // exactly what a failed run has — so the failure was adopted as the plan, or as the review.
        Assert.True(failedWithText);

        // And never simply dropped because something else had been printed. That is how a run which
        // stopped half way came back looking like one that finished, with the thing that went wrong
        // — a credit balance, a revoked key — said out loud nowhere at all.
        var (alone, failedAlone, _) = await Drain(Failed);
        Assert.Contains("error", alone, StringComparison.OrdinalIgnoreCase);
        Assert.True(failedAlone);
    }

    /// <summary>Everything the stream reader has to get right, read off a string. It took a
    /// <c>Process</c> before, which is why none of this was asked: stating any of it needed a tool
    /// installed and a run to observe.</summary>
    private static async Task<(string Answer, bool Failed, List<string> Doing)> Drain(
        params string[] lines)
    {
        var doing = new List<string>();
        var output = await AiProcessRunner.ReadStreamAsync(
            new StringReader(string.Join("\n", lines)), new ClaudeToolRunner(), doing.Add);
        return (output.Text, output.Failed, doing);
    }

    private const string Result = """{"type":"result","result":"the final answer"}""";
    private const string EmptyResult = """{"type":"result","result":""}""";
    private const string Said = """{"type":"assistant","message":{"content":[{"type":"text","text":"partial"}]}}""";
    private const string Used = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"a.cs"}}]}}""";
    private const string Failed = """{"type":"result","subtype":"error_response"}""";

    [Fact]
    public async Task A_final_line_that_says_it_failed_is_not_read_as_the_answer()
    {
        // The text is there either way. A result line carrying is_error is the tool saying this is what
        // went wrong, not what it produced — and taken as an answer it becomes a plan, an
        // implementation or a review that nobody wrote, judged and acted on like any other.
        const string failed = """{"type":"result","is_error":true,"result":"Credit balance is too low"}""";

        var (withText, failedWithText, _) = await Drain(Said, failed);
        Assert.StartsWith("partial", withText);
        Assert.Contains("Credit balance is too low", withText);
        Assert.True(failedWithText);

        // Alone it is still the only thing that explains the silence, and it says what actually
        // happened rather than "Claude returned an error".
        var (alone, failedAlone, _) = await Drain(failed);
        Assert.Equal("Credit balance is too low", alone);
        Assert.True(failedAlone);
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

    [Fact]
    public void A_tool_that_reads_stdin_has_no_budget_to_fit_and_the_prompt_is_left_whole()
    {
        Assert.Null(AiProcessRunner.PromptBudget("claude.cmd", new ClaudeToolRunner()));

        var whole = new GoalPromptBuilder().BuildReview("the goal", new string('d', 20_000), budget: null);
        Assert.Contains("dddd", whole);
    }

    // ── Permission mode ─────────────────────────────────

    [Theory]
    [InlineData(AiPermissionMode.Auto, "auto")]
    [InlineData(AiPermissionMode.AcceptEdits, "acceptEdits")]
    [InlineData(AiPermissionMode.BypassPermissions, "bypassPermissions")]
    public void Claude_is_told_what_it_may_do_without_asking(AiPermissionMode mode, string expected)
    {
        // The tile used to pass nothing, so a run inherited whatever the user's own Claude Code
        // settings said — and the factory default there is to ask, which a `-p` run has nobody to do.
        // Every edit was refused, the implementation wrote no files, and the tile reported "the last
        // attempt changed no files": a true sentence about the wrong thing.
        var psi = new ProcessStartInfo();
        new ClaudeToolRunner().ConfigureProcess(psi, "the prompt", streaming: true, permission: mode);

        var at = psi.ArgumentList.IndexOf("--permission-mode");
        Assert.True(at >= 0);
        Assert.Equal(expected, psi.ArgumentList[at + 1]);
    }

    [Fact]
    public void The_tools_own_settings_are_the_one_mode_that_adds_no_flag()
    {
        // The way back to what this did before the setting existed, for somebody whose Claude Code
        // configuration already says something deliberate.
        var psi = new ProcessStartInfo();
        new ClaudeToolRunner().ConfigureProcess(psi, "the prompt", streaming: false,
            permission: AiPermissionMode.ToolDefault);

        Assert.DoesNotContain("--permission-mode", psi.ArgumentList);
    }

    [Fact]
    public void A_run_that_is_not_told_a_mode_gets_on_with_the_work()
    {
        // The default is the flag, not its absence. Defaulting to "pass nothing" would leave the
        // inherited ask-first mode in place for every caller that forgets — which is every caller
        // written before the parameter existed.
        var psi = new ProcessStartInfo();
        new ClaudeToolRunner().ConfigureProcess(psi, "the prompt", streaming: false);

        Assert.Contains("auto", psi.ArgumentList);
    }

    [Fact]
    public void A_tool_with_no_such_flag_ignores_the_mode()
    {
        var psi = new ProcessStartInfo();
        new GenericToolRunner().ConfigureProcess(psi, "the prompt", streaming: false,
            permission: AiPermissionMode.BypassPermissions);

        Assert.Equal(["the prompt"], psi.ArgumentList);
    }

    // ── Refused tool calls ──────────────────────────────

    [Fact]
    public async Task A_refused_tool_call_is_counted_rather_than_shown()
    {
        // A denial arrives as a user turn carrying the tool_result, not as an error line, so nothing
        // in the reader saw one: a run refused every edit and came back looking like an agent that had
        // read some files and decided to change nothing.
        const string denied = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"Claude requested permissions to write to src/Cart.cs, but you haven't granted it yet."}]}}""";

        var output = await AiProcessRunner.ReadStreamAsync(
            new StringReader(string.Join("\n", [Said, denied, denied, Result])),
            new ClaudeToolRunner(), _ => { });

        Assert.Equal(2, output.PermissionDenials);

        // Counted, and kept out of the answer: the sentence is the harness talking, not the tool.
        Assert.Equal("the final answer", output.Text);
        Assert.False(output.Failed);
    }

    [Fact]
    public async Task An_ordinary_tool_failure_is_not_mistaken_for_a_refusal()
    {
        // is_error is set by every failed command and missing file too, so the flag alone cannot be
        // the test. A false denial would tell a user their permission mode is wrong when it is not.
        const string failed = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"error: no such file or directory"}]}}""";

        var output = await AiProcessRunner.ReadStreamAsync(
            new StringReader(failed), new ClaudeToolRunner(), _ => { });

        Assert.Equal(0, output.PermissionDenials);
    }

    [Theory]
    // The three that mattered, in the words the tools actually print. Every one of them arrives as an
    // ordinary tool_result with is_error set, and every one is a real failure of the work rather than
    // the harness refusing anything.
    [InlineData("git@github.com: Permission denied (publickey).")]
    [InlineData("bash: ./build.sh: Permission denied")]
    [InlineData("EACCES: permission denied, open '/etc/hosts'")]
    public async Task A_permission_error_from_the_work_itself_is_not_a_refusal(string message)
    {
        // A test matching "permission" and "denied" anywhere in the same result reads all three as the
        // harness declining a tool call. The tile then tells the user their permission mode is wrong
        // and points them at a setting that cannot help, while the actual cause — an ssh key, a file
        // mode — goes unmentioned. Cheap to miss a new spelling of a real denial; expensive to invent
        // one.
        var json =
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"""
            + System.Text.Json.JsonSerializer.Serialize(message)
            + """}]}}""";

        var output = await AiProcessRunner.ReadStreamAsync(
            new StringReader(json), new ClaudeToolRunner(), _ => { });

        Assert.Equal(0, output.PermissionDenials);
    }

    [Fact]
    public async Task A_refusal_is_read_out_of_a_content_list_as_well_as_a_string()
    {
        // Both shapes are the harness's, not a choice this side gets to make.
        const string denied = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":[{"type":"text","text":"Claude requested permissions to use Edit, but you haven't granted it yet."}]}]}}""";

        var output = await AiProcessRunner.ReadStreamAsync(
            new StringReader(denied), new ClaudeToolRunner(), _ => { });

        Assert.Equal(1, output.PermissionDenials);
    }

    /// <summary>Starts a run and lets the guard throw before anything is launched. Whatever happens
    /// after that — no such executable — is not what these are asking about.</summary>
    private static void Run(string executable, string prompt) =>
        AiProcessRunner.RunPlainAsync(executable, prompt, ".", new GenericToolRunner())
            .GetAwaiter().GetResult();

    // ── Standard input is always closed ─────────────────

    /// <summary>Captures the <see cref="ProcessStartInfo"/> and stops before anything is launched.</summary>
    private sealed class StartInfoRunner(bool acceptsStdin) : IAiToolRunner
    {
        public ProcessStartInfo? Captured { get; private set; }

        public bool AcceptsPromptOnStdin => acceptsStdin;

        public sealed class Stop : Exception;

        public void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
            AiPermissionMode permission = AiPermissionMode.Auto,
            AiEffort effort = AiEffort.High)
        {
            Captured = psi;
            throw new Stop();
        }

        public IReadOnlyList<AiOutputChunk> ParseLine(string line) => [];
    }

    /// <summary>
    /// Standard input is redirected for every tool, not only the one that reads its prompt from it.
    /// </summary>
    /// <remarks>
    /// <para>Inherited, it is this application's own standard input — and in a windowed process nobody
    /// is ever going to type into that. A tool that decides to be interactive therefore does not fail,
    /// it stops, on a path that deliberately has no wall-clock timeout, and the tile waits for ever
    /// with nothing on screen.</para>
    /// <para>It is the ordinary case rather than a corner: a bare positional prompt is what
    /// <c>GenericToolRunner</c> passes, and that is measured to open an interactive session rather than
    /// a print run on at least one CLI here. Every custom tool a user adds takes that path, and so does
    /// any tool whose own runner is removed.</para>
    /// <para>Redirecting is half of it; the run then closes the pipe at once when there is no prompt to
    /// send, so the child reads end-of-input instead of waiting. That half cannot be asserted without
    /// launching something, which is why this test pins the half it can and the reasoning sits beside
    /// the close itself.</para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Standard_input_is_redirected_whether_or_not_the_prompt_goes_down_it(bool acceptsStdin)
    {
        var runner = new StartInfoRunner(acceptsStdin);

        await Assert.ThrowsAsync<StartInfoRunner.Stop>(() => AiProcessRunner.RunPlainAsync(
            "some-tool", "the prompt", Path.GetTempPath(), runner));

        Assert.NotNull(runner.Captured);
        Assert.True(runner.Captured!.RedirectStandardInput,
            "standard input was left inherited, so a tool that waits for input waits on ours");
    }
}