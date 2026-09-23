using System.Diagnostics;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// Keeps the pictures that were dropped without a file behind them, so a CLI can be handed a path.
/// </summary>
/// <remarks>
/// <para>In this application's own directory and never in the workspace: a file under the repository
/// would wait in the next <c>git status</c>. The directory as well as the file is owner-only — a
/// screenshot is somebody's screen, and on Unix a <c>umask</c>-made directory lets every other user on
/// the machine list the names and the times they were taken.</para>
/// <para>It takes bytes rather than a bitmap so that the rule it carries — what a file is called and how
/// long one is kept — can be argued in a test without a UI toolkit. Encoding is
/// <see cref="mTiles.Views.DroppedImages"/>'s half, which is the half that needs Avalonia.</para>
/// </remarks>
public static class DroppedImageStore
{
    private const string Prefix = "dropped-";
    private const string Extension = ".png";

    /// <summary>What every kept picture is named, so pruning can find them and nothing else.</summary>
    public static string SearchPattern => Prefix + "*" + Extension;

    /// <summary>Writes <paramref name="png"/> and answers its path, or <c>null</c> if it could not be.</summary>
    public static string? Save(byte[] png, DateTime now)
    {
        try
        {
            var directory = AppPaths.GetDroppedImagesDirectory();
            PrivateFile.CreateDirectory(directory);

            var path = Path.Combine(directory, NameFor(now, Guid.NewGuid()));
            PrivateFile.WriteAllBytes(path, png);
            return path;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[DroppedImageStore] The dropped picture could not be written: {ex.Message}");
            return null;
        }
    }

    /// <summary>Dated, so the directory reads in order, and unique, so two drops in one second are two files.</summary>
    public static string NameFor(DateTime now, Guid id) =>
        Prefix + now.ToString("yyyyMMdd-HHmmss") + "-" + id.ToString("N")[..8] + Extension;

    /// <summary>Whether a picture written at <paramref name="writtenAt"/> has outlived its keeping.</summary>
    /// <remarks>The same retention the logs get: by then the agent that was handed the path has long
    /// since read it.</remarks>
    public static bool HasExpired(DateTime writtenAt, DateTime now) =>
        writtenAt < now.AddDays(-AppDefaults.LogRetentionDays);

    /// <summary>Sweeps the kept pictures, the way the logs are swept at startup.</summary>
    /// <remarks>Here rather than out of <see cref="Save"/>: pruning on the next drop is pruning that
    /// never comes for somebody who dropped one screenshot and no more, and the promise the directory
    /// carries is a retention, not a side effect of being used again.</remarks>
    public static void PruneAll(DateTime now)
    {
        try
        {
            var directory = AppPaths.GetDroppedImagesDirectory();
            if (Directory.Exists(directory)) Prune(directory, now);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[DroppedImageStore] The kept pictures could not be swept: {ex.Message}");
        }
    }

    /// <summary>Deletes what has outlived its keeping, leaving anything else in the directory alone.</summary>
    public static void Prune(string directory, DateTime now)
    {
        foreach (var file in Directory.EnumerateFiles(directory, SearchPattern))
            try
            {
                if (HasExpired(File.GetLastWriteTime(file), now)) File.Delete(file);
            }
            catch (Exception ex)
            {
                // In use or already gone — the next drop tries again.
                Trace.TraceWarning($"[DroppedImageStore] {file} could not be pruned: {ex.Message}");
            }
    }
}
