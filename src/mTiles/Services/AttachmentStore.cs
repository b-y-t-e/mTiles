namespace mTiles.Services;

/// <summary>
/// Where a file from outside the workspace is copied when it is attached to a message, so the agent can
/// read it and can still read it later.
/// </summary>
/// <remarks>
/// <para><b>Only a file from outside the workspace.</b> One inside it is named where it is, by an
/// <c>@</c> mention: a copy would show the agent the file as it was at the paste, not as it is, and an edit
/// to it would land in the copy.</para>
/// <para><b>Why a copy at all.</b> Claude Code asks before it reads anything outside the directory it runs
/// in and codex's sandbox may not let it read there — and what gets attached from outside is most often in
/// Downloads or a temporary directory, which empty themselves, while a Goal goes on naming its attachments
/// through every attempt of a run it may resume the next day.</para>
/// <para>Under <c>.mtiles/</c>, which the Git tile keeps ignored, like the Goal tile's pasted images. The
/// name keeps the original's, after a timestamp, because the agent reads the name and learns from it what
/// it was given. Never pruned, for the reason <see cref="GoalImageStore"/> gives: a mention of a file that
/// has gone costs more than the disk it would free.</para>
/// </remarks>
public static class AttachmentStore
{
    /// <summary>Past this size a file is named where it is rather than copied.</summary>
    public const long MaxCopyBytes = 20L * 1024 * 1024;

    /// <summary>What a file attached from outside the workspace is named by: its copy, or itself — with a
    /// notice when it could not be copied (too large, or the copy failed), so the composer can say the agent
    /// is being pointed at the original instead.</summary>
    /// <remarks>Off the caller's thread: it is called from a drop and from the paperclip, and a 20 MB copy
    /// off a USB stick or a network share would otherwise freeze the whole window for its duration.</remarks>
    public static Task<(string Path, string? Notice)> PlaceAsync(string path, string workspaceDirectory) =>
        Task.Run(() => Place(path, workspaceDirectory));

    private static (string Path, string? Notice) Place(string path, string workspaceDirectory)
    {
        if (string.IsNullOrEmpty(workspaceDirectory) || IsInside(path, workspaceDirectory) || !File.Exists(path))
            return (path, null);

        var name = Path.GetFileName(path);
        try
        {
            if (new FileInfo(path).Length > MaxCopyBytes)
                return (path, $"{name} is larger than {MaxCopyBytes / (1024 * 1024)} MB, so the agent is pointed at the original rather than a copy.");

            var target = FreeCopyPath(CopiesDirectory(workspaceDirectory), name);
            File.Copy(path, target);
            return (target, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"[Attachments] {name} could not be copied: {ex.Message}");
            return (path, $"{name} could not be copied into the workspace, so the agent is pointed at the original.");
        }
    }

    private static string FreeCopyPath(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}");
        for (var n = 2; File.Exists(target); n++)
            target = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{n}-{name}");
        return target;
    }

    /// <summary>
    /// Whether a composer's <c>@</c> path names something handed over only to be read — a copy in
    /// <see cref="CopiesDirectory"/>, or a file outside the workspace that could not be copied — rather than a
    /// place in the workspace the work is about.
    /// </summary>
    public static bool IsContextOnly(string mentionPath, string workspaceDirectory)
    {
        if (string.IsNullOrEmpty(workspaceDirectory)) return false;
        try
        {
            var full = Path.GetFullPath(Path.Combine(workspaceDirectory, mentionPath));
            return !IsInside(full, workspaceDirectory) || IsInside(full, CopiesDirectory(workspaceDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Where the copies are kept.</summary>
    public static string CopiesDirectory(string workspaceDirectory) =>
        WorkspacePaths.Combine(workspaceDirectory, "attachments");

    public static bool IsInside(string path, string workspaceDirectory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(workspaceDirectory), Path.GetFullPath(path));
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
