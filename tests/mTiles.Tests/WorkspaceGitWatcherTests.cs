using Xunit;
using mTiles.Services;

namespace mTiles.Tests;

/// <summary>
/// The watcher every tile in a workspace shares. Driven against a real directory, because what is being
/// pinned is that a change on disk reaches <em>every</em> subscriber and that a subscriber who has gone
/// hears nothing — neither of which a fake would be evidence for.
/// </summary>
public sealed class WorkspaceGitWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "mtiles-watch-" + Guid.NewGuid().ToString("N"));

    public WorkspaceGitWatcherTests()
    {
        Directory.CreateDirectory(_dir);
        // GitDirectoryWatcher watches the working tree only once there is a repository beside it.
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a watcher may still hold it */ }
    }

    /// <summary>Long enough for the notification's own debounce plus a slow file system.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static bool Wait(CountdownEvent signal) => signal.Wait(Patience);

    private void Touch(string name) =>
        File.WriteAllText(Path.Combine(_dir, name), Guid.NewGuid().ToString());

    /// <summary>A watcher over the test's own directory, told what git ignores rather than asking it.
    /// The default source is git itself, and a temporary folder with an empty <c>.git</c> in it is not
    /// a repository — the point being pinned here is what the watcher does with the answer, not how it
    /// gets one.</summary>
    private WorkspaceGitWatcher Watching(params string[] ignoredDirs) =>
        new(_dir, new StubIgnoredDirectories(ignoredDirs));

    private sealed class StubIgnoredDirectories(IEnumerable<string> dirs) : IIgnoredDirectorySource
    {
        public Task<HashSet<string>> GetIgnoredDirsAsync(CancellationToken ct = default) =>
            Task.FromResult(new HashSet<string>(dirs, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_subscriber_hears_one_change()
    {
        using var watcher = Watching();
        using var both = new CountdownEvent(2);

        using var first = watcher.Subscribe(() => { if (!both.IsSet) both.Signal(); });
        using var second = watcher.Subscribe(() => { if (!both.IsSet) both.Signal(); });

        Touch("a.txt");

        Assert.True(Wait(both), "both subscribers should have been told the tree changed");
    }

    [Fact]
    public void A_disposed_subscriber_hears_nothing_and_the_others_still_do()
    {
        using var watcher = Watching();
        using var stillListening = new CountdownEvent(1);
        var goneWasCalled = false;

        var gone = watcher.Subscribe(() => goneWasCalled = true);
        using var kept = watcher.Subscribe(() => { if (!stillListening.IsSet) stillListening.Signal(); });

        gone.Dispose();
        Touch("b.txt");

        Assert.True(Wait(stillListening), "the remaining subscriber should still have been told");
        Assert.False(goneWasCalled);
    }

    /// <summary>
    /// The last subscriber leaving stops the watch, and a later one starts it again — which is the whole
    /// point of the sharing: a workspace whose git and goal tiles are both closed watches nothing, and
    /// opening one of them again does not need the workspace rebuilt.
    /// </summary>
    [Fact]
    public void The_watch_comes_back_after_the_last_subscriber_left()
    {
        using var watcher = Watching();

        watcher.Subscribe(() => { }).Dispose();

        using var again = new CountdownEvent(1);
        using var second = watcher.Subscribe(() => { if (!again.IsSet) again.Signal(); });

        Touch("c.txt");

        Assert.True(Wait(again), "a subscriber taken after the watch stopped should still be told");
    }

    /// <summary>
    /// A subscriber's ignored directories leave the union with it. Only the git tile computes them, so
    /// a workspace whose git tile has closed must not go on treating its answer as current — the goal
    /// tile listening beside it would stop hearing about a directory git no longer ignores.
    /// </summary>
    [Fact]
    public void An_ignored_directory_stops_being_ignored_when_its_subscriber_goes()
    {
        var ignoredDir = Path.Combine(_dir, "ignored");
        Directory.CreateDirectory(ignoredDir);

        using var watcher = Watching();

        var ignoring = watcher.Subscribe(() => { });
        ignoring.UpdateIgnoredDirs([ignoredDir]);

        using var heard = new CountdownEvent(1);
        using var listener = watcher.Subscribe(() => { if (!heard.IsSet) heard.Signal(); });

        ignoring.Dispose();
        File.WriteAllText(Path.Combine(ignoredDir, "d.txt"), "x");

        Assert.True(Wait(heard), "the ignore list went with the subscriber that supplied it");
    }

    /// <summary>
    /// What git ignores is the watcher's own business, and a workspace holding no git tile is the case
    /// it exists for: nothing there computes an ignore list, so without this every write into
    /// <c>obj/</c> during a build in the terminal next door woke the Goal tile's detect buttons.
    /// </summary>
    [Fact]
    public void An_ignored_directory_is_quiet_even_when_no_subscriber_supplies_one()
    {
        var ignoredDir = Path.Combine(_dir, "obj");
        Directory.CreateDirectory(ignoredDir);

        using var watcher = Watching(ignoredDir);
        var heardAboutIgnored = false;
        using var heardAboutTheRest = new CountdownEvent(1);

        using var subscription = watcher.Subscribe(() =>
        {
            if (!File.Exists(Path.Combine(_dir, "f.txt"))) heardAboutIgnored = true;
            else if (!heardAboutTheRest.IsSet) heardAboutTheRest.Signal();
        });

        // The answer is fetched off the subscription, so give it a moment to land before writing.
        Thread.Sleep(1000);
        File.WriteAllText(Path.Combine(ignoredDir, "build.tmp"), "x");
        Thread.Sleep(1500);

        Assert.False(heardAboutIgnored, "a write into an ignored directory is not a change to report");

        Touch("f.txt");
        Assert.True(Wait(heardAboutTheRest), "a write outside it still is");
    }

    /// <summary>
    /// A workspace can become a repository after its tiles were built — "Create repository" on its row,
    /// or a clone into the folder. Nothing else retries: the git tile is the only caller of
    /// <c>UpdateIgnoredDirs</c>, so a workspace holding a Goal tile alone would stay deaf for the rest
    /// of the session.
    /// </summary>
    [Fact]
    public void A_workspace_that_becomes_a_repository_starts_being_watched()
    {
        var notYetARepository = Path.Combine(_dir, "later");
        Directory.CreateDirectory(notYetARepository);

        using var watcher = new WorkspaceGitWatcher(
            notYetARepository, new StubIgnoredDirectories([]));
        using var heard = new CountdownEvent(1);
        using var subscription = watcher.Subscribe(() => { if (!heard.IsSet) heard.Signal(); });

        Directory.CreateDirectory(Path.Combine(notYetARepository, ".git"));

        // The repository appeared before anything was watching, so it raises nothing by itself: what is
        // being pinned is that the change *after* it is heard. Long enough for the poll to come round.
        Thread.Sleep(TimeSpan.FromSeconds(8));
        File.WriteAllText(Path.Combine(notYetARepository, "g.txt"), "x");

        Assert.True(Wait(heard), "the watch should have started once the repository appeared");
    }

    [Fact]
    public void Nothing_is_raised_after_the_watcher_is_disposed()
    {
        var watcher = Watching();
        var called = false;
        using var subscription = watcher.Subscribe(() => called = true);

        watcher.Dispose();
        Touch("e.txt");
        Thread.Sleep(1500);

        Assert.False(called);

        // And the handle a tile still holds is safe to dispose afterwards, which is the order a closing
        // workspace produces: the context's watcher goes, then the tiles unwind.
        subscription.Dispose();
    }
}
