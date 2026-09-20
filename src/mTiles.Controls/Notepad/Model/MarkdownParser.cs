using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Notepad.Avalonia.Model;

/// <summary>
/// Markdown parser producing a flat list of <see cref="MarkdownBlock"/>s.
/// </summary>
/// <remarks>
/// Supports ATX and setext headings, thematic breaks, fenced and indented code
/// blocks, nested blockquotes, nested ordered/unordered/task lists, GFM pipe
/// tables and link reference definitions, plus the inline constructs emphasis,
/// strong, strikethrough, code spans, links, autolinks, images, hard line breaks
/// and backslash escapes.
/// </remarks>
public static partial class MarkdownParser
{
    private static readonly Regex AtxHeadingRx =
        new(@"^(#{1,6})(?:[ ]+(.*?))?[ ]*#*[ ]*$", RegexOptions.Compiled);

    private static readonly Regex ThematicBreakRx =
        new(@"^([-*_])[ ]*(?:\1[ ]*){2,}$", RegexOptions.Compiled);

    private static readonly Regex FenceRx =
        new(@"^(`{3,}|~{3,})[ ]*(.*)$", RegexOptions.Compiled);

    private static readonly Regex BulletRx =
        new(@"^([-*+])([ ]+|$)", RegexOptions.Compiled);

    private static readonly Regex OrderedRx =
        new(@"^(\d{1,9})([.)])([ ]+|$)", RegexOptions.Compiled);

    private static readonly Regex TaskRx =
        new(@"^\[([ xX])\](?:[ ]+|$)", RegexOptions.Compiled);

    private static readonly Regex LinkRefDefRx =
        new(@"^\[([^\]]+)\]:[ ]*<?([^\s>]+)>?(?:[ ]+.*)?$", RegexOptions.Compiled);

    private static readonly Regex TableDelimiterRx =
        new(@"^\|?[ ]*:?-+:?[ ]*(\|[ ]*:?-+:?[ ]*)*\|?[ ]*$", RegexOptions.Compiled);

    private static readonly Regex SetextRx =
        new(@"^(=+|-+)[ ]*$", RegexOptions.Compiled);

    /// <summary>
    /// Parses markdown into renderable blocks.
    /// </summary>
    /// <param name="markdown">Source text.</param>
    /// <param name="softBreaksAsLineBreaks">
    /// When true (GitHub-comment style) a single newline inside a paragraph is a
    /// visible line break; when false it collapses into a space (CommonMark).
    /// </param>
    public static List<MarkdownBlock> Parse(string? markdown, bool softBreaksAsLineBreaks = true)
    {
        var state = new State { SoftBreaks = softBreaksAsLineBreaks };
        if (string.IsNullOrEmpty(markdown))
            return state.Blocks;

        var source = SplitLines(markdown);
        var lines = source.Select(ExpandTabs).ToList();
        CollectLinkDefinitions(lines, state.Refs);
        state.Run(lines, source);
        return state.Blocks;
    }

    // The document twice: as written, and with tabs expanded. Every structural
    // decision is taken on the expanded copy, because indentation is the one place
    // markdown counts columns; the body of a fenced code block is taken from the
    // source, because there a tab is somebody's indentation and a patch that renders
    // with spaces is a patch that cannot be copied back out.
    private static List<string> SplitLines(string text)
    {
        var result = new List<string>();
        foreach (var raw in text.Split('\n'))
            result.Add(raw.TrimEnd('\r'));
        return result;
    }

