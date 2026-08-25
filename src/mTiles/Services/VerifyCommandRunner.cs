using System.Diagnostics;
using System.Text;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// What the user's verify command did: whether it ran at all, what it exited with, and what it printed.
/// </summary>
/// <param name="Ran">False when there is no command configured, or when it could not be started. A
/// command that could not be started is deliberately <em>not</em> a failed one — refusing to finish a
/// goal because a shell was missing would be blaming the work for the tooling.</param>
/// <param name="TimedOut">Told apart from every other reason the command did not run, because it is the
/// one that is <em>about the work</em>. A missing shell is the machine's fault and must not fail a goal;
/// a build still running after half an hour is very often the change that was just made — a test that
/// now waits on something, a loop that does not end. Treating the two alike let a goal be declared met
/// over a verification that had never produced an answer, and did it again on every attempt, so a run
/// could take fifty half-hours to arrive at "goal completed".</param>
internal readonly record struct VerifyOutcome(
    bool Ran, int ExitCode, string Output, string? Problem = null, bool TimedOut = false)
{
    public bool Succeeded => Ran && ExitCode == 0;

    public static VerifyOutcome NotRun(string? problem = null) => new(false, 0, "", problem);

    public static VerifyOutcome Timeout(string problem) => new(false, 0, "", problem, TimedOut: true);
}

/// <summary>
/// Runs the tile's verify command — <c>dotnet build</c>, <c>npm test</c>, whatever the user typed.
/// <para>The only completion criterion that is not the AI tool's opinion of its own work, which is the
/// whole argument for it: a review is written by the same family of model that wrote the code, and it
/// will call a build broken or working with equal confidence. An exit code will not.</para>
/// <para>Its own class, with a <see cref="Factory"/> seam, for the reason <see cref="WorktreeReader"/>
/// has one: without it every test that drives the loop spawns a shell.</para>
/// </summary>
/// <param name="timeout">Overridden only by a test. A real one of these would take half an hour to
/// prove anything about it, and what is being proved — that a command which never ends is killed rather
/// than left holding the tile — does not depend on the number.</param>
internal sealed class VerifyCommandRunner(string workingDirectory, AppSettings settings, TimeSpan? timeout = null)
{
    /// <summary>Replaced by a test. Null means run the real command.</summary>
    internal static Func<string, string, CancellationToken, Task<VerifyOutcome>>? Factory { get; set; }

    /// <summary>
    /// How much of the command's output goes into the review prompt.
    /// <para>Small on purpose. A failing build prints its first error near the top and then repeats
    /// itself; the tail is where a test runner puts its summary, so both ends are kept and the middle
    /// is what gives way. It shares the prompt's one command-line budget with the diff — see
    /// <see cref="GoalDiffContext.MaxDiffChars"/> — and it is the newest claimant on it.</para>
    /// </summary>
    public const int MaxOutputChars = 2_000;

    /// <summary>
    /// How long a verify command may take before it is killed.
    /// <para>Generous, because a cold <c>dotnet build</c> or a full test run legitimately takes
    /// minutes and cutting one short would fail a goal for the machine being slow. It is here for the
    /// command that never finishes at all — one waiting on input, or on a lock — which without this
    /// wedged the tile permanently: the loop cannot go on and Pause is the only way out, and Pause has
    /// to be clicked by somebody who has realised what happened.</para>
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    /// <summary>How long the child is given to die once it has been killed, before it is left to it.</summary>
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

