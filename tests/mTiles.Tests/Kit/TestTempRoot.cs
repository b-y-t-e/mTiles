using System.Runtime.CompilerServices;

namespace mTiles.Tests;

/// <summary>
/// Points the whole test process's temporary directory at one directory per run, and clears the runs
/// before the last two.
/// </summary>
/// <remarks>
/// <para><b>Because the suite was leaking about a hundred files a run into %TEMP%, for good.</b> A
/// conversation store is a SQLite file with <c>-wal</c> and <c>-shm</c> beside it, most tests make a
/// scratch directory, and neither was ever deleted — 84 000 files and 4 GB had piled up on one machine
/// before anybody looked. Fixing each of the 160-odd call sites would last until the next test that
/// forgot, so the redirect is made where nothing can forget it: <see cref="Path.GetTempPath"/> reads
/// <c>TMP</c>/<c>TEMP</c> (Windows) and <c>TMPDIR</c> (Unix) on every call, and so do the git and shell
/// processes the tests start, which inherit them.</para>
/// <para>Nothing is deleted at the end of a run, for the reason <see cref="TestAppDataRoot"/> gives: what a
/// failing test wrote is the evidence, and a killed run reaches no teardown anyway. The run before this
/// one is kept for the same reason; anything older, and anything an older build left loose in
/// <c>mTiles-tests</c>, goes at the start of the next run. A directory still in use by a run happening
/// alongside this one is skipped — it is younger than the hour this leaves alone.</para>
/// </remarks>
internal static class TestTempRoot
{
    private static readonly Lock Gate = new();
    private static string? _root;

    /// <summary>This run's temporary directory.</summary>
    public static string Root
    {
        get
        {
            Redirect();
            return _root!;
        }
    }

    /// <summary>Idempotent, so the other module initializers can ask for it without caring which of them
    /// the runtime happens to call first.</summary>
    [ModuleInitializer]
    internal static void Redirect()
    {
        lock (Gate)
        {
            if (_root is not null) return;

            var parent = Path.Combine(Path.GetTempPath(), "mTiles-tests");
            var root = Path.Combine(parent, $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
            try
            {
                Directory.CreateDirectory(root);
                ClearOldRuns(parent, keep: root);
            }
            catch
            {
                // A temp directory that cannot be made is nothing a test run can act on; the tests then
                // use the system's own, as they did before this existed.
                _root = Path.GetTempPath();
                return;
            }

            _root = root;
            Environment.SetEnvironmentVariable("TMP", root);
            Environment.SetEnvironmentVariable("TEMP", root);
            Environment.SetEnvironmentVariable("TMPDIR", root);
        }
    }

    private static void ClearOldRuns(string parent, string keep)
    {
        var anHourAgo = DateTime.Now.AddHours(-1);
        var entries = new DirectoryInfo(parent).EnumerateFileSystemInfos()
            .Where(e => !string.Equals(e.FullName, keep, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.CreationTime)
            .ToList();

        // The newest previous run survives; everything else that is old enough goes.
        var previousRun = entries.FirstOrDefault(e => e.Name.StartsWith("run-", StringComparison.Ordinal));

        foreach (var entry in entries)
        {
            if (entry == previousRun || entry.CreationTime > anHourAgo) continue;
            try
            {
                if (entry is DirectoryInfo dir) dir.Delete(recursive: true);
                else entry.Delete();
            }
            catch
            {
                // Held by something still running; the next run will try again.
            }
        }
    }
}
