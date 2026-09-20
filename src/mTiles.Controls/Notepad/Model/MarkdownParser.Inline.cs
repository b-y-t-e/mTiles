using System;
using System.Collections.Generic;
using System.Text;

namespace Notepad.Avalonia.Model;

public static partial class MarkdownParser
{
    private readonly struct InlineStyle
    {
        public InlineStyle(bool bold, bool italic, bool strike, bool code, string? link)
        {
            Bold = bold; Italic = italic; Strikethrough = strike; Code = code; Link = link;
        }

        public bool Bold { get; }
        public bool Italic { get; }
        public bool Strikethrough { get; }
        public bool Code { get; }
        public string? Link { get; }

        public InlineStyle With(bool? bold = null, bool? italic = null, bool? strike = null,
            bool? code = null, string? link = null) =>
            new(bold ?? Bold, italic ?? Italic, strike ?? Strikethrough, code ?? Code, link ?? Link);
    }

    private static readonly Dictionary<string, string> Entities = new(StringComparer.Ordinal)
    {
        ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'",
        ["nbsp"] = " ", ["hellip"] = "…", ["mdash"] = "—", ["ndash"] = "–",
        ["copy"] = "©", ["reg"] = "®", ["trade"] = "™"
    };

    /// <summary>Parses the inline content of a single block into styled runs.</summary>
    public static List<MarkdownInline> ParseInlines(string? text,
        IReadOnlyDictionary<string, string>? linkReferences = null)
    {
        var result = new List<MarkdownInline>();
        if (!string.IsNullOrEmpty(text))
            ParseInto(result, text, new InlineStyle(), linkReferences, 0);
        return result;
    }

    private const int MaxInlineDepth = 8;

