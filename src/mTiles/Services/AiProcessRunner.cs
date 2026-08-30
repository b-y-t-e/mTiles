using System.Diagnostics;
using System.Text;
using mTiles.Models;
using mTiles.Services.Agents;

namespace mTiles.Services;

/// <summary>
/// Running an agent headlessly: one process, its prompt, and everything it printed.
/// </summary>
/// <remarks>
/// What is left here after the agents moved out is the part that is the same for all of them —
/// starting the child, deciding whether the prompt goes on the command line or down standard input,
/// draining both pipes without deadlocking, and killing the tree when the tile is paused. Which flags
/// the child gets, and what its output means, are the agent's (<see cref="IAiAgent"/>).
/// </remarks>
public static class AiProcessRunner
{
    /// <summary>
    /// The agent a binary name refers to, and a bare-prompt fallback for one nothing is known about.
    /// </summary>
    /// <remarks>By binary name rather than by agent id because that is what the caller has — the two
    /// happen to be the same string for all five, which is a coincidence of naming and not something to
    /// rely on, so it is asked of the catalog rather than assumed.</remarks>
    public static IAiAgent GetRunner(string toolBinary) =>
        AiAgentCatalog.All.FirstOrDefault(
            agent => agent.BinaryName.Equals(toolBinary, StringComparison.OrdinalIgnoreCase))
        ?? new GenericAgent(toolBinary);

    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Refuses a prompt the operating system could not carry, with a message saying so.
    /// <para>The prompt is passed as a command-line argument by every agent here, and Windows caps a
    /// command line at 32 767 characters — 8 191 through a <c>.cmd</c> shim, which is what npm installs.
    /// Over the limit <c>Process.Start</c> throws a <see cref="System.ComponentModel.Win32Exception"/>
    /// whose text says nothing about length, so the tile reported that the tool had failed and offered
    /// to try again, which could only fail identically. This says what actually happened.</para>
    /// </summary>
    private static void GuardPromptLength(
        string executablePath, string prompt, AiAgentInstance? instance)
    {
        if (PromptBudget(executablePath, instance: instance) is not { } budget) return;

        if (budget <= 0)
            throw new InvalidOperationException(
                $"The path to this tool is {executablePath.Length} characters" +
                (ExtraArgsLength(instance) > 0
                    ? $" and its extra arguments another {ExtraArgsLength(instance)}"
                    : "") +
                ", which leaves no room on a command line for a prompt. Move the tool somewhere " +
                "shorter, or shorten the instance's extra arguments.");

        var quoted = CommandLineLength.Quoted(prompt);
        if (quoted <= budget) return;

        var throughShell = CommandLineLength.ThroughShell(executablePath);
        throw new InvalidOperationException(
            $"The prompt is {quoted} characters once quoted and {Path.GetFileName(executablePath)} can be " +
            $"given at most {budget} on a command line" +
            (throughShell ? " (it is a .cmd shim, which is the tighter of the two Windows limits)" : "") +
            ". The working tree and the plan are already capped, so this is a goal or a set of answers " +
            "that will not fit — shorten them, or use a tool that accepts its prompt on standard input.");
    }

    /// <summary>
    /// How many characters of prompt this tool can be handed on a command line, or <c>null</c> when the
    /// question does not arise — a tool that reads standard input has no command line to overflow, and
    /// off Windows the limit is something closer to two megabytes.
    /// <para>Public because the prompt builder needs it <em>before</em> it builds. Refusing an oversized
    /// prompt is the last line of defence and a poor one: the run is judged failed, the tile pauses, and
    /// Resume reproduces the same failure for ever. Knowing the budget in advance lets the borrowed
    /// blocks be trimmed to fit instead, which costs the tool some context and costs the user nothing.
    /// </para>
    /// <para>The arithmetic itself is <see cref="CommandLineLength"/>'s; what this adds is the one thing
    /// only an agent knows — whether the prompt is going on a command line at all.</para>
    /// </summary>
    public static int? PromptBudget(
        string executablePath, IAiAgent? agent = null, AiAgentInstance? instance = null)
    {
        if (agent?.AcceptsPromptOnStdin == true) return null;
        if (CommandLineLength.Budget(executablePath) is not { } budget) return null;

        return Math.Max(0, budget - ExtraArgsLength(instance));
    }

