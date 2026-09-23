using mTiles.AgentSessions.Events;

namespace mTiles.AgentSessions.Checkpoints;

/// <summary>
/// Reads git's <c>--numstat -z</c> and <c>--name-status -z</c> output into changed files. Pure.
/// </summary>
/// <remarks>
/// <para>Two outputs because neither says both things: numstat has the line counts and no kind of change,
/// name-status the kind and no counts. Both are NUL-separated so a path with a tab or a newline in it
/// survives.</para>
/// <para>In numstat a rename is written with an empty path followed by two NUL-separated records, the old
/// path and the new; a binary file counts <c>-</c> lines, read as zero.</para>
/// </remarks>
public static class CheckpointDiffParser
{
    public static IReadOnlyList<ChangedFile> Parse(string numstat, string nameStatus)
    {
        var (kinds, oldPaths) = ParseKinds(nameStatus);
        var files = new List<ChangedFile>();
        var records = numstat.Split('\0');

        for (var i = 0; i < records.Length; i++)
        {
            var parts = records[i].TrimStart('\n').Split('\t');
            if (parts.Length < 3) continue;

            var additions = int.TryParse(parts[0], out var added) ? added : 0;
            var deletions = int.TryParse(parts[1], out var deleted) ? deleted : 0;
            var path = parts[2];

            if (path.Length == 0 && i + 2 < records.Length)
            {
                path = records[i + 2];
                i += 2;
            }

            if (path.Length == 0) continue;
            files.Add(new ChangedFile(path, kinds.GetValueOrDefault(path, FileChangeKind.Modified), additions,
                deletions, oldPaths.GetValueOrDefault(path)));
        }

        return [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)];
    }

    /// <summary>The kind of every change, and — for a rename — the name the file had before it.</summary>
    private static (Dictionary<string, FileChangeKind> Kinds, Dictionary<string, string> OldPaths) ParseKinds(
        string nameStatus)
    {
        var kinds = new Dictionary<string, FileChangeKind>(StringComparer.Ordinal);
        var oldPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var records = nameStatus.Split('\0');

        for (var i = 0; i < records.Length; i++)
        {
            var code = records[i].TrimStart('\n');
            if (code.Length == 0 || i + 1 >= records.Length) continue;

            switch (code[0])
            {
                case 'A':
                    kinds[records[++i]] = FileChangeKind.Added;
                    break;
                case 'D':
                    kinds[records[++i]] = FileChangeKind.Deleted;
                    break;
                case 'R' or 'C' when i + 2 < records.Length:
                    var from = records[++i];
                    var to = records[++i];
                    kinds[to] = FileChangeKind.Renamed;
                    oldPaths[to] = from;
                    break;
                default:
                    kinds[records[++i]] = FileChangeKind.Modified;
                    break;
            }
        }

        return (kinds, oldPaths);
    }
}