    private static void ParseInto(List<MarkdownInline> output, string text, InlineStyle style,
        IReadOnlyDictionary<string, string>? refs, int depth)
    {
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length == 0) return;
            output.Add(new MarkdownInline
            {
                Text = sb.ToString(),
                Bold = style.Bold,
                Italic = style.Italic,
                Strikethrough = style.Strikethrough,
                Code = style.Code,
                LinkUrl = style.Link
            });
            sb.Clear();
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                sb.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '\n')
            {
                Flush();
                output.Add(new MarkdownInline { LineBreak = true, LinkUrl = style.Link });
                i++;
                continue;
            }

            if (c == '&')
            {
                int semi = text.IndexOf(';', i + 1);
                if (semi > i + 1 && semi - i <= 10
                    && Entities.TryGetValue(text[(i + 1)..semi], out var entity))
                {
                    sb.Append(entity);
                    i = semi + 1;
                    continue;
                }
            }

            if (c == '`')
            {
                int run = RunLength(text, i, '`');
                int close = FindCodeSpanClose(text, i + run, run);
                if (close >= 0)
                {
                    Flush();
                    string code = text[(i + run)..close].Replace('\n', ' ');
                    if (code.Length > 1 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0)
                        code = code[1..^1];
                    output.Add(new MarkdownInline
                    {
                        Text = code,
                        Code = true,
                        Bold = style.Bold,
                        Italic = style.Italic,
                        Strikethrough = style.Strikethrough,
                        LinkUrl = style.Link
                    });
                    i = close + run;
                    continue;
                }
            }

            if (!style.Code && c == '!' && i + 1 < text.Length && text[i + 1] == '['
                && TryParseLink(text, i + 1, refs, out string? imgLabel, out string? imgUrl, out int imgEnd))
            {
                Flush();
                output.Add(new MarkdownInline
                {
                    ImageKey = imgUrl,
                    ImageAlt = imgLabel,
                    Text = imgLabel ?? string.Empty,
                    LinkUrl = style.Link
                });
                i = imgEnd;
                continue;
            }

            if (!style.Code && c == '[' && style.Link == null
                && TryParseLink(text, i, refs, out string? label, out string? url, out int linkEnd))
            {
                Flush();
                if (depth < MaxInlineDepth)
                    ParseInto(output, label ?? string.Empty, style.With(link: url), refs, depth + 1);
                else
                    output.Add(new MarkdownInline { Text = label ?? string.Empty, LinkUrl = url });
                i = linkEnd;
                continue;
            }

            if (!style.Code && c == '<')
            {
                int gt = text.IndexOf('>', i + 1);
                if (gt > i)
                {
                    string inner = text[(i + 1)..gt];
                    if (inner is "br" or "br/" or "br /")
                    {
                        Flush();
                        output.Add(new MarkdownInline { LineBreak = true, LinkUrl = style.Link });
                        i = gt + 1;
                        continue;
                    }
                    if (IsAutolink(inner))
                    {
                        Flush();
                        string target = inner.Contains("://") || inner.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                            ? inner : "mailto:" + inner;
                        output.Add(new MarkdownInline { Text = inner, LinkUrl = style.Link ?? target });
                        i = gt + 1;
                        continue;
                    }
                }
            }

            if (!style.Code && c == '~' && RunLength(text, i, '~') >= 2 && !style.Strikethrough)
            {
                int close = FindDelimiterClose(text, i + 2, '~', 2);
                if (close >= 0 && depth < MaxInlineDepth)
                {
                    Flush();
                    ParseInto(output, text[(i + 2)..close], style.With(strike: true), refs, depth + 1);
                    i = close + 2;
                    continue;
                }
            }

            if (!style.Code && (c == '*' || c == '_'))
            {
                int run = RunLength(text, i, c);
                bool canOpen = i + run < text.Length && !char.IsWhiteSpace(text[i + run]);
                if (c == '_' && i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_'))
                    canOpen = false;

                if (canOpen)
                {
                    for (int want = Math.Min(run, 3); want >= 1; want--)
                    {
                        int close = FindDelimiterClose(text, i + run, c, want);
                        if (close < 0) continue;

                        Flush();
                        if (run > want) sb.Append(c, run - want);
                        Flush();

                        var inner = style.With(
                            bold: want >= 2 ? true : style.Bold,
                            italic: want is 1 or 3 ? true : style.Italic);

                        if (depth < MaxInlineDepth)
                            ParseInto(output, text[(i + run)..close], inner, refs, depth + 1);
                        else
                            output.Add(new MarkdownInline { Text = text[(i + run)..close] });

                        i = close + want;
                        goto next;
                    }
                }
            }

            sb.Append(c);
            i++;
        next: ;
        }

        Flush();
    }

    private static bool IsEscapable(char c) =>
        !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c);

    private static bool IsAutolink(string inner)
    {
        if (inner.Length == 0 || inner.Contains(' ')) return false;
        if (inner.Contains("://")) return true;
        if (inner.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return true;
        int at = inner.IndexOf('@');
        return at > 0 && at < inner.Length - 1 && inner.IndexOf('.', at) > at;
    }

    private static int RunLength(string text, int start, char c)
    {
        int n = 0;
        while (start + n < text.Length && text[start + n] == c) n++;
        return n;
    }

    /// <summary>Finds the index of a closing backtick run of exactly <paramref name="run"/> ticks.</summary>
    private static int FindCodeSpanClose(string text, int from, int run)
    {
        int i = from;
        while (i < text.Length)
        {
            if (text[i] != '`') { i++; continue; }
            int n = RunLength(text, i, '`');
            if (n == run) return i;
            i += n;
        }
        return -1;
    }

    /// <summary>
    /// Finds a closing emphasis delimiter run of at least <paramref name="need"/>
    /// characters, skipping escapes and code spans.
    /// </summary>
    private static int FindDelimiterClose(string text, int from, char delim, int need)
    {
        int i = from;
        while (i < text.Length)
        {
            char ch = text[i];

            if (ch == '\\') { i += 2; continue; }

            if (ch == '`')
            {
                int n = RunLength(text, i, '`');
                int close = FindCodeSpanClose(text, i + n, n);
                i = close < 0 ? i + n : close + n;
                continue;
            }

            if (ch == delim)
            {
                int n = RunLength(text, i, delim);
                bool closes = n >= need && i > from && !char.IsWhiteSpace(text[i - 1]);
                if (closes && delim == '_')
                {
                    int after = i + n;
                    if (after < text.Length && char.IsLetterOrDigit(text[after])) closes = false;
                }
                if (closes) return i;
                i += n;
                continue;
            }

            i++;
        }
        return -1;
    }

    /// <summary>
    /// Parses <c>[label](dest "title")</c>, <c>[label][ref]</c> and <c>[ref]</c> starting
    /// at the opening bracket. Returns false when the construct is not a link.
    /// </summary>
    private static bool TryParseLink(string text, int start, IReadOnlyDictionary<string, string>? refs,
        out string? label, out string? url, out int end)
    {
        label = null;
        url = null;
        end = start;

        int close = FindMatchingBracket(text, start);
        if (close < 0) return false;

        label = text[(start + 1)..close];
        int after = close + 1;

        if (after < text.Length && text[after] == '(')
        {
            int paren = FindMatchingParen(text, after);
            if (paren < 0) return false;
            url = ExtractDestination(text[(after + 1)..paren]);
            end = paren + 1;
            return true;
        }

        if (refs == null) return false;

        if (after < text.Length && text[after] == '[')
        {
            int refClose = FindMatchingBracket(text, after);
            if (refClose < 0) return false;
            string key = text[(after + 1)..refClose].Trim();
            if (key.Length == 0) key = label.Trim();
            if (!refs.TryGetValue(key, out var dest)) return false;
            url = dest;
            end = refClose + 1;
            return true;
        }

        if (refs.TryGetValue(label.Trim(), out var shortcut))
        {
            url = shortcut;
            end = close + 1;
            return true;
        }

        return false;
    }

    private static string ExtractDestination(string inside)
    {
        var s = inside.Trim();
        if (s.StartsWith('<'))
        {
            int gt = s.IndexOf('>');
            if (gt > 0) return s[1..gt];
        }
        int sp = s.IndexOf(' ');
        if (sp > 0) s = s[..sp];
        return s.Trim();
    }

    private static int FindMatchingBracket(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\') { i++; continue; }
            if (c == '[') depth++;
            else if (c == ']')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static int FindMatchingParen(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\') { i++; continue; }
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }
}
