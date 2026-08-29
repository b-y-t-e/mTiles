using System.Globalization;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a workspace row reports as the memory its tiles are holding: the walk that finds it, and the
/// wording that puts it on the row.
/// </summary>
/// <remarks>
/// Both halves are pure, which is the whole reason they are separate from the reading of the machine's
/// process table: a real reading is a fact about whichever machine the test happens to run on, and there
/// is nothing to assert about it. The table below is typed out by hand instead.
/// </remarks>
public class ProcessTreeMemoryTests
{
    private static ProcessMemoryEntry Process(int id, int parent, long megabytes) =>
        new(id, parent, megabytes * 1024 * 1024);

    private static Dictionary<string, IReadOnlyCollection<int>> Groups(
        params (string Key, int[] Roots)[] groups) =>
        groups.ToDictionary(group => group.Key, group => (IReadOnlyCollection<int>)group.Roots);

    /// <summary>The tree, not the process: a shell holds a few megabytes and the agent it started holds
    /// the other three hundred, which is the number the user is looking at.</summary>
    [Fact]
    public void Counts_everything_the_tile_started()
    {
        ProcessMemoryEntry[] table =
        [
            Process(100, 1, 5),     // the tile's shell
            Process(200, 100, 300), // the agent it started
            Process(300, 200, 40),  // and the compiler the agent started
            Process(400, 1, 999)    // somebody else's process entirely
        ];

        Assert.Equal(345L * 1024 * 1024, ProcessTreeMemory.SumTrees(table, new HashSet<int> { 100 }));
    }

    /// <summary>Two tiles in one workspace, and a process must not be counted twice because two roots
    /// reach it.</summary>
    [Fact]
    public void Counts_a_shared_descendant_once()
    {
        ProcessMemoryEntry[] table =
        [
            Process(100, 1, 5),
            Process(200, 100, 5),
            Process(300, 200, 90)
        ];

        Assert.Equal(100L * 1024 * 1024,
            ProcessTreeMemory.SumTrees(table, new HashSet<int> { 100, 200 }));
    }

    /// <summary>A process table read from a running machine can describe a loop — a parent id is reused
    /// the moment its process is reaped — and a walk without a visited set would not come back.</summary>
    [Fact]
    public void Survives_a_table_that_names_itself_as_its_own_ancestor()
    {
        ProcessMemoryEntry[] table =
        [
            Process(100, 300, 10),
            Process(200, 100, 10),
            Process(300, 200, 10)
        ];

        Assert.Equal(30L * 1024 * 1024, ProcessTreeMemory.SumTrees(table, new HashSet<int> { 100 }));
    }

    /// <summary>A tile whose shell exited between the tree walk and the reading: its id is in the roots
    /// and in no table, and the answer is nothing rather than an exception on a timer tick.</summary>
    [Fact]
    public void A_process_that_has_gone_contributes_nothing()
    {
        Assert.Equal(0, ProcessTreeMemory.SumTrees([Process(100, 1, 50)], new HashSet<int> { 999 }));
    }

    /// <summary>No roots is the common case — most workspaces are not loaded — and it must not cost a
    /// reading of the machine's whole process table every five seconds.</summary>
    [Fact]
    public void Reads_nothing_when_no_tile_is_running_anything()
    {
        var read = false;
        var probe = new ProcessTreeMemory(() => { read = true; return []; });

        var readings = probe.WorkingSetsOf(Groups(("a", []), ("b", [])));

        Assert.Equal(0, readings["a"]);
        Assert.Equal(0, readings["b"]);
        Assert.False(read);
    }

    /// <summary>Every loaded workspace out of one reading of the machine's process table: the figures
    /// then describe the same instant, and six workspaces do not cost six full scans every five
    /// seconds.</summary>
    [Fact]
    public void Reads_the_process_table_once_for_every_workspace_asked_about()
    {
        var reads = 0;
        ProcessMemoryEntry[] table =
        [
            Process(100, 1, 5),
            Process(200, 100, 300),
            Process(400, 1, 40)
        ];
        var probe = new ProcessTreeMemory(() => { reads++; return table; });

        var readings = probe.WorkingSetsOf(Groups(("first", [100]), ("second", [400]), ("idle", [])));

        Assert.Equal(1, reads);
        Assert.Equal(305L * 1024 * 1024, readings["first"]);
        Assert.Equal(40L * 1024 * 1024, readings["second"]);
        Assert.Equal(0, readings["idle"]);
    }

    /// <summary>A memory figure is worth nothing and stopping the workspace list to raise it would cost
    /// everything: a reader that throws is reported and answered with no reading.</summary>
    [Fact]
    public void A_reading_that_fails_is_no_reading_rather_than_a_crash()
    {
        var probe = new ProcessTreeMemory(() => throw new UnauthorizedAccessException("no"));

        Assert.Equal(0, probe.WorkingSetsOf(Groups(("a", [100])))["a"]);
    }

    /// <summary>The parent and the resident set out of one <c>/proc</c> line — including the one whose
    /// executable is named with the brackets the field is delimited by.</summary>
    [Theory]
    [InlineData("7 (bash) S 3 7 7 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 900 12288000 1234 " +
                "18446744073709551615 1 1 1 1 1 1 1 1 1", 3, 1234)]
    [InlineData("7 (weird ) name) S 5 7 7 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 900 12288000 99 " +
                "18446744073709551615 1 1 1 1 1 1 1 1 1", 5, 99)]
    public void Reads_a_proc_stat_line(string stat, int expectedParent, long expectedPages)
    {
        var entry = ProcessTreeMemory.ParseProcStat(stat, 7);

        Assert.NotNull(entry);
        Assert.Equal(7, entry!.Value.ProcessId);
        Assert.Equal(expectedParent, entry.Value.ParentProcessId);
        Assert.Equal(expectedPages * Environment.SystemPageSize, entry.Value.WorkingSetBytes);
    }

    /// <summary>A line cut short by a process exiting mid-read is not half an entry.</summary>
    [Fact]
    public void Refuses_a_truncated_proc_stat_line()
    {
        Assert.Null(ProcessTreeMemory.ParseProcStat("7 (bash) S 3 7 7", 7));
        Assert.Null(ProcessTreeMemory.ParseProcStat("", 7));
    }

    /// <summary>Nothing at all for nothing measured: "0 MB" is a claim that something was looked at, and
    /// the row reserves its height either way, so an empty string costs no layout.</summary>
    [Fact]
    public void Says_nothing_when_there_is_nothing_to_say()
    {
        Assert.Equal("", MemoryDisplay.Format(0));
        Assert.Equal("", MemoryDisplay.Format(-1));
    }

    /// <summary>Whole megabytes below a gigabyte, one decimal above it, and never a nothing that is
    /// really something.</summary>
    [Theory]
    [InlineData(1, "1 MB")]                       // a process holding almost nothing still holds it
    [InlineData(312 * 1024 * 1024, "312 MB")]
    [InlineData(1024L * 1024 * 1024, "1 GB")]
    [InlineData(1503238553L, "1.4 GB")]
    public void Writes_the_reading_in_the_shortest_form_that_says_it(long bytes, string expected)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, MemoryDisplay.Format(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