    public async Task<VerifyOutcome> RunAsync(string command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command)) return VerifyOutcome.NotRun();
        if (Factory is { } stub) return await stub(workingDirectory, command, ct);

        // The caller's cancellation and the timeout, as one token. Both end the same way — the process
        // tree is killed — and the difference is only what is reported afterwards.
        var limit = timeout ?? DefaultTimeout;
        using var timeoutSource = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
        var token = linked.Token;

        Process? started = null;

        // Out here so the cancellation path can await them after killing the tree. Inside the try they
        // were out of scope exactly where they most need observing.
        Task<string>? stdout = null;
        Task<string>? stderr = null;

        try
        {
            // The same swap the launch chain makes, and for the same measured reasons: cmd.exe runs
            // only the first line of a multi-line command and does not treat ";" as a separator, so a
            // verify command of two steps silently became one.
            var shell = ShellDetector.ResolveForCommands(ShellDetector.ResolveDefault(settings));
            var (executable, args) = ShellCommandLine.For(shell, command);

            var psi = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                // Redirected and closed immediately below. A command that stops to ask something —
                // "overwrite? [y/N]" — otherwise waits on a console nobody is attached to, for ever.
                // Closed input gives it end-of-file, which every well-behaved tool treats as no.
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            var process = Process.Start(psi)
                          ?? throw new InvalidOperationException($"Could not start {executable}.");
            started = process;

            process.StandardInput.Close();

            // Both streams drained before the wait, as everywhere else here: a build that fills the
            // stderr pipe while nobody reads it blocks for ever, and a verify command is exactly the
            // kind that prints megabytes.
            stdout = process.StandardOutput.ReadToEndAsync(token);
            stderr = process.StandardError.ReadToEndAsync(token);
            await Task.WhenAll(stdout, stderr);
            await process.WaitForExitAsync(token);

            var output = string.Join("\n", new[] { await stdout, await stderr }
                .Select(s => s.Trim())
                .Where(s => s.Length > 0));

            return new VerifyOutcome(true, process.ExitCode, Clip(output));
        }
        catch (OperationCanceledException)
        {
            // The build has to be killed, not merely abandoned. Disposing the Process object does not
            // stop the child, so a pause used to leave a compiler running in the user's own worktree —
            // writing to the very files the next attempt reads, with nobody left holding a handle to it.
            // The whole tree, because the child here is a shell and the build is its grandchild.
            await KillTreeAsync(started);

            // The two readers are still out there. They were started with the same token, so they end
            // cancelled — but a read that fails on a pipe torn down by the kill ends *faulted*, and an
            // unobserved faulted task is what CrashHandler reports as an unhandled one. Awaited and
            // discarded: this is the only path in this class where "drained before the wait" was left
            // half-done.
            if (stdout is not null && stderr is not null)
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
                catch { /* already over, and that is the point */ }

            // A timeout is not a cancellation, whatever the exception says: nobody asked for it, and
            // reporting it as a pause would leave the user looking at a tile that stopped for no
            // reason they can see.
            if (timeoutSource.IsCancellationRequested && !ct.IsCancellationRequested)
                return VerifyOutcome.Timeout(
                    $"it was still running after {Describe(limit)} and was stopped");

            // Otherwise a pause, rethrown rather than reported as a failed verification — which would
            // end the goal for something the user asked for.
            throw;
        }
        catch (Exception ex)
        {
            await KillTreeAsync(started);

            // Reported, not swallowed, and not counted as a failure: the criteria stay unblocked and
            // the transcript says the check could not be made. Silence here would have looked exactly
            // like a passing build.
            Trace.TraceWarning($"Verify command failed to run: {ex.Message}");
            return VerifyOutcome.NotRun(ex.Message);
        }
        finally
        {
            started?.Dispose();
        }
    }

    /// <summary>
    /// Ends the command and everything it started, and waits long enough to know that it has.
    /// <para>A kill is a request, not an event: returning before it has been honoured means the next
    /// attempt starts against a tree the last one is still writing to.</para>
    /// </summary>
    private static async Task KillTreeAsync(Process? process)
    {
        if (process == null) return;

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            // Already gone, or not ours to kill. Neither is worth throwing out of a cancellation path.
            Trace.TraceWarning($"Could not stop the verify command: {ex.Message}");
            return;
        }

        try
        {
            using var grace = new CancellationTokenSource(ExitTimeout);
            await process.WaitForExitAsync(grace.Token);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"The verify command did not stop within {ExitTimeout}: {ex.Message}");
        }
    }

    /// <summary>A duration as somebody would say it. <c>TimeSpan</c>'s own formatting turns half an
    /// hour into "00:30:00", which reads like a clock rather than a limit.</summary>
    private static string Describe(TimeSpan span) => span.TotalMinutes >= 1
        ? $"{span.TotalMinutes:0.#} minutes"
        : $"{span.TotalSeconds:0.#} seconds";

    /// <summary>Keeps both ends of the output and drops the middle, on line boundaries.</summary>
    internal static string Clip(string output)
    {
        if (output.Length <= MaxOutputChars) return output;

        // The marker comes out of the budget, not on top of it. It did not, so the result was some
        // twenty characters over MaxOutputChars — and the block that carries it is capped at exactly
        // MaxOutputChars, which cut those characters off the end. The end is the tail, which is the
        // half this keeps a tail for: a test runner puts its summary there.
        const string marker = "\n… output truncated …\n";
        var room = MaxOutputChars - marker.Length;

        var head = output[..(room * 2 / 3)];
        var tail = output[^(room / 3)..];

        var headBreak = head.LastIndexOf('\n');
        if (headBreak > 0) head = head[..headBreak];

        var tailBreak = tail.IndexOf('\n');
        if (tailBreak >= 0 && tailBreak < tail.Length - 1) tail = tail[(tailBreak + 1)..];

        return head + marker + tail;
    }
}
