using System.Collections.Generic;

namespace Notepad.Avalonia.Model;

/// <summary>Kind of a parsed markdown block.</summary>
public enum MarkdownBlockType
{
    Paragraph,
    Heading,
    CodeBlock,
    ThematicBreak,
    Table
}

/// <summary>Horizontal alignment of a markdown table column.</summary>
public enum MarkdownColumnAlignment { Left, Center, Right }

/// <summary>
/// A styled run of inline content: text, a hard line break, or an image.
/// </summary>
public sealed class MarkdownInline
{
    public string Text { get; set; } = string.Empty;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Strikethrough { get; set; }
    public bool Code { get; set; }

    /// <summary>Forces a line break; <see cref="Text"/> is empty.</summary>
    public bool LineBreak { get; set; }

    /// <summary>Target of the enclosing link, or null when not inside a link.</summary>
    public string? LinkUrl { get; set; }

    /// <summary>Image key (the URL part of <c>![alt](key)</c>), or null when this is not an image.</summary>
    public string? ImageKey { get; set; }

    public string? ImageAlt { get; set; }

    public bool IsImage => ImageKey != null;
}

public sealed class MarkdownTableCell
{
    public List<MarkdownInline> Inlines { get; } = new();
}

public sealed class MarkdownTableRow
{
    public List<MarkdownTableCell> Cells { get; } = new();
    public bool IsHeader { get; set; }
}

public sealed class MarkdownTable
{
    public List<MarkdownTableRow> Rows { get; } = new();
    public List<MarkdownColumnAlignment> Alignments { get; } = new();

    public int ColumnCount
    {
        get
        {
            int max = 0;
            foreach (var row in Rows)
                if (row.Cells.Count > max) max = row.Cells.Count;
            return max;
        }
    }
}

/// <summary>
/// One renderable markdown block. Container constructs (blockquotes, lists) are
/// flattened into <see cref="QuoteDepth"/> / <see cref="ListDepth"/> so the
/// renderer can walk a flat list and still draw the right indentation.
/// </summary>
public sealed class MarkdownBlock
{
    public MarkdownBlockType Type { get; set; }

    /// <summary>1..6 for <see cref="MarkdownBlockType.Heading"/>.</summary>
    public int HeadingLevel { get; set; }

    /// <summary>Number of enclosing blockquote levels (0 = not quoted).</summary>
    public int QuoteDepth { get; set; }

    /// <summary>Number of enclosing list levels (0 = not in a list).</summary>
    public int ListDepth { get; set; }

    /// <summary>True when this block is the first block of a list item (draws the marker).</summary>
    public bool IsListItem { get; set; }

    public bool Ordered { get; set; }

    /// <summary>Rendered bullet/number text, e.g. "•" or "3.".</summary>
    public string? Marker { get; set; }

    public bool IsTask { get; set; }
    public bool TaskChecked { get; set; }

    /// <summary>Raw text of a fenced/indented code block.</summary>
    public string Code { get; set; } = string.Empty;

    public string? CodeLanguage { get; set; }

    public List<MarkdownInline> Inlines { get; } = new();

    public MarkdownTable? Table { get; set; }
}
