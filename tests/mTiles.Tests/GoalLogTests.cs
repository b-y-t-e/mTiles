using System.Globalization;
using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The record one Goal tile leaves behind: what it is called, what is swept, and what a written entry
/// actually holds.
/// </summary>
/// <remarks>
/// <para>The naming and the sweep are pinned as a table for the reason <c>ChainPolicy</c> and
/// <c>UsagePace</c> are, and with rather more at stake than either: the sweep <b>deletes files</b> in a
/// directory a user can open. A changed prefix or a changed date format is invisible in a run and shows
/// up either as retention that never happens or as somebody else's file removed, months later.</para>
/// <para>The writing half goes through a real file because that is the whole of what it promises — the
/// entry is on disk, it is truncated where it is too large and it says so, and a logger that has been
/// disposed of writes nothing.</para>
/// </remarks>
public class GoalLogTests
{
    private static readonly DateTime Cutoff =
        DateTime.ParseExact("2026-09-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Theory]
    // Ours, and old enough.
    [InlineData("goal-2026-08-20-abc.log", true)]
    // Ours, and inside the window. The cutoff day itself is kept - a file written today is today's.
    [InlineData("goal-2026-09-01-abc.log", false)]
    [InlineData("goal-2026-09-04-abc.log", false)]
    // The application's own daily log, which lives elsewhere but must never be swept by this rule.
    [InlineData("mtiles-2026-08-20.log", false)]
    // Starts the way ours do and is not ours: no date where the date goes, or no separator after it.
    [InlineData("goal-notes.log", false)]
    [InlineData("goal-2026-08-20abc.log", false)]
    [InlineData("goal-2026-13-40-abc.log", false)]
    // Ours in every way but the extension - a copy somebody took by hand stays where they put it.
    [InlineData("goal-2026-08-20-abc.txt", false)]
    // The date and nothing after it: still ours, and still old.
    [InlineData("goal-2026-08-20.log", true)]
    public void Sweeps_only_its_own_expired_files(string fileName, bool expired) =>
        Assert.Equal(expired, GoalLog.IsExpired(fileName, Cutoff));

    /// <summary>
    /// The date leads the id, because that is what lets the sweep read it off the front of a name whose
    /// tail is of no fixed length.
    /// </summary>
    [Fact]
    public void Names_the_file_date_first_and_reads_it_back()
    {
        var day = DateTime.ParseExact("2026-08-20", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var name = GoalLog.NameFor(Path.Combine("goals", "deadbeef.json"), day);

        Assert.Equal("goal-2026-08-20-deadbeef.log", name);
        Assert.True(GoalLog.IsExpired(name, Cutoff));
    }

    /// <summary>
    /// A goal id is a file name here, so it goes through the same allow-list every other stored id does.
    /// </summary>
    [Fact]
    public void Keeps_a_hostile_goal_id_inside_one_path_component()
    {
        var name = GoalLog.NameFor("../../etc/passwd", DateTime.Now);

        Assert.Equal(name, Path.GetFileName(name));
        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
    }

    /// <summary>A file the sweep would delete goes; one inside the window, and one that is not ours at
    /// all, stay exactly where they were.</summary>
    [Fact]
    public void Opening_a_log_removes_the_expired_files_and_nothing_else()
    {
        using var appData = new TempAppData();
        var directory = AppPaths.GetGoalLogsDirectory();
        Directory.CreateDirectory(directory);

        var old = Written(directory, GoalLog.NameFor("gone.json",
            DateTime.Now.AddDays(-AppDefaults.LogRetentionDays - 1)));
        var recent = Written(directory, GoalLog.NameFor("kept.json", DateTime.Now.AddDays(-1)));
        var stranger = Written(directory, "goal-notes.log");

        using (new GoalLog("fresh.json")) { }

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(stranger));
    }

    /// <summary>What was written is on disk once Dispose has waited for the chain - which is the whole
    /// promise, since every caller of this class writes and moves on.</summary>
    [Fact]
    public void Writes_the_heading_and_its_body()
    {
        using var appData = new TempAppData();

        string path;
        using (var log = new GoalLog("run.json"))
        {
            path = Assert.IsType<string>(log.FilePath);
            log.Event("PHASE -> Implement");
            log.Block("PROMPT", "the whole prompt");
        }

        var text = File.ReadAllText(path);
        Assert.Contains("PHASE -> Implement", text, StringComparison.Ordinal);
        Assert.Contains("PROMPT", text, StringComparison.Ordinal);
        Assert.Contains("the whole prompt", text, StringComparison.Ordinal);
    }

    /// <summary>An entry past the cap is cut, and the cut says so - otherwise the next reader takes it
    /// for the end of the answer.</summary>
    [Fact]
    public void Truncates_an_oversized_entry_and_says_by_how_much()
    {
        using var appData = new TempAppData();

        const int Excess = 5_000;
        var body = new string('x', GoalLog.MaxEntry + Excess);

        string path;
        using (var log = new GoalLog("huge.json"))
        {
            path = Assert.IsType<string>(log.FilePath);
            log.Block("RESPONSE", body);
        }

        var text = File.ReadAllText(path);
        Assert.Contains($"truncated, {Excess} more characters", text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', GoalLog.MaxEntry + 1), text, StringComparison.Ordinal);
    }

    /// <summary>A disposed logger is silent rather than throwing: it is reached from a tile that is
    /// already being torn down, and every call site is a fire-and-forget one.</summary>
    [Fact]
    public void Writes_nothing_after_it_has_been_disposed()
    {
        using var appData = new TempAppData();

        var log = new GoalLog("closed.json");
        var path = Assert.IsType<string>(log.FilePath);
        log.Event("before");
        log.Dispose();

        log.Event("after");
        log.Dispose();

        Assert.Contains("before", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("after", File.ReadAllText(path), StringComparison.Ordinal);
    }

    private static string Written(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "old");
        return path;
    }
}
