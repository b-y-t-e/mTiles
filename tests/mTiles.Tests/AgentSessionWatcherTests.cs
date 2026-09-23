using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Agents.SessionLogs;
using Xunit;

namespace mTiles.Tests;

/// <summary>Which conversation a watcher may hand its tile: the one it knows, and a new one only when the
/// tile's own submission can explain it.</summary>
public sealed class AgentSessionWatcherTests
{
    [Theory]
    [InlineData(null, "known")]
    [InlineData(-5, "known")]
    [InlineData(5, "new")]
    public async Task A_new_conversation_is_taken_only_after_a_submission_the_known_one_did_not_answer(
        int? submittedSecondsAfterKnownWrite, string expected)
    {
        var knownWrittenAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        DateTimeOffset? submittedAt = submittedSecondsAfterKnownWrite is { } seconds
            ? knownWrittenAt.AddSeconds(seconds)
            : null;

        Assert.Equal(expected, await FirstReading(new TwoConversationLog(knownWrittenAt), () => submittedAt));
    }

    [Fact]
    public async Task A_tile_that_says_nothing_about_submissions_takes_any_new_conversation() =>
        Assert.Equal("new", await FirstReading(new TwoConversationLog(DateTimeOffset.UtcNow), lastSubmission: null));

    private static async Task<string> FirstReading(IAgentSessionLog log, Func<DateTimeOffset?>? lastSubmission)
    {
        var readings = new List<string>();
        using var watcher = new AgentSessionWatcher(log, null, "D:\\w",
            DateTimeOffset.UtcNow.AddHours(-1), isFree: null,
            report: reading => { lock (readings) readings.Add(reading.SessionId); },
            knownSessionId: () => "known",
            lastSubmission: lastSubmission);

        watcher.Start();
        await Until(() => { lock (readings) return readings.Count > 0; }, TimeSpan.FromSeconds(5));

        lock (readings) return Assert.Single(readings);
    }

    [Fact]
    public async Task A_conversation_seen_written_before_the_submission_is_never_taken_after_it()
    {
        // A claude in a terminal outside this application goes on writing into the same directory; an
        // Enter here that makes the tile's own transcript write nothing straight away must not hand the
        // tile that conversation.
        DateTimeOffset? submittedAt = null;
        var readings = new List<string>();
        using var watcher = new AgentSessionWatcher(new StrangerLog(), null, "D:\\w",
            DateTimeOffset.UtcNow.AddHours(-1), isFree: null,
            report: reading => { lock (readings) readings.Add(reading.SessionId); },
            knownSessionId: () => "known",
            lastSubmission: () => submittedAt);

        watcher.Start();
        await Until(() => { lock (readings) return readings.Count > 0; }, TimeSpan.FromSeconds(5));

        submittedAt = DateTimeOffset.UtcNow;
        watcher.ReadNow();
        await Until(() => { lock (readings) return readings.Count > 1; }, TimeSpan.FromSeconds(5));

        lock (readings) Assert.Equal(["known", "known"], readings);
    }

    [Fact]
    public async Task Nothing_is_reported_once_the_watcher_is_disposed()
    {
        var reported = 0;
        var watcher = new AgentSessionWatcher(new TwoConversationLog(DateTimeOffset.UtcNow), null, "D:\\w",
            DateTimeOffset.UtcNow, isFree: null,
            report: _ => Interlocked.Increment(ref reported),
            knownSessionId: () => "known");

        watcher.Start();
        watcher.Dispose();
        await Task.Delay(AgentSessionWatcher.QuietWindow * 3);

        Assert.Equal(0, Volatile.Read(ref reported));
    }

