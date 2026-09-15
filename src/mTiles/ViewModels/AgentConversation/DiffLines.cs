namespace mTiles.ViewModels.AgentConversation;

/// <summary>What a line of a unified diff is, for colouring it.</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    Hunk,
    Header,
}

/// <summary>One line of a unified diff.</summary>
public sealed record DiffLine(string Text, DiffLineKind Kind);

/// <summary>
/// A unified diff as lines worth colouring. Pure.
/// </summary>
/// <remarks>Capped, because a diff is drawn as one control per line in a list that does not virtualise
/// — the same constraint the Goal tile's transcript lives under — and a regenerated lock file is tens of
/// thousands of lines nobody reads in a conversation.</remarks>
public static class DiffLines
{
    public const int MaxLines = 1500;

    public static IReadOnlyList<DiffLine> Parse(string? diff)
    {
        if (string.IsNullOrEmpty(diff)) return [];

        var lines = diff.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var result = new List<DiffLine>(Math.Min(lines.Length, MaxLines + 1));
        foreach (var line in lines.Take(MaxLines))
            result.Add(new DiffLine(line, KindOf(line)));

        if (lines.Length > MaxLines)
            result.Add(new DiffLine($"… {lines.Length - MaxLines} more lines", DiffLineKind.Header));
        return result;
    }

    private static DiffLineKind KindOf(string line) => line switch
    {
        _ when line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)
               || line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal)
               || line.StartsWith("Index: ", StringComparison.Ordinal) || line.StartsWith("===", StringComparison.Ordinal)
            => DiffLineKind.Header,
        _ when line.StartsWith("@@", StringComparison.Ordinal) => DiffLineKind.Hunk,
        _ when line.StartsWith('+') => DiffLineKind.Added,
        _ when line.StartsWith('-') => DiffLineKind.Removed,
        _ => DiffLineKind.Context,
    };
}
