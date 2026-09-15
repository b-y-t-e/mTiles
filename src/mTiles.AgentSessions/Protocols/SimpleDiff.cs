using System.Text;

namespace mTiles.AgentSessions.Protocols;

/// <summary>
/// A unified diff made from a file's text before and after, for agents that report an edit as the two
/// texts rather than as a patch. Pure.
/// </summary>
/// <remarks>Deliberately not a real diff algorithm: the lines the two share at the start and at the end
/// are dropped and the middle is shown as one hunk removed and added. An edit an agent makes is nearly
/// always one contiguous change, where this is exact, and the view only needs something readable — the
/// checkpoint diff, which git computes, is the authoritative one.</remarks>
public static class SimpleDiff
{
    public static string Unified(string path, string? oldText, string newText)
    {
        var before = Lines(oldText);
        var after = Lines(newText);

        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;

        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix
               && before[^(suffix + 1)] == after[^(suffix + 1)])
            suffix++;

        var builder = new StringBuilder();
        builder.Append("--- ").AppendLine(oldText is null ? "/dev/null" : $"a/{path}");
        builder.Append("+++ b/").AppendLine(path);

        var removed = before.Length - prefix - suffix;
        var added = after.Length - prefix - suffix;
        if (removed == 0 && added == 0) return builder.ToString();

        builder.Append("@@ -").Append(prefix + 1).Append(',').Append(removed)
            .Append(" +").Append(prefix + 1).Append(',').Append(added).AppendLine(" @@");
        for (var i = prefix; i < before.Length - suffix; i++) builder.Append('-').AppendLine(before[i]);
        for (var i = prefix; i < after.Length - suffix; i++) builder.Append('+').AppendLine(after[i]);
        return builder.ToString();
    }

    private static string[] Lines(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
}
