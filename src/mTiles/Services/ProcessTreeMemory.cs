using System.Diagnostics;
using System.Runtime.InteropServices;

namespace mTiles.Services;

/// <summary>One process as the machine's process table describes it, for the purposes of this file.</summary>
/// <param name="ProcessId">The process.</param>
/// <param name="ParentProcessId">What started it — how a shell's descendants are found.</param>
/// <param name="WorkingSetBytes">What it is holding in physical memory.</param>
public readonly record struct ProcessMemoryEntry(int ProcessId, int ParentProcessId, long WorkingSetBytes);

/// <summary>
/// How much memory a set of processes and everything they started are holding.
/// </summary>
/// <remarks>
/// An interface so the thing that samples it periodically — the workspace list — depends on the question
/// and not on toolhelp snapshots and <c>/proc</c>. It is also the seam a test uses, since a real reading
/// is a fact about the machine the test happens to run on.
/// </remarks>
public interface IProcessMemoryProbe
{
    /// <summary>The working set of each group's processes and all their descendants, in bytes.</summary>
    /// <remarks>Every group at once, and deliberately not one call per group: the answer comes out of
    /// the machine's whole process table, so asking six times would read that table six times to answer
    /// six questions about the same instant. The result has an entry for every key that was asked
    /// about, zero included, so the caller never has to tell "nothing running" from "not answered".</remarks>
    IReadOnlyDictionary<string, long> WorkingSetsOf(
        IReadOnlyDictionary<string, IReadOnlyCollection<int>> rootProcessIdsByGroup);
}

/// <summary>
/// One reading of the machine's process table, indexed by who started whom.
/// </summary>
/// <remarks>
/// Built once and asked many times, which is the whole point: every loaded workspace's figure describes
/// the same instant and costs one walk between them, where a sum per workspace read the table again for
/// each. Pure, so the walk can be argued about in a test with a table typed out by hand.
/// </remarks>
internal sealed class ProcessTree
{
    private readonly Dictionary<int, ProcessMemoryEntry> _byProcessId;
    private readonly Dictionary<int, List<ProcessMemoryEntry>> _children;

    private ProcessTree(Dictionary<int, ProcessMemoryEntry> byProcessId,
        Dictionary<int, List<ProcessMemoryEntry>> children)
    {
        _byProcessId = byProcessId;
        _children = children;
    }

    internal static ProcessTree Of(IReadOnlyList<ProcessMemoryEntry> table)
    {
        var byProcessId = new Dictionary<int, ProcessMemoryEntry>(table.Count);
        var children = new Dictionary<int, List<ProcessMemoryEntry>>();
        foreach (var entry in table)
        {
            // A duplicate id cannot happen in one honest reading; last one wins rather than throwing,
            // because a table read from a running machine is not owed to us.
            byProcessId[entry.ProcessId] = entry;
            if (!children.TryGetValue(entry.ParentProcessId, out var list))
                children[entry.ParentProcessId] = list = [];
            list.Add(entry);
        }

        return new ProcessTree(byProcessId, children);
    }

    /// <summary>Adds up the roots and everything descended from them, counting nobody twice.</summary>
    /// <remarks>The visited set is not only tidiness: a process table read while the machine is running
    /// can describe a process whose parent id has been reused, and a walk without one would not come
    /// back.</remarks>
    internal long WorkingSetOf(IReadOnlyCollection<int> rootProcessIds)
    {
        if (rootProcessIds.Count == 0) return 0;

        var visited = new HashSet<int>();
        var pending = new Stack<ProcessMemoryEntry>();
        foreach (var rootProcessId in rootProcessIds)
        {
            // A root the table does not name is a shell that exited between the tile tree being walked
            // and this reading: nothing to add, and nothing to raise about it either.
            if (_byProcessId.TryGetValue(rootProcessId, out var root))
                pending.Push(root);
        }

        var total = 0L;
        while (pending.Count > 0)
        {
            var entry = pending.Pop();
            if (!visited.Add(entry.ProcessId)) continue;

            total += entry.WorkingSetBytes;
            if (!_children.TryGetValue(entry.ProcessId, out var descendants)) continue;
            foreach (var child in descendants)
                pending.Push(child);
        }

        return total;
    }
}

/// <summary>
/// Reads the machine's process table and adds up whole process trees.
/// </summary>
/// <remarks>
/// <para><b>The tree, not the process.</b> A terminal tile starts a shell; the shell starts the agent,
/// the agent starts a compiler. The shell itself holds a few megabytes and the answer the user wants is
/// the other three hundred, so a reading that stopped at the process the tile knows about would be a
/// number that never moves.</para>
/// <para>Everything here fails soft and downwards. A process that exits between the snapshot and the sum
/// is simply absent, one whose memory cannot be read contributes nothing, and a platform with no reader
/// reports zero — which the row draws as no reading at all. A memory figure is worth nothing and
/// stopping a workspace list to raise it would cost everything.</para>
/// </remarks>
public sealed class ProcessTreeMemory : IProcessMemoryProbe
{
    private readonly Func<IReadOnlyList<ProcessMemoryEntry>> _readProcessTable;

