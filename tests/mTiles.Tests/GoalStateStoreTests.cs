using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The rules about <em>when</em> a Goal tile writes its file — a debounce, a lock, a disposal order and
/// three flags that only ever say "do not write".
/// <para>Driven directly, with no dispatcher, no headless Avalonia session and no phase machine. That
/// is the whole reason this moved out of the view model: none of these rules is about a view, and every
/// one of them could previously only be reached by running a workflow.</para>
/// </summary>
public class GoalStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-store-" + Guid.NewGuid().ToString("N"));

    public GoalStateStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* not a test failure */ }
    }

    private string Path_(string name) => System.IO.Path.Combine(_dir, name);

    private static GoalStateStore Store(string path, GoalTileState state, List<string>? reported = null) =>
        new(path, new GoalStatePersistence())
        {
            Snapshot = () => state,
            Report = text => reported?.Add(text),
        };

    private static GoalTileState Goal(string text) => new() { OriginalGoal = text };

    [Fact]
    public void A_debounced_save_lands_and_an_immediate_one_does_not_wait_for_it()
    {
        var path = Path_("a.json");
        using var store = Store(path, Goal("the goal"));

        store.SaveSoon();
        Assert.False(File.Exists(path));

        // SaveNow does not cancel the timer's callback — Timer.Dispose makes no such promise — it makes
        // the callback redundant: every writer serialises the state as it stands when it runs.
        store.SaveNow();
        Assert.Contains("the goal", File.ReadAllText(path));
    }

    [Fact]
    public void Closing_flushes_what_the_debounce_was_still_holding()
    {
        var path = Path_("b.json");
        var store = Store(path, Goal("answered a moment ago"));

        store.SaveSoon();
        store.Dispose();

        // A tile closed a moment after the tool answered must not lose that answer to a timer that
        // never got to fire.
        Assert.Contains("answered a moment ago", File.ReadAllText(path));
    }

    [Fact]
    public void Closing_a_store_with_nothing_to_say_leaves_no_file()
    {
        var path = Path_("c.json");
        var store = Store(path, Goal(""));

        store.Dispose(flush: false);

        // .mterminal/goals/ lives in the user's repository and nothing ever prunes it.
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_stop_reason_from_a_newer_build_does_not_cost_the_user_their_session()
    {
        var path = Path_("downgraded.json");

        // What a build one version ahead would write. Enums are stored as names, so an unknown one is a
        // JsonException — which the persistence layer rightly reads as a damaged file and sets aside.
        // The ordinary way this happens is a downgrade, and deleting a session over a field whose only
        // job is to decide whether one button appears is wildly out of proportion.
        File.WriteAllText(path,
            """{"OriginalGoal":"a real session","LastStopReason":"SomethingInventedLater"}""");

        using var store = Store(path, Goal("x"));

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("a real session", loaded!.OriginalGoal);

        // Null, not the first member: null is the file's own way of saying no run has finished here, so
        // the unreadable value degrades into the one state that claims nothing and offers nothing.
        Assert.Null(loaded.LastStopReason);
    }

    [Fact]
    public void A_note_the_tile_wrote_about_itself_does_not_create_a_session_file()
    {
        var path = Path_("notes-only.json");
        var state = new GoalTileState
        {
            Messages =
            {
                new GoalMessage { Role = GoalMessageRole.System, Text = "This tile cannot ask whether to discard." },
            },
        };

        using var store = new GoalStateStore(path, new GoalStatePersistence())
        {
            Snapshot = () => state,
            Report = _ => { },
        };

        store.SaveNow();

        // The same question GoalTilePolicy.WorthConfirming asks, so the two cannot disagree: a tile
        // nobody has set a goal in is not a session, whatever it has said to itself. .mterminal/goals/
        // lives in the user's repository and nothing ever prunes it.
        Assert.False(File.Exists(path));

        state.Messages.Add(new GoalMessage { Role = GoalMessageRole.User, Text = "a real goal" });
        store.SaveNow();
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Nothing_is_written_after_closing_however_late_it_is_asked()
    {
        var path = Path_("d.json");
        var store = Store(path, Goal("first"));

        store.SaveNow();
        store.Dispose(flush: false);

        // The workflow keeps unwinding after the tile closes and its phase changes still call through.
        var state = Goal("written after the tile was gone");
        var late = new GoalStateStore(path, new GoalStatePersistence())
        {
            Snapshot = () => state,
            Report = _ => { },
        };
        late.Dispose(flush: false);

        store.SaveNow();
        store.SaveSoon();
        Thread.Sleep(AppDefaults.SaveDebounceMs * 2);

        Assert.Contains("first", File.ReadAllText(path));
    }

    [Fact]
    public void A_file_that_cannot_be_opened_stops_the_store_writing_for_good()
    {
        var path = Path_("held.json");
        File.WriteAllText(path, "{\"OriginalGoal\":\"a real session\"}");

        // Held open the way a backup tool holds a file for a moment. The content is fine; only the
        // opening fails — and the tile in front of it is empty, so saving would replace a real session
        // with the blank one that failed to load it.
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var store = Store(path, Goal("the empty tile in front of it"));

            Assert.Throws<GoalStateUnavailableException>(() => store.Load());

            store.SaveNow();
        }

        Assert.Contains("a real session", File.ReadAllText(path));
    }

    [Fact]
    public void A_damaged_file_is_set_aside_and_the_store_carries_on_writing()
    {
        var path = Path_("damaged.json");
        File.WriteAllText(path, "{ this is not json");

        var reported = new List<string>();
        using var store = Store(path, Goal("a fresh start"), reported);

        Assert.Throws<GoalStateUnreadableException>(() => store.Load());

        // Refusing here would punish a damaged file more harshly than an unreadable one, which is
        // backwards: the damaged one has already been moved out of the way.
        store.SaveNow();
        Assert.Contains("a fresh start", File.ReadAllText(path));
        Assert.Empty(reported);
    }

    [Fact]
    public void A_write_that_fails_is_said_out_loud_exactly_once()
    {
        // A directory where the file should be: every write fails, and keeps failing.
        var path = Path_("blocked.json");
        Directory.CreateDirectory(path);

        var reported = new List<string>();
        using var store = Store(path, Goal("nowhere to put this"), reported);

        store.SaveNow();
        store.SaveNow();
        store.SaveNow();

        // The tile that cannot save is the one whose user most needs to know — once. A transcript is
        // not a log.
        Assert.Single(reported);
        Assert.Contains("will not survive a restart", reported[0]);
    }
}