    /// <summary>
    /// What the instance's own <see cref="AiAgentInstance.ExtraArgs"/> will take off the same command
    /// line, quoted as they will be written and with the space in front of each.
    /// </summary>
    /// <remarks>Part of the budget rather than of the slack <see cref="CommandLineLength.Budget"/>
    /// keeps back: that slack is a fixed 256 characters for the agent's own flags, while these are
    /// user-typed and unbounded — a couple of <c>--add-dir</c> entries with long paths pushed the argv
    /// past the limit after the guard had already passed it, which is the opaque
    /// <see cref="System.ComponentModel.Win32Exception"/> the guard exists to prevent.</remarks>
    private static int ExtraArgsLength(AiAgentInstance? instance) =>
        instance is null
            ? 0
            : instance.ExtraArgs
                .Where(argument => !string.IsNullOrWhiteSpace(argument))
                .Sum(argument => CommandLineLength.Quoted(argument) + 1);

    /// <summary>
    /// What this agent will actually be run with, once what was asked for has been fitted to what it
    /// supports for this <paramref name="usage"/>.
    /// </summary>
    /// <remarks>
    /// <para>The one place the two rounding rules are applied, so that they are a fact about every run
    /// rather than a rule each agent has to remember. Without it <c>SupportedBehaviours</c> and
    /// <c>SupportedEfforts</c> are documentation: a mode an agent does not have reaches its command
    /// line, and the run fails on a flag the user never typed.</para>
    /// <para>Asked by the failure path too, so "was the flag we passed refused" is asked about the
    /// flag that was passed rather than the one the strip shows.</para>
    /// </remarks>
    /// <param name="instance">The configured instance being run, or null while a caller still resolves
    /// its agent by binary name — see <see cref="AiAgentCatalog.SeedInstanceFor"/>.</param>
    public static (AiBehaviour Behaviour, AiEffort Effort) Fit(
        IAiAgent agent,
        AiUsage usage,
        AiBehaviour behaviour,
        AiEffort effort,
        AiAgentInstance? instance = null)
    {
        var configured = instance ?? AiAgentCatalog.SeedInstanceFor(agent);

        return (AiBehaviours.RoundDown(behaviour, agent.SupportedBehaviours(configured, usage)),
            AiEfforts.RoundToNearest(effort, agent.SupportedEfforts(configured, usage)));
    }

    /// <summary>
    /// Puts the instance's own <see cref="AiAgentInstance.ExtraArgs"/> on the command line of a
    /// headless run, in front of the prompt when the prompt is the last positional argument.
    /// </summary>
    /// <remarks>
    /// <para>Here rather than in each agent, for the reason <c>AiAgent.Interactive</c> composes them
    /// rather than leaving them to six classes: an instance's settings apply wherever the instance is
    /// used, and the tile already honoured these while every goal run on the same instance ignored
    /// them — so <c>--add-dir</c> worked in the agent tile and silently did nothing in a goal.</para>
    /// <para><b>Where they go is the agent's answer</b> (<see cref="IAiAgent.ExtraArgsIndex"/>), not a
    /// rule worked out here. "In front of the last argument when it equals the prompt" is right for the
    /// agents that pass the prompt as a bare positional — an option after a positional is a parse this
    /// application does not get to decide — and wrong for agy, whose prompt is the value of its own
    /// <c>--print</c>: an argument slipped between the two became that flag's value and left the prompt
    /// as a stray positional.</para>
    /// <para>Blank entries are dropped, as they are for the interactive command: the field is a
    /// multi-line text box, so an empty line in it is a keystroke rather than an argument.</para>
    /// </remarks>
    private static void AddExtraArgs(
        IList<string> arguments, AiAgentInstance? instance, string prompt, IAiAgent agent)
    {
        if (instance is null) return;

        var extra = instance.ExtraArgs
            .Where(argument => !string.IsNullOrWhiteSpace(argument))
            .ToList();
        if (extra.Count == 0) return;

        var at = Math.Clamp(agent.ExtraArgsIndex([.. arguments], prompt), 0, arguments.Count);
        foreach (var argument in extra)
            arguments.Insert(at++, argument);
    }

