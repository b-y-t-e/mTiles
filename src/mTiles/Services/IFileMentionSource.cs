namespace mTiles.Services;

/// <summary>
/// The files a mention can point at, as paths relative to the workspace and separated by <c>/</c>.
/// </summary>
/// <remarks>
/// An abstraction rather than the class that reads them, for two reasons that are the same reason: the
/// suggestions are a view model's business and reading a working tree is not, and the matching and
/// ordering rules are only arguable with a list nobody had to create on disk first.
/// </remarks>
public interface IFileMentionSource
{
    /// <summary>Every file worth offering, in a stable order.</summary>
    /// <remarks>Returns an empty list rather than throwing when the tree cannot be read: a mention that
    /// suggests nothing is a feature that is quietly not there, and an exception on a keystroke is a
    /// tile that dies while somebody types.</remarks>
    Task<IReadOnlyList<string>> GetPathsAsync(CancellationToken ct = default);
}