    private static string ExpandTabs(string line)
    {
        if (line.IndexOf('\t') < 0) return line;
        var sb = new StringBuilder(line.Length + 8);
        foreach (char c in line)
        {
            if (c == '\t') sb.Append(' ', 4 - (sb.Length % 4));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // Link reference definitions may sit anywhere outside code fences; harvest
    // them up front and blank the lines so the block pass never sees them.
    private static void CollectLinkDefinitions(List<string> lines, Dictionary<string, string> refs)
    {
        // The fence as it was opened, not its first three characters: a block opened
        // with four backticks is closed only by four, and cut to three it ends on the
        // first body line that carries a fence of its own — after which the rest of
        // somebody's code is read as prose and every line shaped like a link
        // definition is blanked out of it.
        char fenceChar = '\0';
        int fenceLen = 0;
        int fenceQuote = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var stripped = StripQuotes(lines[i], fenceLen > 0 ? fenceQuote : int.MaxValue, out int quote);
            var trimmed = stripped.TrimStart();

            if (fenceLen > 0)
            {
                if (ClosesFence(trimmed, fenceChar, fenceLen)) fenceLen = 0;
                continue;
            }

            var fm = FenceRx.Match(trimmed);
            if (fm.Success && Indent(stripped) < 4)
            {
                fenceChar = fm.Groups[1].Value[0];
                fenceLen = fm.Groups[1].Value.Length;
                fenceQuote = quote;
                continue;
            }

            if (Indent(stripped) >= 4) continue;

            var m = LinkRefDefRx.Match(trimmed);
            if (m.Success)
            {
                refs[m.Groups[1].Value.Trim()] = m.Groups[2].Value;
                lines[i] = string.Empty;
            }
        }
    }

    private static int Indent(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        return i;
    }

    /// <summary>Whether a line closes a fence of the given character and length.</summary>
    private static bool ClosesFence(string trimmed, char fenceChar, int fenceLen) =>
        trimmed.Length >= fenceLen && trimmed.All(c => c == fenceChar);

    /// <summary>Removes leading blockquote markers and reports how many were found.</summary>
    private static string StripQuotes(string line, out int depth) =>
        StripQuotes(line, int.MaxValue, out depth);

    /// <summary>
    /// Removes at most <paramref name="maxDepth"/> leading blockquote markers.
    /// </summary>
    /// <remarks>Inside a block the quote depth is fixed by the line that opened it, and
    /// a marker past that depth is content: a patch of a markdown file, a doctest or a
    /// heredoc carries <c>&gt;</c> at the head of a line, and stripped it shows a line
    /// the file does not have — on screen and in whatever is copied out of it.</remarks>
    private static string StripQuotes(string line, int maxDepth, out int depth)
    {
        depth = 0;
        int i = 0;
        while (depth < maxDepth)
        {
            int j = i;
            while (j < line.Length && line[j] == ' ' && j - i < 3) j++;
            if (j < line.Length && line[j] == '>')
            {
                depth++;
                j++;
                if (j < line.Length && line[j] == ' ') j++;
                i = j;
            }
            else break;
        }
        return depth > 0 ? line[i..] : line;
    }

    private sealed class ListCtx
    {
        public int MarkerIndent;
        public int ContentIndent;
        public int Counter;
        public bool Ordered;
    }

    private sealed class State
    {
        public readonly List<MarkdownBlock> Blocks = new();
        public readonly Dictionary<string, string> Refs = new(StringComparer.OrdinalIgnoreCase);
        public bool SoftBreaks;

        private readonly List<ListCtx> _lists = new();
        private MarkdownBlock? _para;
        private readonly StringBuilder _paraText = new();
        private bool _lastBlank = true;

        /// <summary>The document as it was written — see <see cref="SplitLines"/>.</summary>
        private List<string> _source = new();

        public void Run(List<string> lines, List<string> source)
        {
            _source = source;
            for (int i = 0; i < lines.Count; i++)
                i = ProcessLine(lines, i);
            FlushParagraph();
        }

        private int ProcessLine(List<string> lines, int index)
        {
            string stripped = StripQuotes(lines[index], out int quote);

            if (stripped.Trim().Length == 0)
            {
                FlushParagraph();
                _lastBlank = true;
                return index;
            }

            // A change of quote depth always starts a new block.
            if (_para != null && _para.QuoteDepth != quote)
                FlushParagraph();

            int indent = Indent(stripped);
            string content = stripped[indent..];

            // Close list levels this line has outdented past. An open paragraph
            // keeps the list alive (lazy continuation).
            if (_para == null && _lastBlank)
            {
                while (_lists.Count > 0 && indent < _lists[^1].ContentIndent
                       && !IsListMarker(content, out _, out _, out _))
                    _lists.RemoveAt(_lists.Count - 1);
            }

            int listIndent = _lists.Count > 0 ? _lists[^1].ContentIndent : 0;
            int relIndent = Math.Max(0, indent - listIndent);

            // ---- list marker ----
            if (IsListMarker(content, out _, out int markerLen, out bool ordered)
                && (_lists.Count > 0 || relIndent < 4))
            {
                FlushParagraph();
                OpenListLevel(indent, markerLen, ordered, content[..markerLen]);

                string rest = content[markerLen..];
                var block = NewBlock(MarkdownBlockType.Paragraph, quote);
                block.IsListItem = true;
                block.Ordered = _lists[^1].Ordered;
                block.Marker = _lists[^1].Ordered
                    ? _lists[^1].Counter + "."
                    : BulletFor(_lists.Count);

                var task = TaskRx.Match(rest);
                if (task.Success)
                {
                    block.IsTask = true;
                    block.TaskChecked = task.Groups[1].Value is "x" or "X";
                    rest = rest[task.Length..];
                }

                StartParagraph(block, rest);
                _lastBlank = false;
                return index;
            }

            // ---- indented code block (only outside lists) ----
            if (_para == null && _lists.Count == 0 && _lastBlank && indent >= 4)
            {
                var code = new StringBuilder();
                int j = index;
                for (; j < lines.Count; j++)
                {
                    var s = StripQuotes(lines[j], out int q);
                    if (s.Trim().Length == 0)
                    {
                        int k = j + 1;
                        while (k < lines.Count && StripQuotes(lines[k], out _).Trim().Length == 0) k++;
                        if (k < lines.Count && Indent(StripQuotes(lines[k], out _)) >= 4)
                        {
                            code.Append('\n');
                            j = k - 1;
                            continue;
                        }
                        break;
                    }
                    if (q != quote || Indent(s) < 4) break;
                    if (code.Length > 0) code.Append('\n');
                    code.Append(s[4..]);
                }

                var cb = NewBlock(MarkdownBlockType.CodeBlock, quote);
                cb.Code = code.ToString();
                Blocks.Add(cb);
                _lastBlank = false;
                return j - 1;
            }

            // ---- fenced code block ----
            var fence = FenceRx.Match(content);
            if (fence.Success && relIndent < 4 && !ThematicBreakRx.IsMatch(content))
            {
                FlushParagraph();
                string fenceChars = fence.Groups[1].Value;
                char fenceChar = fenceChars[0];
                int fenceLen = fenceChars.Length;
                string info = fence.Groups[2].Value.Trim();

                var code = new StringBuilder();
                int j = index + 1;
                for (; j < lines.Count; j++)
                {
                    // Only the quote markers this block was opened at: deeper down the
                    // line, a `>` is one of its own characters. And the line as it was
                    // written, so a patch indented with tabs is still indented with tabs
                    // once it has been selected and copied.
                    if (ClosesFence(StripQuotes(lines[j], quote, out _).Trim(), fenceChar, fenceLen))
                        break;
                    if (code.Length > 0) code.Append('\n');
                    code.Append(StripIndent(StripQuotes(_source[j], quote, out _), indent));
                }

                var cb = NewBlock(MarkdownBlockType.CodeBlock, quote);
                cb.Code = code.ToString();
                cb.CodeLanguage = info.Length > 0 ? info.Split(' ')[0] : null;
                Blocks.Add(cb);
                _lastBlank = false;
                return Math.Min(j, lines.Count - 1);
            }

            // ---- setext heading (underlined paragraph) ----
            // Checked before the thematic break: "---" under an open paragraph is
            // a level-2 heading, not a rule.
            if (_para != null && _para.Type == MarkdownBlockType.Paragraph && !_para.IsListItem
                && relIndent < 4 && SetextRx.IsMatch(content))
            {
                _para.Type = MarkdownBlockType.Heading;
                _para.HeadingLevel = content[0] == '=' ? 1 : 2;
                FlushParagraph();
                _lastBlank = false;
                return index;
            }

            // ---- thematic break ----
            if (relIndent < 4 && ThematicBreakRx.IsMatch(content))
            {
                FlushParagraph();
                Blocks.Add(NewBlock(MarkdownBlockType.ThematicBreak, quote));
                _lastBlank = false;
                return index;
            }

            // ---- ATX heading ----
            var atx = AtxHeadingRx.Match(content);
            if (atx.Success && relIndent < 4)
            {
                FlushParagraph();
                var h = NewBlock(MarkdownBlockType.Heading, quote);
                h.HeadingLevel = atx.Groups[1].Value.Length;
                h.Inlines.AddRange(ParseInlines(atx.Groups[2].Value.Trim(), Refs));
                Blocks.Add(h);
                _lastBlank = false;
                return index;
            }

            // ---- GFM table ----
            if (_para == null && TryStartTable(lines, index, quote, out int rowCount))
                return index + rowCount - 1;

            // ---- paragraph ----
            if (_para == null)
                StartParagraph(NewBlock(MarkdownBlockType.Paragraph, quote), content);
            else
                AppendParagraphLine(content);

            _lastBlank = false;
            return index;
        }

        private static string StripIndent(string line, int count)
        {
            int i = 0;
            while (i < count && i < line.Length && line[i] == ' ') i++;
            return line[i..];
        }

        private static string BulletFor(int depth) => depth switch
        {
            1 => "•",
            2 => "◦",
            _ => "▪"
        };

        private static bool IsListMarker(string content, out string marker, out int markerLen, out bool ordered)
        {
            marker = string.Empty;
            markerLen = 0;
            ordered = false;

            // "- - -" and "***" are thematic breaks, not list items.
            if (ThematicBreakRx.IsMatch(content)) return false;

            var b = BulletRx.Match(content);
            if (b.Success)
            {
                marker = b.Groups[1].Value;
                markerLen = b.Length;
                return true;
            }

            var o = OrderedRx.Match(content);
            if (o.Success)
            {
                marker = o.Groups[1].Value + o.Groups[2].Value;
                markerLen = o.Length;
                ordered = true;
                return true;
            }
            return false;
        }

        // Determines whether the marker at <paramref name="indent"/> opens a nested
        // list, continues the current one, or closes levels the document outdented past.
        private void OpenListLevel(int indent, int markerLen, bool ordered, string marker)
        {
            while (_lists.Count > 0 && indent < _lists[^1].MarkerIndent)
                _lists.RemoveAt(_lists.Count - 1);

            int number = 1;
            if (ordered)
            {
                var digits = marker.TrimEnd('.', ')', ' ');
                if (!int.TryParse(digits, out number)) number = 1;
            }

            if (_lists.Count == 0 || indent >= _lists[^1].ContentIndent)
            {
                _lists.Add(new ListCtx
                {
                    MarkerIndent = indent,
                    ContentIndent = indent + markerLen,
                    Ordered = ordered,
                    Counter = number
                });
                return;
            }

            var top = _lists[^1];
            if (top.Ordered != ordered)
            {
                top.Ordered = ordered;
                top.Counter = number;
            }
            else if (ordered)
            {
                top.Counter++;
            }
            top.MarkerIndent = indent;
            top.ContentIndent = indent + markerLen;
        }

        private MarkdownBlock NewBlock(MarkdownBlockType type, int quote) => new()
        {
            Type = type,
            QuoteDepth = quote,
            ListDepth = _lists.Count
        };

        private void StartParagraph(MarkdownBlock block, string firstLine)
        {
            _para = block;
            _paraText.Clear();
            AppendLineText(firstLine);
        }

        private void AppendParagraphLine(string line)
        {
            if (_paraText.Length > 0)
            {
                // A trailing backslash marks a hard break requested by the source
                // (either a literal "\" or two trailing spaces, normalised below).
                bool hard = _paraText[^1] == '\\';
                if (hard) _paraText.Length--;
                _paraText.Append(hard || SoftBreaks ? '\n' : ' ');
            }
            AppendLineText(line);
        }

        private void AppendLineText(string line)
        {
            bool hardBreak = line.Length - line.TrimEnd(' ').Length >= 2;
            _paraText.Append(line.TrimEnd());
            if (hardBreak) _paraText.Append('\\');
        }

        private void FlushParagraph()
        {
            if (_para == null) return;
            if (_paraText.Length > 0 && _paraText[^1] == '\\') _paraText.Length--;
            _para.Inlines.AddRange(ParseInlines(_paraText.ToString(), Refs));
            Blocks.Add(_para);
            _para = null;
            _paraText.Clear();
        }

        private bool TryStartTable(List<string> lines, int index, int quote, out int consumed)
        {
            consumed = 0;
            if (index + 1 >= lines.Count) return false;

            string header = StripQuotes(lines[index], out int q0).Trim();
            string delim = StripQuotes(lines[index + 1], out int q1).Trim();

            if (q0 != quote || q1 != quote) return false;
            if (!header.Contains('|')) return false;
            if (!delim.Contains('-') || !TableDelimiterRx.IsMatch(delim)) return false;

            var headerCells = SplitTableRow(header);
            var delimCells = SplitTableRow(delim);
            if (delimCells.Count != headerCells.Count) return false;

            var table = new MarkdownTable();
            foreach (var d in delimCells)
            {
                var t = d.Trim();
                bool left = t.StartsWith(':');
                bool right = t.EndsWith(':');
                table.Alignments.Add(left && right ? MarkdownColumnAlignment.Center
                    : right ? MarkdownColumnAlignment.Right
                    : MarkdownColumnAlignment.Left);
            }

            AddTableRow(table, headerCells, isHeader: true);

            int j = index + 2;
            for (; j < lines.Count; j++)
            {
                string s = StripQuotes(lines[j], out int q);
                if (q != quote || s.Trim().Length == 0 || !s.Contains('|')) break;
                AddTableRow(table, SplitTableRow(s.Trim()), isHeader: false);
            }

            var block = NewBlock(MarkdownBlockType.Table, quote);
            block.Table = table;
            Blocks.Add(block);
            _lastBlank = false;
            consumed = j - index;
            return true;
        }

        private void AddTableRow(MarkdownTable table, List<string> cells, bool isHeader)
        {
            var row = new MarkdownTableRow { IsHeader = isHeader };
            foreach (var cell in cells)
            {
                var c = new MarkdownTableCell();
                c.Inlines.AddRange(ParseInlines(cell.Trim(), Refs));
                row.Cells.Add(c);
            }
            table.Rows.Add(row);
        }

        private static List<string> SplitTableRow(string line)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            int i = line.StartsWith('|') ? 1 : 0;
            for (; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\\' && i + 1 < line.Length && line[i + 1] == '|') { sb.Append('|'); i++; continue; }
                if (c == '|') { cells.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(c);
            }
            if (sb.ToString().Trim().Length > 0 || cells.Count == 0) cells.Add(sb.ToString());
            return cells;
        }
    }
}
