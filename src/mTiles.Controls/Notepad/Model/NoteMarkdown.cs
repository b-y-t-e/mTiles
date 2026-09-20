using System.Collections.Generic;
using System.Text;

namespace Notepad.Avalonia.Model;

internal static class NoteMarkdown
{
    public static List<NoteItemData> ParseMarkdown(string? markdown)
    {
        var result = new List<NoteItemData>();
        if (string.IsNullOrEmpty(markdown)) return result;

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            result.Add(new NoteItemData(line));
        }
        return result;
    }

    public static string ToMarkdown(IEnumerable<NoteItemData>? items)
    {
        if (items == null) return string.Empty;
        var sb = new StringBuilder();
        bool first = true;
        foreach (var item in items)
        {
            if (!first) sb.Append('\n');
            first = false;
            sb.Append(item.Text);
        }
        return sb.ToString();
    }
}