    /// <summary>A read asked for while another is running is taken once that one finishes, rather than
    /// lost until the next file event.</summary>
    [Fact]
    public async Task A_read_asked_for_during_a_read_is_taken_afterwards()
    {
        var log = new SlowFirstReadLog();
        var reported = 0;
        using var watcher = new AgentSessionWatcher(log, null, "D:\\w", DateTimeOffset.UtcNow, isFree: null,
            report: _ => Interlocked.Increment(ref reported),
            knownSessionId: () => "known");

        watcher.Start();
        await Until(() => log.Reads >= 1, TimeSpan.FromSeconds(5));
        watcher.ReadNow();
        await Task.Delay(AgentSessionWatcher.QuietWindow * 3);
        log.ReleaseFirstRead();

        await Until(() => log.Reads >= 2, TimeSpan.FromSeconds(5));
        Assert.Equal(2, log.Reads);
    }

    private static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
    }

    /// <summary>A store holding the conversation the tile knows, last written at
    /// <paramref name="knownWrittenAt"/>, and a newer one somebody began.</summary>
    private sealed class TwoConversationLog(DateTimeOffset knownWrittenAt) : IAgentSessionLog
    {
        public bool TellsHeadlessRunsApart => true;

        public Task<IReadOnlyList<string>> ListInteractiveAsync(AiSignIn? signIn, string workspaceDir,
            DateTimeOffset since, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<AgentSessionReading?> ReadLatestAsync(AiSignIn? signIn, string workspaceDir,
            DateTimeOffset since, Func<string, bool>? isFree = null, CancellationToken ct = default) =>
            Task.FromResult<AgentSessionReading?>(new AgentSessionReading("new", DateTimeOffset.UtcNow));

        public Task<AgentSessionReading?> ReadAsync(AiSignIn? signIn, string workspaceDir, string sessionId,
            CancellationToken ct = default) =>
            Task.FromResult<AgentSessionReading?>(new AgentSessionReading(sessionId, knownWrittenAt));

        public string? WatchDirectory(AiSignIn? signIn, string workspaceDir) => null;
    }

    /// <summary>A store holding the conversation the tile knows, written a minute ago, and one a process
    /// outside the tile is writing now — offered only to a caller that finds it free.</summary>
    private sealed class StrangerLog : IAgentSessionLog
    {
        public bool TellsHeadlessRunsApart => true;

        public Task<IReadOnlyList<string>> ListInteractiveAsync(AiSignIn? signIn, string workspaceDir,
            DateTimeOffset since, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["stranger"]);

        public Task<AgentSessionReading?> ReadLatestAsync(AiSignIn? signIn, string workspaceDir,
            DateTimeOffset since, Func<string, bool>? isFree = null, CancellationToken ct = default) =>
            Task.FromResult(isFree?.Invoke("stranger") == false
                ? null
                : new AgentSessionReading("stranger", DateTimeOffset.UtcNow));

        public Task<AgentSessionReading?> ReadAsync(AiSignIn? signIn, string workspaceDir, string sessionId,
            CancellationToken ct = default) =>
            Task.FromResult<AgentSessionReading?>(
                new AgentSessionReading(sessionId, DateTimeOffset.UtcNow.AddMinutes(-1)));

        public string? WatchDirectory(AiSignIn? signIn, string workspaceDir) => null;
    }

    /// <summary>A store whose first read does not finish until the test says so.</summary>
    private sealed class SlowFirstReadLog : IAgentSessionLog
    {
        private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public void ReleaseFirstRead() => _firstRead.TrySetResult();

        public bool TellsHeadlessRunsApart => false;

        public Task<IReadOnlyList<string>> ListInteractiveAsync(AiSignIn? signIn, string workspaceDir,
            DateTimeOffset since, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<AgentSessionReading?> ReadLatestAsync(AiSignIn? signIn, string workspaceDir,
            DateTimeOffset since, Func<string, bool>? isFree = null, CancellationToken ct = default) =>
            Task.FromResult<AgentSessionReading?>(null);

        public async Task<AgentSessionReading?> ReadAsync(AiSignIn? signIn, string workspaceDir,
            string sessionId, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _reads) == 1) await _firstRead.Task;
            return new AgentSessionReading(sessionId, DateTimeOffset.UtcNow);
        }

        public string? WatchDirectory(AiSignIn? signIn, string workspaceDir) => null;
    }
}
