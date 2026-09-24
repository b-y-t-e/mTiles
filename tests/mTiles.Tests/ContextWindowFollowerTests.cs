using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Agents.SessionLogs;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The gauge's denominator belongs to a model, and follows it when <c>/model</c> changes it inside the
/// TUI — and a store that does not exist yet is still watched once it appears.
/// </summary>
public sealed class ContextWindowFollowerTests
{
    private static readonly Dictionary<string, long?> Windows = new()
    {
        ["claude-opus-5"] = 1_000_000,
        ["claude-opus-4-5"] = 200_000,
    };

    private static ContextWindowFollower Follower(Action? changed = null) =>
        new((model, _) => Task.FromResult(Windows.GetValueOrDefault(model)),
            post: action => action(), changed: changed ?? (() => { }));

    private static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
    }

    [Fact]
    public async Task A_model_changed_inside_the_tui_replaces_the_window()
    {
        // The answer lands on a pool thread, and Window is set before changed is called: waiting on the
        // count rather than on Window is what keeps the assertions behind both.
        var changes = 0;
        var follower = Follower(() => Interlocked.Increment(ref changes));
        follower.Settle("claude-opus-5");
        await Until(() => Volatile.Read(ref changes) == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(1_000_000, follower.Window);

        follower.Follow("claude-opus-4-5");
        // Dropped at once: a count with no bar, never a bar against the previous model's room.
        Assert.True(follower.Window is null or 200_000);

        await Until(() => Volatile.Read(ref changes) == 2, TimeSpan.FromSeconds(5));
        Assert.Equal(200_000, follower.Window);
        Assert.Equal(2, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task The_same_model_is_not_asked_about_again()
    {
        var asked = 0;
        var follower = new ContextWindowFollower((_, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult<long?>(131_072);
        }, action => action(), () => { });

        follower.Settle("z-ai/glm-5");
        await Until(() => follower.Window is not null, TimeSpan.FromSeconds(5));
        follower.Follow("openrouter/z-ai/glm-5");
        follower.Follow("Z-AI/GLM-5");

        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task A_model_nobody_could_answer_for_yet_is_asked_again_at_the_next_reading()
    {
        // A subscription's token is not renewed by the gauge, so the answer at launch can be "nobody
        // said" and become answerable once the CLI in the tile has renewed it itself.
        var asked = 0;
        var follower = new ContextWindowFollower((_, _) =>
            Task.FromResult<long?>(Interlocked.Increment(ref asked) == 1 ? null : 1_000_000),
            action => action(), () => { });

        follower.Settle("claude-opus-5");
        await Until(() => Volatile.Read(ref asked) == 1 && !follower.IsAsking, TimeSpan.FromSeconds(5));
        follower.Follow("claude-opus-5");
        await Until(() => follower.Window is not null, TimeSpan.FromSeconds(5));

        Assert.Equal(1_000_000, follower.Window);
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task An_answer_that_changes_nothing_is_not_announced()
    {
        // The caller answers an announcement by reading again, and a reading of a model without a window
        // asks again: announcing a null that changed nothing re-read the store for the life of the tile.
        var asked = 0;
        var announced = 0;
        var follower = new ContextWindowFollower((_, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult<long?>(null);
        }, action => action(), () => Interlocked.Increment(ref announced));

        follower.Settle("codex-mini");
        await Until(() => Volatile.Read(ref asked) == 1 && !follower.IsAsking, TimeSpan.FromSeconds(5));
        follower.Follow("codex-mini");
        await Until(() => Volatile.Read(ref asked) == 2 && !follower.IsAsking, TimeSpan.FromSeconds(5));

        Assert.Equal(2, Volatile.Read(ref asked));
        // Once for the model settled — the caller may be drawing another model's window — and not for the
        // repeat that changed nothing.
        Assert.Equal(1, Volatile.Read(ref announced));
        Assert.Null(follower.Window);
    }

    [Fact]
    public async Task A_model_nobody_describes_leaves_no_window()
    {
        var follower = Follower();
        follower.Settle("claude-opus-5");
        await Until(() => follower.Window is not null, TimeSpan.FromSeconds(5));

        follower.Follow("some-local-model");
        await Until(() => !follower.IsAsking, TimeSpan.FromSeconds(5));

        Assert.Null(follower.Window);
    }

    [Fact]
    public void A_launch_does_not_wait_for_the_window()
    {
        var answer = new TaskCompletionSource<long?>();
        var follower = new ContextWindowFollower((_, _) => answer.Task, action => action(), () => { });

        follower.Settle("claude-opus-5");

        Assert.Null(follower.Window);
        answer.SetResult(1_000_000);
    }

    [Theory]
    [InlineData("claude-opus-5", "claude-opus-5", true)]
    [InlineData("openrouter/z-ai/glm-5", "z-ai/glm-5", true)]
    [InlineData("z-ai/glm-5", "openrouter/z-ai/glm-5", true)]
    [InlineData("claude-opus-5", "claude-opus-4-5", false)]
    [InlineData("claude-opus-5", "", false)]
    public void Two_spellings_name_one_model_only_when_one_qualifies_the_other(
        string a, string b, bool same) =>
        Assert.Equal(same, ContextWindowFollower.NamesTheSameModel(a, b));

    [Fact]
    public async Task A_store_that_appears_after_the_tile_started_is_watched()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mtiles-watch-" + Guid.NewGuid().ToString("N"));
        var log = new CountingLog(directory);
        using var watcher = new AgentSessionWatcher(log, null, "D:\\w", DateTimeOffset.UtcNow,
            isFree: null, report: _ => { }, knownSessionId: () => "known");
        try
        {
            watcher.Start();
            await Until(() => log.Reads >= 1, TimeSpan.FromSeconds(5));

            Directory.CreateDirectory(directory);
            // The retry finds it and reads what the CLI wrote before anybody was watching.
            await Until(() => log.Reads >= 2, AgentSessionWatcher.AttachRetryInterval * 3);
            Assert.True(log.Reads >= 2);

            var before = log.Reads;
            await File.WriteAllTextAsync(Path.Combine(directory, "known.jsonl"), "{}");
            await Until(() => log.Reads > before, TimeSpan.FromSeconds(5));
            Assert.True(log.Reads > before);
        }
        finally
        {
            watcher.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class CountingLog(string directory) : IAgentSessionLog
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);

        public bool TellsHeadlessRunsApart => false;

        public Task<IReadOnlyList<string>> ListInteractiveAsync(AiSignIn? signIn, string workspaceDir,
            DateTimeOffset since, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<AgentSessionReading?> ReadLatestAsync(AiSignIn? signIn, string workspaceDir,
            DateTimeOffset since, Func<string, bool>? isFree = null, CancellationToken ct = default) =>
            Task.FromResult<AgentSessionReading?>(null);

        public Task<AgentSessionReading?> ReadAsync(AiSignIn? signIn, string workspaceDir, string sessionId,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult<AgentSessionReading?>(new AgentSessionReading(sessionId, DateTimeOffset.UtcNow));
        }

        public string? WatchDirectory(AiSignIn? signIn, string workspaceDir) => directory;
    }
}
