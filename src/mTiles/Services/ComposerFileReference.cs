namespace mTiles.Services;

/// <summary>
/// What a file that is not a picture becomes when it is attached to a message in either composer: an
/// <c>@</c> mention, where the caret was, of a file the agent can open.
/// </summary>
/// <remarks>
/// <para><b>A path, never the contents.</b> Every agent these tiles drive reads files for itself, and one
/// that reads it can also read the half it needs rather than being handed a megabyte of log in its context.
/// It is also the one form every protocol here carries: pi and agy take no files at all, and a Goal prompt is
/// a command line.</para>
/// <para><b>Always a mention</b>, spelled exactly as the <c>@</c> list spells one
/// (<see cref="FileMentionToken.Mention"/>): the same file named the same way however it got into the
/// message, and a mention is what the composer's file chips are drawn from. Inside the workspace it is
/// relative with forward slashes; a file from outside is first copied in (<see cref="AttachmentStore"/>), and
/// only one that could not be is named by its absolute path.</para>
/// </remarks>
public static class ComposerFileReference
{
    /// <summary>The mention to insert, and the notice <see cref="AttachmentStore.PlaceAsync"/> gave, if any.</summary>
    public static async Task<(string Mention, string? Notice)> ForAsync(string path, string workspaceDirectory)
    {
        var (placed, notice) = await AttachmentStore.PlaceAsync(Path.GetFullPath(path), workspaceDirectory);
        return (MentionOf(Path.GetFullPath(placed), workspaceDirectory), notice);
    }

    private static string MentionOf(string fullPath, string workspaceDirectory) =>
        FileMentionToken.Mention(!string.IsNullOrEmpty(workspaceDirectory) && AttachmentStore.IsInside(fullPath, workspaceDirectory)
            ? Path.GetRelativePath(Path.GetFullPath(workspaceDirectory), fullPath).Replace('\\', '/')
            : fullPath);
}
