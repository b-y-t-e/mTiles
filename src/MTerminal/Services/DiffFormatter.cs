namespace MTerminal.Services;

public static class DiffFormatter
{
    public static string StripHeader(string diff)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git") || line.StartsWith("index ") ||
                line.StartsWith("--- ") || line.StartsWith("+++ ") ||
                line.StartsWith("old mode") || line.StartsWith("new mode") ||
                line.StartsWith("similarity index") || line.StartsWith("rename from") ||
                line.StartsWith("rename to") || line.StartsWith("new file mode") ||
                line.StartsWith("deleted file mode"))
                continue;
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    public static string TrimCommonIndent(string diff)
    {
        var lines = diff.Split('\n');
        var minIndent = int.MaxValue;

        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            var prefix = line[0];
            if (prefix != ' ' && prefix != '+' && prefix != '-') continue;

            var content = line.Length > 1 ? line[1..] : "";
            if (content.Length == 0 || content.Trim().Length == 0) continue;
            if (line.StartsWith("+++") || line.StartsWith("---")) continue;

            var indent = 0;
            foreach (var c in content)
            {
                if (c == ' ') indent++;
                else if (c == '\t') indent += 4;
                else break;
            }
            if (indent < minIndent)
                minIndent = indent;
        }

        if (minIndent <= 0 || minIndent == int.MaxValue)
            return diff;

        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (line.Length > 1 && (line[0] == ' ' || line[0] == '+' || line[0] == '-')
                && !line.StartsWith("+++") && !line.StartsWith("---"))
            {
                var linePrefix = line[0];
                var content = line[1..];
                var trimmed = TrimLeading(content, minIndent);
                sb.Append(linePrefix).AppendLine(trimmed);
            }
            else
            {
                sb.AppendLine(line);
            }
        }
        return sb.ToString();
    }

    private static string TrimLeading(string s, int count)
    {
        var removed = 0;
        var i = 0;
        while (i < s.Length && removed < count)
        {
            if (s[i] == ' ') { removed++; i++; }
            else if (s[i] == '\t') { removed += 4; i++; }
            else break;
        }
        return s[i..];
    }
}
