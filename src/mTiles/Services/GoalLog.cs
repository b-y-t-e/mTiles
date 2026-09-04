using System.Diagnostics;
using System.Globalization;
using System.Text;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// The whole of what one Goal tile did, written down: every prompt sent, every answer that came back,
/// every decision taken about them, and every failure on the way.
/// </summary>
/// <remarks>
/// <para><b>Why not <see cref="Trace"/> like everything else.</b> The application's own log is where a
/// failure goes — one line, a reason, and something a user can act on. This is the opposite kind of
/// record: whole prompts and whole answers, tens of kilobytes each, several per attempt. Written into
/// <c>mtiles-YYYY-MM-DD.log</c> it would bury every other line in the file, and that file already
/// carries a day's worth of binding traces. So it is its own directory, its own file per tile per day,
/// and nothing else writes there.</para>
/// <para><b>One file per tile per day.</b> Two goals running side by side in two workspaces are the
/// ordinary case, and interleaving them costs the one thing this exists for — reading a run from the
/// top. The date leads the tile in the name so the retention sweep can find it by the same
/// <c>prefix + date</c> rule <see cref="FileLogWriter"/> uses, and the same
/// <see cref="AppDefaults.LogRetentionDays"/> applies: a full transcript of somebody's repository is
/// not something to keep for ever by accident.</para>
/// <para><b>Owner-only, for the reason the usage history is.</b> A prompt carries this project's diff,
/// its file names and whatever the user typed into the goal; an answer carries the code the tool wrote.
/// Outside Windows nothing else narrows it, so every append goes through
/// <see cref="PrivateFile.AppendAllText"/>, which sets the mode <em>as the file is created</em> rather
/// than after the first entry has already been written at whatever the umask said.</para>
/// <para><b>Nothing here may cost a run.</b> Writing happens on a chain off the caller's thread — the
/// callers are the UI thread, and an entry can be a megabyte — and every failure is swallowed: a log
/// that cannot be written is a log that is not written, never a goal that stops. For the same reason
/// nothing here throws on a null, an empty string or a closed logger.</para>
/// </remarks>
public sealed class GoalLog : IDisposable
{
    /// <summary>How much of one prompt or one answer is kept.</summary>
    /// <remarks>Generous on purpose — the point of this file is that the whole thing is in it, and a
    /// prompt carrying a working tree runs to a few hundred kilobytes. What the cap is actually for is
    /// a tool that streams megabytes of one repeated line, which is a fault worth recording and not
    /// worth recording all of. A truncated entry says so on its own line rather than simply stopping,
    /// or the next reader takes the cut for the end of the answer.</remarks>
    internal const int MaxEntry = 1_000_000;

    private const string Prefix = "goal-";

    /// <summary>Serialises the writes and keeps them off the caller's thread. One chain for the whole
    /// application rather than one per tile: the entries are large, the disk is one, and two tiles
    /// writing to two files at once buys nothing but a second thread waiting on the same spindle.
    /// </summary>
    private static Task _chain = Task.CompletedTask;
    private static readonly object ChainLock = new();

    private readonly string? _path;
    private bool _disposed;

    /// <param name="goalFilePath">The tile's own <c>goals/id.json</c>. Its name is the goal's identity
    /// everywhere else, so it is the identity here too — a log nobody can match to a tile is a log
    /// nobody reads.</param>
    public GoalLog(string goalFilePath)
    {
        try
        {
            var directory = AppPaths.GetGoalLogsDirectory();
            Directory.CreateDirectory(directory);
            CleanupOldLogs(directory);

            _path = Path.Combine(directory, NameFor(goalFilePath, DateTime.Now));
        }
        catch (Exception ex)
        {
            // A directory that cannot be made is a tile that runs without a log, which is exactly how
            // every goal ran until this existed.
            Trace.TraceWarning(
                $"The goal log could not be opened, so this run is not recorded: {ex.Message}");
            _path = null;
        }
    }

    /// <summary>Where this tile is writing, or null where no log could be opened.</summary>
    public string? FilePath => _path;

    /// <summary>One line: something happened.</summary>
    public void Event(string message) => Append(Line(message), body: null);

