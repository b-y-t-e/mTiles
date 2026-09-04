using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The live mirror, against a real <see cref="System.IO.FileSystemWatcher"/> — loop prevention here is
/// a cache compared against actual disk state, so a fake clock or a fake watcher would not exercise the
/// thing that matters: a write this engine makes must not cause it to write again.
/// </summary>
[Collection(AgentFileSyncTests.CollectionName)]
public sealed class AgentFileSyncEngineTests : IAsyncLifetime
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "mtiles-agentsync-" + Guid.NewGuid().ToString("N"));
    private AgentFileSyncEngine? _engine;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _engine?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory */ }
        return Task.CompletedTask;
    }

    private string Claude => Path.Combine(_dir, AgentFileSyncEngine.ClaudeFileName);
    private string Agents => Path.Combine(_dir, AgentFileSyncEngine.AgentsFileName);

    /// <summary>Polls briefly rather than sleeping a fixed amount: the watcher's debounce plus the
    /// reconcile pass is asynchronous, and a fixed sleep is either flaky under load or slower than it
    /// needs to be on a quiet machine.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.True(condition(), "Condition was not met within the timeout.");
    }

    /// <summary>A file held open by an editor, an antivirus or a cloud-sync client cannot be read, and
    /// the event that would have led here has already been consumed — so the reconcile has to ask again
    /// itself, or the two sides quietly disagree until somebody happens to save one of them.</summary>
    [Fact]
    public async Task A_file_that_was_locked_for_a_moment_is_still_mirrored()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        _engine = new AgentFileSyncEngine(_dir);
        await _engine.StartAsync();

        var held = new FileStream(Agents, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            File.WriteAllText(Claude, "two");
            // Long enough for the debounce to fire and find AGENTS.md unreadable at least once. The
            // handle denies every kind of sharing, so the mirror cannot have run while it was held.
            await Task.Delay(700);
        }
        finally
        {
            held.Dispose();
        }

        // Nothing touches either file from here on: the retry the engine armed is what has to notice.
        await WaitUntilAsync(() => File.ReadAllText(Agents) == "two");
    }

    /// <summary>A run that spent its whole budget of attempts against a file some editor was holding
    /// must not hand the next run a counter already at its limit: started again — which is what the
    /// global switch and an answer arriving for a live mirror both do — it would give up without
    /// arming a single retry, and the two files would stay apart until somebody saved one by hand.
    /// </summary>
    [Fact]
    public async Task A_restarted_engine_gets_its_own_budget_of_attempts()
    {
        File.WriteAllText(Agents, "one");
        File.WriteAllText(Claude, "two");
        // Named rather than left to the clock: with both files changed against an empty cache the
        // newest wins, and two writes a millisecond apart do not say which that is.
        File.SetLastWriteTimeUtc(Claude, DateTime.UtcNow.AddSeconds(5));

        using var held = new FileStream(Agents, FileMode.Open, FileAccess.Read, FileShare.None);
        _engine = new AgentFileSyncEngine(_dir);

        // The whole of this run's budget is spent against a handle that denies every kind of sharing.
        await _engine.StartAsync();
        await Task.Delay(AppDefaults.WatcherDebounceMs * (5 + 2));

        _engine.Stop();
        await _engine.StartAsync();
        held.Dispose();

        // Nothing touches either file from here on: a retry the second run armed is what has to
        // notice that AGENTS.md can be read again.
        await WaitUntilAsync(() => File.ReadAllText(Agents) == "two", timeoutMs: 5000);
    }

    [Fact]
    public async Task Editing_claude_md_mirrors_into_agents_md()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        _engine = new AgentFileSyncEngine(_dir);
        await _engine.StartAsync();

        File.WriteAllText(Claude, "two");

        await WaitUntilAsync(() => File.Exists(Agents) && File.ReadAllText(Agents) == "two");
    }

    [Fact]
    public async Task Editing_agents_md_mirrors_into_claude_md()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        _engine = new AgentFileSyncEngine(_dir);
        await _engine.StartAsync();

        File.WriteAllText(Agents, "two");

        await WaitUntilAsync(() => File.Exists(Claude) && File.ReadAllText(Claude) == "two");
    }

    [Fact]
    public async Task Deleting_one_file_recreates_it_from_the_other()
    {
        File.WriteAllText(Claude, "keep me");
        File.WriteAllText(Agents, "keep me");
        _engine = new AgentFileSyncEngine(_dir);
        await _engine.StartAsync();

        File.Delete(Claude);

        await WaitUntilAsync(() => File.Exists(Claude) && File.ReadAllText(Claude) == "keep me");
    }

    [Fact]
    public async Task Deleting_both_does_not_crash_and_a_later_creation_reseeds_the_mirror()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        _engine = new AgentFileSyncEngine(_dir);
        await _engine.StartAsync();

        File.Delete(Claude);
        File.Delete(Agents);
        await Task.Delay(600); // let the delete events settle without either file existing

        File.WriteAllText(Claude, "reborn");

        await WaitUntilAsync(() => File.Exists(Agents) && File.ReadAllText(Agents) == "reborn");
    }

    [Fact]
    public async Task A_disabled_engine_never_mirrors()
    {
        File.WriteAllText(Claude, "one");
        _engine = new AgentFileSyncEngine(_dir);
        // Never started.

        File.WriteAllText(Claude, "two");
        await Task.Delay(600);

        Assert.False(File.Exists(Agents));
    }

    /// <summary>Nothing watches while the application is closed, so a pull, a checkout or an edit in
    /// another tool can leave the two apart. Seeding the cache with that state would make the
    /// disagreement the engine's idea of "unchanged" — and the first later edit of one side would
    /// silently overwrite the other side's offline changes.</summary>
    [Fact]
    public async Task Starting_on_files_that_drifted_apart_while_nothing_watched_propagates_the_newer_one()
    {
        File.WriteAllText(Agents, "from the pull");
        File.WriteAllText(Claude, "stale");
        File.SetLastWriteTimeUtc(Claude, DateTime.UtcNow.AddMinutes(-5));

        _engine = new AgentFileSyncEngine(_dir);
        await _engine.StartAsync();

        await WaitUntilAsync(() => File.ReadAllText(Claude) == "from the pull");
    }

    /// <summary>The other half of the same start: a file that appeared while nothing watched is
    /// mirrored rather than treated as the state to keep.</summary>
    [Fact]
    public async Task Starting_with_only_one_of_the_two_present_seeds_the_other()
    {
        File.WriteAllText(Claude, "only this one");

        _engine = new AgentFileSyncEngine(_dir);
        await _engine.StartAsync();

        await WaitUntilAsync(() => File.Exists(Agents) && File.ReadAllText(Agents) == "only this one");
    }

    [Fact]
    public async Task Stopping_and_restarting_reseeds_from_disk_rather_than_trusting_stale_state()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        _engine = new AgentFileSyncEngine(_dir);
        await _engine.StartAsync();
        _engine.Stop();

        // Changed while stopped — nothing should have seen this.
        File.WriteAllText(Claude, "changed while stopped");

        await _engine.StartAsync();
        File.WriteAllText(Agents, "after restart");

        await WaitUntilAsync(() => File.ReadAllText(Claude) == "after restart");
    }

    /// <summary>Stopping while a start is in flight leaves nothing watching. The window is real: the
    /// seeding is awaited before the watcher exists, so a Stop landing in it used to find no watcher to
    /// take down and the start then built one for an engine nobody would ever stop again.</summary>
    [Fact]
    public async Task Stopping_during_a_start_leaves_nothing_watching()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        _engine = new AgentFileSyncEngine(_dir);

        var starting = _engine.StartAsync();
        _engine.Stop();
        await starting;

        Assert.False(_engine.IsRunning);

        File.WriteAllText(Claude, "two");
        await Task.Delay(600);

        Assert.Equal("one", File.ReadAllText(Agents));
    }
    /// <summary>The wizard's answer is the engine's to act on: seeding from an explicitly named file is
    /// the same reconcile as every later edit, so the rule lives in one class rather than being spelled
    /// a second time in the coordinator.</summary>
    [Fact]
    public async Task Starting_with_an_authoritative_file_overwrites_the_other_whatever_the_mtimes_say()
    {
        File.WriteAllText(Claude, "the one the user picked");
        File.WriteAllText(Agents, "newer but not chosen");
        File.SetLastWriteTimeUtc(Claude, DateTime.UtcNow.AddMinutes(-5));

        _engine = new AgentFileSyncEngine(_dir);
        await _engine.StartAsync(AgentFileSyncEngine.ClaudeFileName);

        await WaitUntilAsync(() => File.ReadAllText(Agents) == "the one the user picked");
    }

    /// <summary>The seeding write is the one that can replace content this application has never
    /// carried — an AGENTS.md nobody has committed — so it is copied aside first. Every mirror after it
    /// is the sync the user switched on and leaves no copy behind.</summary>
    [Fact]
    public async Task Seeding_over_a_file_that_disagreed_keeps_a_copy_of_what_it_replaced()
    {
        File.WriteAllText(Claude, "the one the user picked");
        File.WriteAllText(Agents, "hours of uncommitted work");

        _engine = new AgentFileSyncEngine(_dir);
        await _engine.StartAsync(AgentFileSyncEngine.ClaudeFileName);
        await WaitUntilAsync(() => File.ReadAllText(Agents) == "the one the user picked");

        var backup = Assert.Single(Directory.GetFiles(_dir, "AGENTS.md.pre-sync-*"));
        Assert.Equal("hours of uncommitted work", File.ReadAllText(backup));

        File.WriteAllText(Claude, "an ordinary later edit");
        await WaitUntilAsync(() => File.ReadAllText(Agents) == "an ordinary later edit");
        Assert.Single(Directory.GetFiles(_dir, "*.pre-sync-*"));
    }

    /// <summary>A reconcile queued under the previous run must not outlive it. The coordinator hands an
    /// answer to a live mirror by stopping and starting the same engine, and a callback already in
    /// flight would see <c>IsRunning</c> true again and resolve the pair by mtime — overwriting the very
    /// file the user has just named. The window is milliseconds wide, so it is hit by repetition rather
    /// than arranged; what is asserted is the contract, which is that the answer stands.</summary>
    [Fact]
    public async Task An_answer_arriving_for_a_live_mirror_is_not_overruled_by_a_queued_reconcile()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        _engine = new AgentFileSyncEngine(_dir);
        await _engine.StartAsync();

        for (var round = 0; round < 10; round++)
        {
            // An external edit on each side, the second one newer — so a reconcile deciding by mtime
            // alone would carry AGENTS.md over the file named below.
            File.WriteAllText(Claude, $"the user's answer {round}");
            File.WriteAllText(Agents, $"the side that must not win {round}");

            _engine.Stop();
            await _engine.StartAsync(AgentFileSyncEngine.ClaudeFileName);

            var expected = $"the user's answer {round}";
            await WaitUntilAsync(() => ReadOrNull(Agents) == expected);
        }

        return;

        // The mirror may hold the file for the moment it writes it, which says nothing about the
        // property under test.
        static string? ReadOrNull(string path)
        {
            try { return File.ReadAllText(path); } catch (IOException) { return null; }
        }
    }

    /// <summary>An edit that lands between the engine's own write and the read that re-stamps its cache
    /// used to be remembered as the engine's own write, so it read as "unchanged" for ever and the two
    /// files stayed apart while the contract says they are identical. The window is milliseconds wide,
    /// so it is hit by hammering rather than by arranging it; what is asserted is the contract, which
    /// is that the two converge.</summary>
    [Fact]
    public async Task An_edit_landing_while_the_mirror_writes_is_not_lost()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        _engine = new AgentFileSyncEngine(_dir);
        await _engine.StartAsync();

        for (var round = 0; round < 20; round++)
        {
            Write(Claude, $"source {round}");
            Write(Agents, $"target {round}");
            await Task.Delay(20);
        }

        await WaitUntilAsync(SidesAgree, timeoutMs: 5000);
        return;

        // Both sides are being written by the mirror at the same time, so a share violation here is the
        // ordinary case and says nothing about the property under test.
        static void Write(string path, string content)
        {
            try { File.WriteAllText(path, content); } catch (IOException) { /* the mirror had it */ }
        }

        bool SidesAgree()
        {
            try { return File.ReadAllText(Claude) == File.ReadAllText(Agents); }
            catch (IOException) { return false; }
        }
    }

    /// <summary>A watcher error — a native buffer overflow above all, which a large checkout at the
    /// workspace root reaches, because the name filters are applied only in managed code — is lost
    /// events, not a dead mirror. The engine rebuilds itself rather than leaving the two files free to
    /// drift for the rest of the session while the config still reads enabled.</summary>
    [Fact]
    public async Task A_failing_watcher_is_rebuilt_and_the_mirror_survives()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        _engine = new AgentFileSyncEngine(_dir);
        await _engine.StartAsync();

        // An edit the failing watcher never carried across — the lost-events window the rebuild has
        // to heal, the same reconcile an offline window gets.
        File.WriteAllText(Claude, "two");

        _engine.FailWatcher(new ErrorEventArgs(new InternalBufferOverflowException()));

        // The rebuild's synchronous prefix runs before FailWatcher returns, so the engine is live
        // again on a fresh watcher even though its seeding is still in flight — and the seeding is
        // what carries the edit across.
        Assert.True(_engine.IsRunning);
        await WaitUntilAsync(() => File.ReadAllText(Agents) == "two");

        // And the fresh watcher is the one watching from here on.
        File.WriteAllText(Claude, "three");
        await WaitUntilAsync(() => File.ReadAllText(Agents) == "three");
    }

    /// <summary>A filesystem whose watchers die as fast as they are raised must not be rebuilt for
    /// ever: bounded the way the launch chain is bounded, a rate over a window rather than a running
    /// total, and past it the engine stops instead of spinning.</summary>
    [Fact]
    public async Task A_watcher_that_keeps_failing_is_not_rebuilt_for_ever()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        _engine = new AgentFileSyncEngine(_dir);
        await _engine.StartAsync();

        for (var i = 0; i < 3; i++)
            _engine.FailWatcher(new ErrorEventArgs(new IOException("watcher broken")));

        Assert.True(_engine.IsRunning);

        _engine.FailWatcher(new ErrorEventArgs(new IOException("watcher broken")));
        Assert.False(_engine.IsRunning);

        File.WriteAllText(Claude, "two");
        await Task.Delay(600); // past the debounce, and nothing is watching to react to the edit

        Assert.Equal("one", File.ReadAllText(Agents));
    }

    /// <summary>A reconcile caught inside the gate by the stop-and-restart that carries the user's
    /// answer must not go on to settle the pair by mtime: the seeding that follows would find the two
    /// already agreeing and never apply the answer, which would then be lost without even the copy the
    /// seeding write keeps. The window is the width of two file reads, so it is held open by a hook
    /// rather than hit by repetition.</summary>
    [Fact]
    public async Task A_reconcile_caught_inside_the_gate_by_an_answer_does_not_settle_the_pair_by_mtime()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        _engine = new AgentFileSyncEngine(_dir);
        await _engine.StartAsync();

        // An external edit on each side, the one that must not win the newer — so a reconcile deciding
        // by mtime alone carries it over the file named below. Named rather than left to the clock,
        // the way every test here that depends on the ordering names it.
        File.WriteAllText(Claude, "the user's answer");
        File.WriteAllText(Agents, "the side that must not win");
        File.SetLastWriteTimeUtc(Claude, DateTime.UtcNow.AddMinutes(-5));

        var inside = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _engine.ReconcilePauseForTests = () =>
        {
            inside.TrySetResult();
            return release.Task;
        };

        // The edits' debounced reconcile, now inside the gate with both reads behind it.
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The restarted run's own reconcile must not pause.
        _engine.ReconcilePauseForTests = null;
        _engine.Stop();
        var restarted = _engine.StartAsync(AgentFileSyncEngine.ClaudeFileName);
        release.TrySetResult();
        await restarted;

        await WaitUntilAsync(() => File.ReadAllText(Agents) == "the user's answer");
        Assert.Equal("the user's answer", File.ReadAllText(Claude));
    }
}
