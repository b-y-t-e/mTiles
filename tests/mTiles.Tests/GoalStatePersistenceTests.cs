using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The Goal tile's file is the whole record of a session, and it is rewritten at every message and
/// every phase change. These pin what happens when a write or a read goes wrong: the file is never
/// left half-written, a damaged one is kept rather than quietly replaced, and one that merely could
/// not be opened is not touched at all.
/// </summary>
public class GoalStatePersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-goal-" + Guid.NewGuid().ToString("N"));
    private readonly GoalStatePersistence _persistence = new();

    public GoalStatePersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* a temp directory that outlives the run is not a test failure */ }
    }

    private string File_(string name) => Path.Combine(_dir, name);

    [Fact]
    public void A_saved_session_comes_back_whole()
    {
        var path = File_("goal.json");
        _persistence.Save(path, new GoalTileState
        {
            OriginalGoal = "the goal",
            ApprovedPlan = "the plan",
            CurrentPhase = GoalPhase.Implement,
            IterationCount = 2,
            SelectedToolName = "Claude Code",
            Messages = [new GoalMessage { Role = GoalMessageRole.Assistant, Text = "done", Phase = GoalPhase.Implement }]
        });

        var loaded = _persistence.Load(path)!;

        Assert.Equal("the goal", loaded.OriginalGoal);
        Assert.Equal("the plan", loaded.ApprovedPlan);
        Assert.Equal(GoalPhase.Implement, loaded.CurrentPhase);
        Assert.Equal(2, loaded.IterationCount);
        Assert.Single(loaded.Messages);
    }

    [Fact]
    public void A_missing_file_is_a_new_tile_rather_than_an_error()
    {
        Assert.Null(_persistence.Load(File_("never-written.json")));
    }

    [Fact]
    public void Saving_leaves_no_temporary_file_behind()
    {
        var path = File_("goal.json");
        _persistence.Save(path, new GoalTileState { OriginalGoal = "one" });
        _persistence.Save(path, new GoalTileState { OriginalGoal = "two" });

        // The write goes through a temporary file and a move, so a crash cannot truncate the real one.
        // The temporary must not survive the move, or the directory fills with one per message.
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Equal("two", _persistence.Load(path)!.OriginalGoal);
    }

    [Fact]
    public void Concurrent_saves_leave_one_readable_file()
    {
        // A message can be added from a background thread while a phase changes on the UI thread, so
        // two saves really can be in flight at once. They used to share one .tmp path, where the second
        // truncates what the first is about to move.
        var path = File_("goal.json");

        // Fifty is plenty: the writes are serialised by the lock, so this is a check that they
        // serialise rather than a race to be won, and a large count only makes it slow.
        Parallel.For(0, 50, i =>
            _persistence.Save(path, new GoalTileState { OriginalGoal = "goal", IterationCount = i }));

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.NotNull(_persistence.Load(path));
    }

    [Fact]
    public void A_damaged_file_is_kept_and_the_caller_is_told()
    {
        var path = File_("goal.json");
        File.WriteAllText(path, "{ \"OriginalGoal\": \"truncated half-way thro");

        var ex = Assert.Throws<GoalStateUnreadableException>(() => _persistence.Load(path));

        // Not a silent null: the tile's next act is to save over this file, so swallowing the failure
        // replaced a session with an empty one and left a log line as the only evidence it existed.
        Assert.NotNull(ex.KeptAt);
        Assert.True(File.Exists(ex.KeptAt));
        Assert.False(File.Exists(path));
        Assert.Contains("truncated half-way thro", File.ReadAllText(ex.KeptAt!));
    }

    [Fact]
    public void A_file_holding_literally_null_is_damaged_rather_than_absent()
    {
        // It parses without complaint and deserialises to nothing, so it used to be indistinguishable
        // from a file that was never written: the tile opened empty and then saved over it, which is
        // the outcome this whole class exists to prevent.
        var path = File_("goal.json");
        File.WriteAllText(path, "null");

        var ex = Assert.Throws<GoalStateUnreadableException>(() => _persistence.Load(path));

        Assert.NotNull(ex.KeptAt);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Damaged_copies_do_not_pile_up_in_the_users_repository()
    {
        // This directory lives inside the user's repository, and the stamp is only accurate to the
        // second, so both halves are tested at once: nothing overwrites an earlier rescue, and the
        // rescues stop at five.
        var path = File_("goal.json");

        for (var i = 0; i < 8; i++)
        {
            File.WriteAllText(path, "{ not json " + i);
            Assert.Throws<GoalStateUnreadableException>(() => _persistence.Load(path));
        }

        Assert.Equal(5, Directory.GetFiles(_dir, "goal.json.bad-*").Length);
    }

    [Fact]
    public void An_old_temporary_left_by_a_crash_is_cleared_away()
    {
        var path = File_("goal.json");
        var stale = path + ".deadbeefdeadbeefdeadbeefdeadbeef.tmp";
        File.WriteAllText(stale, "half a session");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(1));

        var fresh = path + ".cafecafecafecafecafecafecafecafe.tmp";
        File.WriteAllText(fresh, "a write happening right now");

        _persistence.Save(path, new GoalTileState { OriginalGoal = "goal" });

        Assert.False(File.Exists(stale));
        // Not the one that might belong to a write in flight somewhere else.
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void A_file_that_cannot_be_opened_is_left_exactly_where_it_is()
    {
        var path = File_("goal.json");
        _persistence.Save(path, new GoalTileState { OriginalGoal = "a real session" });

        // Held open for writing with no sharing: what another process, or a backup tool, does to a file
        // for a moment. The content is fine; only the opening failed.
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<GoalStateUnavailableException>(() => _persistence.Load(path));
        }

        // Untouched — not moved aside, not truncated. Destroying a session because of a transient
        // failure to open it is the one outcome worse than not loading it.
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.bad-*"));
        Assert.Equal("a real session", _persistence.Load(path)!.OriginalGoal);
    }
}