    public ProcessTreeMemory() : this(ReadProcessTable) { }

    /// <param name="readProcessTable">Where the process table comes from. The one thing here that is a
    /// fact about the machine rather than about arithmetic.</param>
    internal ProcessTreeMemory(Func<IReadOnlyList<ProcessMemoryEntry>> readProcessTable) =>
        _readProcessTable = readProcessTable;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, long> WorkingSetsOf(
        IReadOnlyDictionary<string, IReadOnlyCollection<int>> rootProcessIdsByGroup)
    {
        if (rootProcessIdsByGroup.Values.All(roots => roots.Count == 0))
            return NoReadings(rootProcessIdsByGroup);

        try
        {
            var tree = ProcessTree.Of(_readProcessTable());
            return rootProcessIdsByGroup.ToDictionary(
                group => group.Key,
                group => tree.WorkingSetOf(group.Value));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Process memory reading failed: {0}", ex.Message);
            return NoReadings(rootProcessIdsByGroup);
        }
    }

    private static Dictionary<string, long> NoReadings(
        IReadOnlyDictionary<string, IReadOnlyCollection<int>> rootProcessIdsByGroup) =>
        rootProcessIdsByGroup.ToDictionary(group => group.Key, _ => 0L);

    /// <summary>Adds up the roots and everything descended from them, counting nobody twice.</summary>
    /// <remarks>Pure, so the walk can be argued about in a test with a table typed out by hand.</remarks>
    internal static long SumTrees(IReadOnlyList<ProcessMemoryEntry> table, IReadOnlyCollection<int> roots) =>
        ProcessTree.Of(table).WorkingSetOf(roots);

    private static IReadOnlyList<ProcessMemoryEntry> ReadProcessTable() =>
        OperatingSystem.IsWindows() ? ReadWindowsProcessTable()
        : OperatingSystem.IsLinux() ? ReadLinuxProcessTable()
        : [];

    /// <summary>
    /// Windows: parents from a toolhelp snapshot, memory from the runtime's own listing.
    /// </summary>
    /// <remarks>Two sources because neither carries both — the snapshot has no memory figures, and
    /// <see cref="Process"/> exposes no parent. Joined on the process id, and a process in one but not
    /// the other is dropped rather than guessed at.</remarks>
    private static IReadOnlyList<ProcessMemoryEntry> ReadWindowsProcessTable()
    {
        var parents = ReadWindowsParents();
        if (parents.Count == 0) return [];

        var entries = new List<ProcessMemoryEntry>(parents.Count);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (parents.TryGetValue(process.Id, out var parent))
                    entries.Add(new ProcessMemoryEntry(process.Id, parent, process.WorkingSet64));
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Reading process {0} failed: {1}", process.Id, ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }

        return entries;
    }

    private static Dictionary<int, int> ReadWindowsParents()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandle) return parents;

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return parents;
            do
            {
                parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            } while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return parents;
    }

    /// <summary>Linux: <c>/proc/&lt;pid&gt;/stat</c>, which carries the parent and the resident set at once.</summary>
    private static IReadOnlyList<ProcessMemoryEntry> ReadLinuxProcessTable()
    {
        var entries = new List<ProcessMemoryEntry>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var processId)) continue;
            try
            {
                if (ParseProcStat(File.ReadAllText(Path.Combine(directory, "stat")), processId) is { } entry)
                    entries.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A process that exited while we were walking, or one this user may not read.
            }
        }

        return entries;
    }

    /// <summary>Pulls the parent and the resident set out of one <c>/proc/&lt;pid&gt;/stat</c> line.</summary>
    /// <remarks>Split from the last <c>)</c> rather than from the start: the second field is the
    /// executable's name in brackets and a program is free to have spaces and brackets in its own.</remarks>
    internal static ProcessMemoryEntry? ParseProcStat(string stat, int processId)
    {
        var afterName = stat.LastIndexOf(')');
        if (afterName < 0) return null;

        // The fields after the name begin at the third overall, so the state is index 0, the parent
        // (4th) is index 1 and the resident set (24th) is index 21.
        var fields = stat[(afterName + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        const int parentField = 1;
        const int residentSetField = 21;
        if (fields.Length <= residentSetField) return null;
        if (!int.TryParse(fields[parentField], out var parentProcessId)) return null;
        if (!long.TryParse(fields[residentSetField], out var residentPages)) return null;

        return new ProcessMemoryEntry(processId, parentProcessId, residentPages * Environment.SystemPageSize);
    }

    private const uint SnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
