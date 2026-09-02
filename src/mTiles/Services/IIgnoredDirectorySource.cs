namespace mTiles.Services;

/// <summary>
/// Where the directories git ignores in a working tree come from.
/// </summary>
/// <remarks>An interface rather than a <see cref="GitService"/> reference so that
/// <see cref="WorkspaceGitWatcher"/> — which needs one answer and none of the rest of that class —
/// depends on the question instead of on the tool that answers it, and so a test can drive the watcher
/// without a repository.</remarks>
public interface IIgnoredDirectorySource
{
    /// <summary>The absolute paths of the directories git ignores in this working tree.</summary>
    Task<HashSet<string>> GetIgnoredDirsAsync(CancellationToken ct = default);
}