    /// <summary>A heading and whatever it is a heading for — a prompt, an answer, a diff.</summary>
    /// <remarks>The body is written under the heading rather than beside it, because these are the
    /// entries somebody reads rather than greps, and a hundred kilobytes on the end of a timestamped
    /// line is not a line.</remarks>
    public void Block(string heading, string? body) => Append(Line(heading), body);

    private static string Line(string message) =>
        $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}] {message}";

    private void Append(string heading, string? body)
    {
        if (_path is not { } path || _disposed) return;

        var text = Compose(heading, body);

        lock (ChainLock)
        {
            _chain = _chain.ContinueWith(_ =>
            {
                try
                {
                    // Owner-only from the moment it exists, never narrowed after the first write: this
                    // file's first entry is already a whole prompt, and a process killed between the
                    // write and the narrowing would leave it readable by everyone on the machine for
                    // good.
                    PrivateFile.AppendAllText(path, text);
                }
                catch
                {
                    // A log is not worth an exception on a thread-pool thread, and the goal it belongs
                    // to is still running.
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private static string Compose(string heading, string? body)
    {
        var builder = new StringBuilder();
        builder.AppendLine(heading);

        if (!string.IsNullOrEmpty(body))
        {
            if (body.Length > MaxEntry)
            {
                builder.AppendLine(body[..MaxEntry]);
                builder.AppendLine(
                    $"    ... truncated, {body.Length - MaxEntry} more characters not recorded.");
            }
            else
            {
                builder.AppendLine(body);
            }
        }

        return builder.ToString();
    }

    /// <summary>Waits for what has been queued, so a tile closing does not abandon its last entries.
    /// </summary>
    /// <remarks>Bounded, and for the reason <c>GitIgnoreEditQueue.WaitForAll</c> is: this runs while a
    /// tile is being disposed of, and a disk that has stopped answering must not hold the window open.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Task chain;
        lock (ChainLock) chain = _chain;

        try { chain.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* whatever it was, it was a log entry */ }
    }

    /// <summary>The name this tile's log has today.</summary>
    /// <remarks>The date leads the id so that <see cref="IsExpired"/> can read it off the front of the
    /// name: the id is user-derived and of no fixed length, so anything after it cannot be found by
    /// position. The two rules are here together because they are one rule read in two directions, and
    /// apart they drift into a sweep that either keeps everything or deletes what it did not write.
    /// </remarks>
    internal static string NameFor(string goalFilePath, DateTime day) =>
        $"{Prefix}{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
        + $"-{SafePathComponent.Of(Path.GetFileNameWithoutExtension(goalFilePath))}.log";

    /// <summary>Whether a file in the goal-log directory is one of ours and older than the cutoff.</summary>
    /// <remarks>Pure, and separate from the sweep that acts on it, because the sweep <em>deletes</em>:
    /// a name that is not ours — something a user dropped in there, a copy taken by hand — has to be
    /// left alone, and that is a rule worth being able to read in a table rather than in a directory
    /// enumeration.</remarks>
    internal static bool IsExpired(string fileName, DateTime cutoff)
    {
        if (!fileName.StartsWith(Prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            return false;

        const int DateLength = 10;
        var body = Path.GetFileNameWithoutExtension(fileName);
        if (body.Length < Prefix.Length + DateLength) return false;

        // A name of ours carries the id after the date, so anything else there is somebody else's file
        // that happens to start the way ours do.
        if (body.Length > Prefix.Length + DateLength
            && body[Prefix.Length + DateLength] != '-') return false;

        return DateTime.TryParseExact(body.Substring(Prefix.Length, DateLength), "yyyy-MM-dd",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
               && date < cutoff;
    }

    private static void CleanupOldLogs(string directory)
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-AppDefaults.LogRetentionDays);

            foreach (var file in Directory.GetFiles(directory, $"{Prefix}*.log"))
            {
                if (!IsExpired(Path.GetFileName(file), cutoff)) continue;

                try { File.Delete(file); } catch { /* in use, or gone already */ }
            }
        }
        catch
        {
            // The sweep is housekeeping. Failing it is not a reason to open no log at all.
        }
    }
}
