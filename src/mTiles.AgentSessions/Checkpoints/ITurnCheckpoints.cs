using mTiles.AgentSessions.Events;

namespace mTiles.AgentSessions.Checkpoints;

/// <summary>
/// Photographs of a working tree taken between turns, and what changed from one to the next.
/// </summary>
/// <remarks>
/// A seam rather than git itself because a conversation in a directory without a repository still
/// works — it just has no diffs — and because a test drives the host without a repository at all.
/// </remarks>
public interface ITurnCheckpoints
{
    /// <summary>Photographs the working tree, or answers null where that cannot be done — no repository,
    /// no git, a snapshot that ran out of time. Never throws for any of those.</summary>
    /// <param name="conversationId">What the photograph is filed under.</param>
    /// <param name="index">Its position in the conversation, from 0 for the baseline.</param>
    Task<string?> CaptureAsync(string conversationId, int index, CancellationToken ct);

    /// <summary>What changed between two photographs, file by file.</summary>
    Task<IReadOnlyList<ChangedFile>> ChangesAsync(string fromCheckpoint, string toCheckpoint, CancellationToken ct);

    /// <summary>The unified diff between two photographs, for one file or for everything.</summary>
    Task<string> DiffAsync(string fromCheckpoint, string toCheckpoint, string? path, CancellationToken ct);

    /// <summary>Puts the working tree back to how a photograph found it. Files added since are removed;
    /// ignored files are left alone.</summary>
    /// <returns>Where the files it replaced were photographed first. Throws, and changes nothing, when that
    /// photograph cannot be taken.</returns>
    Task<string> RestoreAsync(string checkpoint, CancellationToken ct);

    /// <summary>Drops every photograph of a conversation.</summary>
    Task ForgetAsync(string conversationId, CancellationToken ct);
}
