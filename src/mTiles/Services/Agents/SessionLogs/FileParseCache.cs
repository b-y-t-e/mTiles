using System.Collections.Concurrent;

namespace mTiles.Services.Agents.SessionLogs;

/// <summary>
/// What a small file said the last time it was parsed, kept until its size or last write changes.
/// </summary>
/// <remarks>
/// <para>For a store that is one file per turn — opencode's <c>message/&lt;session&gt;/msg_*.json</c> —
/// where the answer is every turn added up. Parsed afresh on each change, that was the whole history
/// read and parsed per turn, growing with the conversation; cached, a turn costs a <c>stat</c> per file
/// and a parse of the file that turn wrote.</para>
/// <para>A parse that answers null — a file caught half-written — is not remembered, so it is read
/// again next time rather than counted as saying nothing for good.</para>
/// </remarks>
internal sealed class FileParseCache<T> where T : class
{
    private sealed record Entry(long Length, DateTime LastWriteUtc, T Value);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>What <paramref name="parse"/> made of the file, parsed only when it has changed.</summary>
    public T? Get(FileInfo file, Func<FileInfo, T?> parse)
    {
        if (_entries.TryGetValue(file.FullName, out var known)
            && known.Length == file.Length && known.LastWriteUtc == file.LastWriteTimeUtc)
            return known.Value;

        if (parse(file) is not { } value) return null;
        _entries[file.FullName] = new Entry(file.Length, file.LastWriteTimeUtc, value);
        return value;
    }
}
