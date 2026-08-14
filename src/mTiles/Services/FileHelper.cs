using System.Diagnostics;
using mTiles.Models;

namespace mTiles.Services;

public static class FileHelper
{
    /// <summary>
    /// How two paths are compared: the way the filesystem underneath does.
    /// </summary>
    /// <remarks>
    /// One answer for the whole application, because the two places that ask are asking the same
    /// question for different reasons — whether an archive entry stays inside the directory it is being
    /// unpacked into, and whether the model being deleted is the one that is loaded — and a codebase
    /// with two copies of this expression is one where they eventually disagree. Ignoring case on Linux
    /// is not merely inaccurate: it would accept <c>/tmp/Model/x</c> as being inside <c>/tmp/model</c>
    /// and then write it somewhere else entirely.
    /// </remarks>
    public static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Whether two paths name the same thing, as far as this filesystem is concerned.</summary>
    public static bool SamePath(string? left, string? right) =>
        string.Equals(left, right, PathComparison);

    /// <summary>
    /// Opens a folder in the system file manager.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="OpenFolderAndSelect"/> because "select this" and "show me this folder"
    /// are different requests, and the trick for asking the second with the first —
    /// <c>Path.Combine(folder, ".")</c> — asks Explorer to select an entry that does not exist. It
    /// happens to open the folder; what it does with the selection is undefined and version-dependent.
    /// </remarks>
    /// <returns>False if the folder could not be opened, having said why in the log.</returns>
    public static bool OpenFolder(string folderPath)
    {
        var command = OperatingSystem.IsWindows() ? "explorer.exe"
            : OperatingSystem.IsMacOS() ? "open"
            : "xdg-open";

        try
        {
            Process.Start(new ProcessStartInfo(command, $"\"{folderPath}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            // A desktop with no xdg-open, a policy that blocks the shell verb: neither is this
            // application's problem, and neither is worth ending it over. Guarded like OpenFile beside
            // it — a button that opens a folder is not a button worth crashing on.
            Trace.TraceWarning("Opening the folder {0} failed: {1}", folderPath, ex.Message);
            return false;
        }
    }

    public static void OpenFolderAndSelect(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo("open", $"-R \"{filePath}\"") { UseShellExecute = true });
        }
        else
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir != null)
                Process.Start(new ProcessStartInfo("xdg-open", dir) { UseShellExecute = true });
        }
    }

    /// <summary>Opens a file with whatever the system uses for it. Returns false when there is nothing
    /// there or the system refused — a button that silently does nothing is its own bug.</summary>
    public static bool OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Opening {0} failed: {1}", path, ex.Message);
            return false;
        }
    }

    /// <summary>Deletes a file if it is there, and says so if it could not.</summary>
    public static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Deleting {0} failed: {1}", path, ex.Message);
            return false;
        }
    }

    public static void WriteWithRetry(string path, Action<string> writeAction)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);

        try
        {
            writeAction(path);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("File write failed, retrying: {0}", ex.Message);
            Thread.Sleep(AppDefaults.FileRetryDelayMs);
            try
            {
                writeAction(path);
            }
            catch (Exception ex2)
            {
                Trace.TraceWarning("File write retry failed: {0}", ex2.Message);
            }
        }
    }
}
