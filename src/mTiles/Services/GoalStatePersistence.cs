using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// The Goal tile's state on disk. Small, but it is the whole record of a session — the goal, the
/// approved plan and every answer the tool gave — and it is rewritten at every message and every phase
/// change, so both halves of the round trip are written for the moment something goes wrong during one.
/// </summary>
internal sealed class GoalStatePersistence
{
    /// <summary>
    /// Serialises this instance's writes.
    /// <para>A save can be asked for from a debounce timer on a pool thread and from the UI thread at
    /// the same moment, and two writers on one path collide. The lock covers that; the unique temporary
    /// name below covers what the lock cannot, since it is per instance while the path is not.</para>
    /// </summary>
    private readonly Lock _writeLock = new();
    private bool _sweptOnce;

    /// <summary>
    /// Writes the state through a temporary file and a move, so an interrupted write cannot leave a
    /// half-written one behind. <c>File.WriteAllText</c> truncates first: a crash a millisecond later
    /// left an empty or clipped file where the session used to be, and the tile came back as new. The
    /// window is small and it is opened once per message, which is how often enough to matter.
    /// </summary>
    public void Save(string filePath, GoalTileState state)
    {
        var json = JsonSerializer.Serialize(state, JsonDefaults.Options);

        lock (_writeLock)
        {
            var dir = Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(dir);

            // A name no other writer can be using, rather than one shared <c>.tmp</c>: the lock is
            // this object's, and nothing stops a second instance — or a second process — pointing at
            // the same file. The move is what makes the write atomic either way; the last writer wins,
            // and no reader ever sees a half-written file.
            // Once per instance, not once per save. What it clears is litter from a previous run, so
            // there is nothing to find on the second sweep — and this directory is never pruned, so a
            // long-lived workspace would have every message pay for a scan of every goal ever set.
            if (!_sweptOnce)
            {
                _sweptOnce = true;
                SweepStaleTemporaries(filePath);
            }

            var temp = $"{filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, filePath, overwrite: true);
            }
            catch
            {
                // The move never happened, so this is ours to clean up. Left behind, one accumulates
                // per failed write, and they are the size of the session.
                try { File.Delete(temp); } catch { /* nothing more to try */ }
                throw;
            }
        }
    }

    /// <summary>
    /// Loads the state, or throws to say which of the two ways it failed.
    /// <para>The two are not interchangeable. A file that <em>parses</em> as nothing is damaged, so it
    /// is moved aside as <c>&lt;name&gt;.bad-&lt;timestamp&gt;</c> and the tile may start fresh over the
    /// top of it. A file that could not be <em>read</em> — locked, a failing disk, no permission — is
    /// almost certainly intact, and moving it would be destroying a session because of a transient
    /// failure to open it. That one is reported as <see cref="GoalStateUnavailableException"/> and the
    /// tile stops saving, because the only thing worse than not loading a session is overwriting it
    /// with the empty one that stood in for it.</para>
    /// </summary>
    public GoalTileState? Load(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        string json;
        try
        {
            json = File.ReadAllText(filePath);
        }
        // Gone between the check above and the read, or its directory removed underneath us. There is
        // nothing here to protect, so this is the same answer as "no file": a new tile. Reporting it as
        // unavailable would have stopped the tile saving for the rest of its life over a file that no
        // longer exists — the one case where refusing to write protects nothing at all.
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            System.Diagnostics.Trace.TraceWarning($"The goal file vanished while being read: {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GoalStateUnavailableException(ex);
        }

        try
        {
            // A file holding the four characters `null` parses without complaint and deserialises to
            // nothing. Returning that would be the same answer as "no file", so the tile would open
            // empty and then save over it — the exact outcome this class exists to prevent. It is a
            // damaged file, and it is treated as one.
            return JsonSerializer.Deserialize<GoalTileState>(json, JsonDefaults.Options)
                   ?? throw new JsonException("The goal file holds no state.");
        }
        // NotSupportedException as well as JsonException: the serialiser throws it for a value it can
        // parse but cannot turn into the target type, which is a damaged file by any other name.
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new GoalStateUnreadableException(SetAside(filePath), ex);
        }
    }

    /// <summary>
    /// Clears temporary files this tile left behind. One survives only a crash between the write and
    /// the move, so they are rare — but this directory is inside the user's repository, and rare and
    /// permanent adds up to a repository that slowly fills with our litter.
    /// <para>An hour old, so a temporary belonging to a write happening right now in another process is
    /// never taken out from under it, and only ones named after this exact file.</para>
    /// </summary>
    private static void SweepStaleTemporaries(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath)!;
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);

            foreach (var stale in Directory.EnumerateFiles(dir, Path.GetFileName(filePath) + ".*.tmp"))
                if (File.GetLastWriteTimeUtc(stale) < cutoff)
                    File.Delete(stale);
        }
        catch (Exception ex)
        {
            // Never worth failing a save over.
            System.Diagnostics.Trace.TraceWarning($"Could not clear stale goal temporaries: {ex.Message}");
        }
    }

    /// <summary>How many damaged copies of one goal file are kept, newest first. The same rule the
    /// settings file follows: enough to survive a run of bad luck, not enough to be a collection.</summary>
    private const int KeptDamagedCopies = 5;

    /// <summary>Deletes all but the newest <see cref="KeptDamagedCopies"/> rescued copies of one goal
    /// file.</summary>
    private static void PruneDamagedCopies(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath)!;
            var copies = Directory.EnumerateFiles(dir, Path.GetFileName(filePath) + ".bad-*")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(KeptDamagedCopies);

            foreach (var old in copies)
                File.Delete(old);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not prune damaged goal copies: {ex.Message}");
        }
    }

    /// <summary>Moves the damaged file out of the way and returns where it went, or null if even that
    /// failed — in which case the caller still has to say something, and now has less to say.</summary>
    private static string? SetAside(string filePath)
    {
        try
        {
            // Never over an earlier copy. The stamp is only accurate to the second, and two damaged
            // loads within one second would have the second backup destroy the first — a rescue
            // undoing a rescue.
            var stamp = $"{filePath}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            var kept = stamp;
            for (var n = 2; File.Exists(kept); n++)
                kept = $"{stamp}-{n}";

            File.Move(filePath, kept);
            PruneDamagedCopies(filePath);
            return kept;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not set aside a damaged goal file: {ex.Message}");
            return null;
        }
    }
}

/// <summary>Thrown when a goal file was read but is damaged. <see cref="KeptAt"/> is where the original
/// was moved to, so the tile can tell the user where their session went. Starting fresh is safe.</summary>
internal sealed class GoalStateUnreadableException(string? keptAt, Exception inner)
    : Exception("The goal file is damaged.", inner)
{
    public string? KeptAt { get; } = keptAt;
}

/// <summary>Thrown when a goal file exists but could not be opened. The file has not been touched, and
/// the tile must not save over it until it can be read.</summary>
internal sealed class GoalStateUnavailableException(Exception inner)
    : Exception("The goal file could not be opened.", inner);
