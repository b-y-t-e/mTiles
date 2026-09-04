using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The gate's concurrency contract, argued without a filesystem: what generation a piece of work is
/// handed, and what an invalidate does to work that has not started yet.
/// </summary>
public sealed class WorkspaceWorkGateTests
{
    [Fact]
    public async Task Work_handed_the_generation_current_when_it_runs_sees_no_invalidate()
    {
        var gate = new WorkspaceWorkGate();
        var dir = Path.Combine(Path.GetTempPath(), "mtiles-gate-" + Guid.NewGuid().ToString("N"));
        long seen = -1;

        await gate.RunAsync(dir, generation =>
        {
            seen = generation;
            return Task.CompletedTask;
        });

        Assert.True(gate.IsCurrent(dir, seen));
    }

    /// <summary>Work queued behind a held turnstile and started only after an invalidate must carry
    /// the generation the invalidate retired — otherwise its IsCurrent checks pass and it acts on a
    /// workspace nobody has open: showing a wizard for it, starting an engine for it.</summary>
    [Fact]
    public async Task Work_queued_behind_a_busy_turnstile_gets_the_generation_it_was_queued_under()
    {
        var gate = new WorkspaceWorkGate();
        var dir = Path.Combine(Path.GetTempPath(), "mtiles-gate-" + Guid.NewGuid().ToString("N"));
        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var first = gate.RunAsync(dir, _ =>
        {
            firstEntered.TrySetResult();
            return releaseFirst.Task;
        });
        await firstEntered.Task;

        // RunAsync's synchronous prefix queues the work and samples the generation before its first
        // await, so this call is already queued behind the turnstile by the time it returns.
        long queuedGeneration = -1;
        var second = gate.RunAsync(dir, generation =>
        {
            queuedGeneration = generation;
            return Task.CompletedTask;
        });

        gate.Invalidate(dir);
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.False(gate.IsCurrent(dir, queuedGeneration),
            "Work that starts after the invalidate must carry a generation the invalidate retired.");
    }
}
