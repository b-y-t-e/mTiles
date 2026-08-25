using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The verify command against a real shell — the only tests in this suite that start one, and they earn
/// it: what is being proved is that a command which outlives its welcome is <b>killed</b>, and nothing
/// short of a real process shows the difference between killing one and walking away from it.
/// <para>Each writes a marker file after a delay. If the marker appears, the process was still alive
/// after the runner said it was finished — which is the bug: a compiler left running in the user's own
/// worktree, writing to the files the next attempt reads, with nobody holding a handle to it.</para>
/// </summary>
public class VerifyCommandRunnerProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-verify-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings = new();

    public VerifyCommandRunnerProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* not a test failure */ }
    }

    /// <summary>
    /// Sleeps, then writes <c>marker</c> — spelled for the shell the runner will actually use.
    /// <para>Asking <c>OperatingSystem.IsWindows()</c> is not the same question and got the wrong
    /// answer: this machine is Windows and resolves to Git Bash, so the PowerShell spelling produced
    /// <c>Start-Sleep: command not found</c> and a command that exited in 77 ms — which every one of
    /// these assertions would then have passed for the wrong reason.</para>
    /// </summary>
    private string SleepThenMark(int seconds) =>
        ShellDetector.ResolveForCommands(ShellDetector.ResolveDefault(_settings)).Type == ShellType.PowerShell
            ? $"Start-Sleep -Seconds {seconds}; Set-Content -Path marker -Value done"
            : $"sleep {seconds}; echo done > marker";

    private VerifyCommandRunner Runner(TimeSpan? timeout = null) => new(_dir, _settings, timeout);

    private bool MarkerExists => File.Exists(Path.Combine(_dir, "marker"));

    [Fact]
    public async Task A_command_that_finishes_is_reported_with_its_exit_code()
    {
        // The exit code is the whole reason this class exists: it is the one fact in a review that is
        // not the AI tool's opinion of its own work.
        var outcome = await Runner().RunAsync("exit 3", CancellationToken.None);

        Assert.True(outcome.Ran, outcome.Problem ?? "no problem reported");
        Assert.Equal(3, outcome.ExitCode);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task Cancelling_kills_the_command_rather_than_leaving_it_running()
    {
        using var cts = new CancellationTokenSource();
        var run = Runner().RunAsync(SleepThenMark(2), cts.Token);

        // Long enough for the shell to be up and sleeping, far short of the four seconds it needs.
        await Task.Delay(700);
        await cts.CancelAsync();

        // A pause, reported as one. Anything else would end the goal for something the user asked for.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // Past when the command would have written it. Disposing a Process does not stop the child, and
        // WaitForExitAsync(ct) only stops waiting, so before the kill this file appeared every time.
        //
        // Two seconds of sleep and three of waiting rather than four and five. These are the only tests
        // here that idle on a clock, and every second of it is load under somebody else's wall-clock
        // assertion elsewhere in the run — this class is not in the serialised seam collection, so it
        // does run alongside the rest.
        await Task.Delay(3_000);
        Assert.False(MarkerExists);
    }

    [Fact]
    public async Task A_command_that_never_ends_is_stopped_and_reported_rather_than_holding_the_tile()
    {
        // The real limit is half an hour; the number is not what is under test. Without any limit a
        // command waiting on something that never arrives wedges the loop, and the only way out is a
        // Pause clicked by somebody who has worked out what happened.
        var outcome = await Runner(TimeSpan.FromMilliseconds(700))
            .RunAsync(SleepThenMark(2), CancellationToken.None);

        // Not a failed verification: nobody asked for it, and failing the goal over it would blame the
        // work for the tooling. It is a check that could not be made, with the reason.
        Assert.False(outcome.Ran);
        Assert.False(outcome.Succeeded);
        Assert.NotNull(outcome.Problem);
        Assert.Contains("still running", outcome.Problem);

        await Task.Delay(3_000);
        Assert.False(MarkerExists);
    }
}

/// <summary>
/// <see cref="WorktreeReader.HasChangesAsync"/> against real git — the question two buttons are shown
/// on the strength of.
/// </summary>
[Collection(GoalSeamCollection.Name)]
public class WorktreeReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-tree-" + Guid.NewGuid().ToString("N"));

    public WorktreeReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        WorktreeReader.Factory = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* not a test failure */ }
    }

    [Fact]
    public async Task A_directory_that_is_not_a_repository_answers_no()
    {
        // The button offers to work a goal out of the changes. Offering it where the changes cannot be
        // read is offering a run that can only fail, so an unreadable repository is a no rather than an
        // exception escaping into a fire-and-forget task.
        var reader = new WorktreeReader(_dir, "git");

        Assert.False(await reader.HasChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_tree_nobody_could_read_says_so_rather_than_looking_clean()
    {
        // The distinction the no-change stop rests on. A directory that is not a repository produces
        // the same answer on every read, so comparing two of them said the implementation had changed
        // nothing — and every goal in such a workspace ended after one attempt, with a confident and
        // false explanation. "I could not tell" is not "nothing happened".
        var unreadable = await new WorktreeReader(_dir, "git").ReadAsync(CancellationToken.None);

        Assert.False(unreadable.Readable);
        Assert.False(unreadable.ProvablyUnchangedFrom(unreadable));
    }

    [Fact]
    public async Task The_stub_stands_in_for_git_and_an_empty_answer_is_a_clean_tree()
    {
        // The seam every loop test runs through. An empty or whitespace answer has to read as "nothing
        // to detect", or those tests would offer the buttons over a tree they had said was clean.
        WorktreeReader.Factory = (_, _) => Task.FromResult<string?>("   ");
        Assert.False(await new WorktreeReader(_dir, "git").HasChangesAsync(CancellationToken.None));

        WorktreeReader.Factory = (_, _) => Task.FromResult<string?>("diff --git a/x b/x");
        Assert.True(await new WorktreeReader(_dir, "git").HasChangesAsync(CancellationToken.None));
    }
}