    /// <summary>
    /// Runs the tool once and returns everything it printed.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately without an overall timeout</b>, and that is the decision rather than an
    /// oversight. An agent that has been running for forty
    /// minutes is doing the thing it was asked to do, and it writes as it goes — so a timeout would
    /// kill it mid-edit and leave the worktree half-changed, which is worse than waiting. Any number
    /// picked here would be a guess about how long somebody's task takes, applied to work that is
    /// already in the user's files.
    /// <para>What ends a run instead is <paramref name="ct"/>: Pause cancels it and the process tree is
    /// killed. That is a decision made by somebody who can see the tile, which is the right kind of
    /// decision for this.</para>
    /// </remarks>
    /// <param name="onActivity">
    /// Called with a few words about what the tool is doing, as it does it, when the tool can say —
    /// see <see cref="IAiAgent.SupportsStreaming"/>. <b>Called from the thread draining the child's
    /// output</b>, so anything touching the UI marshals for itself.
    /// <para>Passing one is what turns streaming on. Without it the tool is run exactly as it was and
    /// its output read at the end, which is what the tools that cannot stream always do.</para>
    /// </param>
    public static async Task<AiOutput> RunPlainAsync(
        string executablePath,
        string prompt,
        string workingDirectory,
        IAiAgent agent,
        AiUsage usage,
        AiBehaviour behaviour = AiBehaviour.Auto,
        AiEffort effort = AiEffort.High,
        Action<string>? onActivity = null,
        Action<int>? onStarted = null,
        AiAgentInstance? instance = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string model = "",
        CancellationToken ct = default)
    {
        (behaviour, effort) = Fit(agent, usage, behaviour, effort, instance);

        if (!agent.AcceptsPromptOnStdin)
            GuardPromptLength(executablePath, prompt, instance);

        var streaming = onActivity != null && agent.SupportsStreaming;

        var psi = CreateProcessStartInfo(executablePath, workingDirectory);
        ApplyEnvironment(psi, environment);

        // **Always redirected, whether or not the prompt goes down it.** Left inherited, a tool that
        // decides to be interactive waits on this application's own standard input, which in a windowed
        // process nobody is ever going to type into — so the run does not fail, it stops, on a path
        // that deliberately has no wall-clock timeout, and the tile waits for ever.
        //
        // This is not hypothetical and not about one tool: a bare positional prompt is what
        // `GenericAgent` passes, and a bare positional prompt is measured to open an interactive
        // session rather than a print run on at least one of the CLIs here. Every tool without an agent
        // of its own — every custom AI tool a user adds, and any tool whose entry is removed — takes
        // that path. Closing the pipe below turns "waits for input that will never come" into
        // end-of-input, which is a tool that exits and says something.
        psi.RedirectStandardInput = true;
        // Both, and this is the whole of what makes either setting real. `effort` was accepted here
        // and dropped on this line, so ConfigureProcess took its own default of High: every run went
        // out with `--effort high` whatever the strip said, the combo box was decoration, and — worse —
        // a Claude Code from before that flag existed rejected it on every goal with no way for the
        // user to turn it off, because choosing "tool default" changed nothing that got this far.
        // The model too, by the same argument as the effort: an instance configured to run a
        // provider's model had that setting reach nothing at all on four agents out of five, so the run
        // went out on the CLI's own default — to an address that usually does not serve it.
        agent.ConfigureProcess(psi, prompt, streaming, usage, behaviour, effort, model);
        AddExtraArgs(psi.ArgumentList, instance, prompt, agent);

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Reported as soon as there is something to report, and never afterwards: the caller uses it to
        // name the root of a process tree, and a tool that has exited leaves an id the system is free to
        // hand to somebody else. A caller that raises here is not allowed to take the run with it.
        try { onStarted?.Invoke(process.Id); } catch { /* a bystander is not worth a run */ }

        // The readers start first, then the prompt goes down stdin. The other order deadlocks on a
        // prompt large enough to fill the pipe: this side blocks writing the rest of it while the child
        // blocks writing output nobody is draining, which is the size of prompt stdin exists for.
        var stdoutTask = streaming
            ? ReadStreamAsync(process.StandardOutput, agent, onActivity!)
            : ReadToEndAsync(process.StandardOutput);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        // Registered before the write, not after. A prompt big enough to block — the child not draining
        // it — would otherwise sit here with nothing left to interrupt it, so pausing during the write
        // hung the tile until the tool gave up on its own.
        using var reg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        if (agent.AcceptsPromptOnStdin)
        {
            await WritePromptAsync(process, prompt);
        }
        else
        {
            // Nothing to send, so the pipe is closed at once — the same thing `WritePromptAsync` does
            // after writing, and for the same reason: an open pipe with nobody writing to it is
            // indistinguishable, from the child's side, from a user who has not typed yet.
            try { process.StandardInput.Close(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The child has already gone. Nothing to close it for.
            }
        }

        var output = await stdoutTask;
        var stderr = await stderrTask;

        await WaitForExitWithTimeoutAsync(process);

        ct.ThrowIfCancellationRequested();

        // A non-zero exit with something on stderr is the plain path's version of the stream's error
        // chunk, and is now reported the same way: the words kept, the fact carried beside them.
        if (process.HasExited && process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            return AiOutput.Failure(
                $"{output.Text.Trim()}\n\n[stderr] {stderr.Trim()}".Trim())
                with { PermissionDenials = output.PermissionDenials };

        return new AiOutput(output.Text.Trim(), output.Failed, output.PermissionDenials);
    }

    /// <summary>The whole of standard output, for a tool that cannot say anything about itself as it
    /// goes. Nothing here can fail on its own account — the exit code is the only signal, and the
    /// caller reads it.</summary>
    private static async Task<AiOutput> ReadToEndAsync(TextReader output) =>
        new(await output.ReadToEndAsync(CancellationToken.None), Failed: false);

    /// <summary>
    /// Drains a streaming tool, reporting what it does and keeping what it answers.
    /// </summary>
    /// <remarks>
    /// <para>The answer is the <c>result</c> line when there is one, because that is the tool's own
    /// final text rather than this side's reassembly of the pieces. Falling back to the pieces matters
    /// all the same: a run killed part way through has no result line, and the text it did produce is
    /// better than nothing to show for it.</para>
    /// <para>A line that parses to nothing is dropped, which is most of them — init, usage, tool
    /// results. Reading them is how the tile knows the difference between a tool that finished and one
    /// that stopped, which is the whole reason for streaming: with plain text output those two are the
    /// same string.</para>
    /// </remarks>
    /// <param name="output">
    /// The child's standard output. A <see cref="TextReader"/> rather than the process, so the rules
    /// below can be read off a string in a test instead of needing a tool installed to state them.
    /// </param>
    internal static async Task<AiOutput> ReadStreamAsync(
        TextReader output, IAiAgent agent, Action<string> onActivity)
    {
        var text = new StringBuilder();
        string? result = null;
        string? error = null;
        var denied = 0;

        while (await output.ReadLineAsync() is { } line)
        {
            foreach (var chunk in agent.ParseLine(line))
            {
                switch (chunk.Kind)
                {
                    case AiChunkKind.Activity:
                        // Not awaited and not marshalled: this is the caller's business, and holding the
                        // reader while a dispatcher gets round to it stalls the pipe the child is writing
                        // into.
                        try { onActivity(chunk.Content); } catch { /* a status line is not worth a run */ }
                        break;

                    case AiChunkKind.Result:
                        // Only when it says something. An empty result line is what a run that was killed
                        // or that failed leaves behind, and taking it anyway meant an empty string beat the
                        // text the tool had already produced — the answer thrown away in favour of the
                        // absence of one.
                        if (chunk.Content.Length > 0) result = chunk.Content;
                        break;

                    case AiChunkKind.Error:
                        // Kept apart, not appended. The tool's account of its own failure is not a paragraph
                        // of its answer, and glued onto a half-finished one it becomes a sentence the review
                        // prompt reads as something the implementation decided.
                        error = chunk.Content;
                        break;

                    case AiChunkKind.Denied:
                        denied++;
                        break;

                    case AiChunkKind.Text:
                        // Appended without a newline. A whole assistant message ends where it ends, and a
                        // content_block_delta is a fragment — often half a word — so a line break between
                        // them puts one inside the word. Claude emits no deltas without
                        // --include-partial-messages, so this is unreached today and is written for the day
                        // it is not.
                        text.Append(chunk.Content);
                        break;

                    default:
                        break;
                }
            }
        }

        // The tool's own final text first, then whatever it said on the way. The error is never thrown
        // away: dropping it whenever anything else had been printed is how a run that stopped half way
        // through came back looking like one that finished, and the thing that went wrong — a credit
        // balance, a revoked key — was never said out loud anywhere.
        //
        // Labelled rather than glued on, and after the answer rather than into it, which is the same
        // shape RunPlainAsync already uses for a non-zero exit with something on stderr. The verdict
        // stays content-based on purpose: a failed implementation has usually already written files, and
        // what it managed to say about them is worth more to the next attempt than a clean "it failed".
        var answer = result is { Length: > 0 } ? result
            : text.Length > 0 ? text.ToString().TrimEnd()
            : "";

        if (error is not { Length: > 0 }) return AiOutput.Answered(answer) with { PermissionDenials = denied };

        // Both halves. The text is kept because a failed implementation has usually already written
        // files and this is the only account of what is in the worktree; the flag is kept because
        // without it the loop reads that account as an answer and adopts it as the plan, or as the
        // review, and carries on.
        return AiOutput.Failure(
            answer.Length > 0 ? $"{answer}\n\n[error] {error}" : error)
            with { PermissionDenials = denied };
    }

    /// <summary>
    /// Writes the prompt to the child's standard input and closes the pipe.
    /// <para>Closing is the part that matters: a tool reading its prompt from standard input waits for
    /// end-of-input before it starts, so a handle left open hangs the run until the timeout.</para>
    /// </summary>
    private static async Task WritePromptAsync(Process process, string prompt)
    {
        try
        {
            await process.StandardInput.WriteAsync(prompt);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The child stopped reading — it exited early, or it was killed. Not worth throwing over:
            // letting this out skipped the awaits on stdout and stderr, so the tool's own account of
            // what went wrong was thrown away in favour of "the pipe is broken".
            Trace.TraceWarning($"Writing the prompt to standard input ended early: {ex.Message}");
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { /* already gone */ }
        }
    }

    private static async Task WaitForExitWithTimeoutAsync(Process process)
    {
        using var exitCts = new CancellationTokenSource(ProcessExitTimeout);
        try
        {
            await process.WaitForExitAsync(exitCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>
    /// Puts an instance's own environment on a run — its provider's address, its key, its model.
    /// </summary>
    /// <remarks><b>A null value unsets</b>, which is the same contract <c>PtyOptions.Environment</c>
    /// carries for the tile launch paths, and it exists for the same reason: on a machine that exports a
    /// global <c>ANTHROPIC_API_KEY</c>, a block that could only add would leave the inherited key beside
    /// our token and the run would go out on somebody else's account without a word.
    /// <para>Never the prompt or a command line: a key on either is a key in a log.</para></remarks>
    private static void ApplyEnvironment(ProcessStartInfo psi,
        IReadOnlyDictionary<string, string?>? environment)
    {
        if (environment is null) return;

        foreach (var (name, value) in environment)
        {
            if (value is null) psi.Environment.Remove(name);
            else psi.Environment[name] = value;
        }
    }

    private static ProcessStartInfo CreateProcessStartInfo(string executablePath, string workingDirectory) => new()
    {
        FileName = executablePath,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        StandardOutputEncoding = System.Text.Encoding.UTF8,
        StandardErrorEncoding = System.Text.Encoding.UTF8
    };
}
