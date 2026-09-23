using System.Collections.Concurrent;
using System.Text;

namespace mTiles.Services.Agents.SessionLogs;

/// <summary>
/// A running fold over the lines of an append-only <c>.jsonl</c> file, read once and then only from
/// where the previous read stopped.
/// </summary>
/// <remarks>
/// <para><b>Why a fold rather than the last line.</b> pi records a price per turn, not a running total,
/// so the figure is every line of the transcript added up — which, re-read whole every time the file
/// changed, cost a parse of the entire conversation per turn and grew with its length. Remembering the
/// sum and the offset it reaches makes a turn cost the lines that turn appended. codex's rollouts are
/// read through the same tail, folding the last token count rather than a sum.</para>
/// <para><b>Only complete lines are folded into what is remembered.</b> A line the CLI is still writing
/// is folded into this answer and not into the stored state, so it is counted once, when it is whole —
/// folded into both, its price would be added again on the next read.</para>
/// <para>Concurrent because one log is shared by every tile of its agent, and
/// two tiles racing on one file at worst read the same new lines twice into the same result.</para>
/// </remarks>
internal sealed class JsonlFoldTail<TState> where TState : struct
{
    private sealed record Progress(long Offset, DateTime LastWriteUtc, TState Complete, TState Answer);

    private readonly ConcurrentDictionary<string, Progress> _progress = new(StringComparer.Ordinal);
    private readonly Func<TState, string, TState> _fold;
    private readonly TState _empty;

    /// <param name="fold">What one line does to the state; a line it cannot read leaves it as it was.
    /// </param>
    /// <param name="empty">The state of a file with nothing in it.</param>
    public JsonlFoldTail(Func<TState, string, TState> fold, TState empty)
    {
        _fold = fold;
        _empty = empty;
    }

    /// <summary>The fold over the whole file, reading only what was appended since it was last asked
    /// about.</summary>
    public TState Read(FileInfo file)
    {
        file.Refresh();
        var known = _progress.TryGetValue(file.FullName, out var remembered) ? remembered : null;
        if (known is not null && known.Offset == file.Length && known.LastWriteUtc == file.LastWriteTimeUtc)
            return known.Answer;

        // Shorter than what was read is a file rewritten rather than appended to: start again.
        var start = known is not null && known.Offset <= file.Length ? known : null;
        var progress = start is null ? ReadFrom(file, 0, _empty) : ReadFrom(file, start.Offset, start.Complete);
        _progress[file.FullName] = progress;
        return progress.Answer;
    }

    private Progress ReadFrom(FileInfo file, long offset, TState state)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var lastWrite = file.LastWriteTimeUtc;
        stream.Position = offset;
        var appended = new byte[stream.Length - offset];
        stream.ReadExactly(appended);

        var complete = Array.LastIndexOf(appended, (byte)'\n') + 1;
        var skip = offset == 0 && HasUtf8Bom(appended) ? Utf8Bom.Length : 0;
        foreach (var line in LinesOf(appended, skip, complete)) state = _fold(state, line);

        var answer = state;
        foreach (var line in LinesOf(appended, Math.Max(skip, complete), appended.Length))
            answer = _fold(answer, line);

        return new Progress(offset + complete, lastWrite, state, answer);
    }

    private static IEnumerable<string> LinesOf(byte[] bytes, int start, int end) =>
        end > start
            ? Encoding.UTF8.GetString(bytes, start, end - start)
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 0)
            : [];

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static bool HasUtf8Bom(byte[] bytes) => bytes.AsSpan().StartsWith(Utf8Bom);
}
