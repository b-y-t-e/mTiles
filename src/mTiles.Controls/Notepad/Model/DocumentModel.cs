using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using global::Avalonia.Media;
using global::Avalonia.Media.Imaging;

namespace Notepad.Avalonia.Model;

public enum ContentElementType
{
    Text,
    Image
}

public class ContentElement
{
    public ContentElementType Type { get; set; }
    public string Text { get; set; } = string.Empty;
    public Bitmap? Image { get; set; }
    public double ImageWidth { get; set; }
    public double ImageHeight { get; set; }
    public string? ImageKey { get; set; }
    public string? ImageAltText { get; set; }
    internal string? UnresolvedImageKey { get; set; }
    public FontFamily Font { get; set; } = FontFamily.Default;
    public double FontSize { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }

    public int Length => Type == ContentElementType.Text ? Text.Length : 1;

    public static ContentElement CreateText(string text, FontFamily? font = null, double fontSize = 0)
    {
        return new ContentElement
        {
            Type = ContentElementType.Text,
            Text = text,
            Font = font ?? FontFamily.Default,
            FontSize = fontSize
        };
    }

    public static ContentElement CreateImage(Bitmap bitmap)
    {
        return new ContentElement
        {
            Type = ContentElementType.Image,
            Image = bitmap,
            ImageWidth = bitmap.PixelSize.Width,
            ImageHeight = bitmap.PixelSize.Height
        };
    }

    public ContentElement Clone()
    {
        return new ContentElement
        {
            Type = Type,
            Text = Text,
            Image = Image,
            ImageWidth = ImageWidth,
            ImageHeight = ImageHeight,
            ImageKey = ImageKey,
            ImageAltText = ImageAltText,
            UnresolvedImageKey = UnresolvedImageKey,
            Font = Font,
            FontSize = FontSize,
            Bold = Bold,
            Italic = Italic
        };
    }
}

internal enum CharClass { Whitespace, Word, Punctuation, Image }

public class NoteItem
{
    public List<ContentElement> Elements { get; } = new();

    public NoteItem() { }

    public NoteItem(string text)
    {
        if (text.Length > 0)
            Elements.Add(ContentElement.CreateText(text));
    }

    public string PlainText
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var el in Elements)
            {
                if (el.Type == ContentElementType.Text)
                    sb.Append(el.Text);
                else
                    sb.Append('￼');
            }
            return sb.ToString();
        }
    }

    public int TextLength
    {
        get
        {
            int len = 0;
            foreach (var el in Elements)
                len += el.Length;
            return len;
        }
    }

    public (int elementIndex, int offsetInElement) ResolveOffset(int globalOffset)
    {
        int pos = 0;
        for (int i = 0; i < Elements.Count; i++)
        {
            int elLen = Elements[i].Length;
            bool isLast = i == Elements.Count - 1;
            if (globalOffset < pos + elLen || (isLast && globalOffset <= pos + elLen))
                return (i, globalOffset - pos);
            pos += elLen;
        }
        return (Math.Max(0, Elements.Count - 1), Elements.Count > 0
            ? Elements[^1].Length
            : 0);
    }

    public int GlobalOffset(int elementIndex, int offsetInElement)
    {
        int pos = 0;
        for (int i = 0; i < elementIndex && i < Elements.Count; i++)
            pos += Elements[i].Length;
        return pos + offsetInElement;
    }

    internal CharClass ClassifyAt(int globalOffset)
    {
        if (globalOffset < 0 || globalOffset >= TextLength)
            return CharClass.Whitespace;

        var (elIdx, localOff) = ResolveOffset(globalOffset);
        var el = Elements[elIdx];

        if (el.Type == ContentElementType.Image)
            return CharClass.Image;

        char c = el.Text[localOff];
        if (char.IsWhiteSpace(c)) return CharClass.Whitespace;
        if (char.IsLetterOrDigit(c) || c == '_') return CharClass.Word;
        return CharClass.Punctuation;
    }

    public int FindWordBoundaryLeft(int fromOffset)
    {
        if (fromOffset <= 0) return 0;

        int pos = fromOffset - 1;

        while (pos > 0 && ClassifyAt(pos) == CharClass.Whitespace)
            pos--;

        if (pos <= 0)
            return 0;

        var cls = ClassifyAt(pos);
        if (cls == CharClass.Image)
            return pos;

        while (pos > 0 && ClassifyAt(pos - 1) == cls)
            pos--;

        return pos;
    }

    public int FindWordBoundaryRight(int fromOffset)
    {
        int len = TextLength;
        if (fromOffset >= len) return len;

        int pos = fromOffset;

        var cls = ClassifyAt(pos);
        if (cls == CharClass.Image)
            return pos + 1;

        while (pos < len && ClassifyAt(pos) == cls)
            pos++;

        while (pos < len && ClassifyAt(pos) == CharClass.Whitespace)
            pos++;

        return pos;
    }
}

public class NoteDocument
{
    public ObservableCollection<NoteItem> Items { get; } = new();
    public FontFamily DefaultFont { get; set; } = FontFamily.Default;
    public double DefaultFontSize { get; set; } = 14;

    public NoteDocument()
    {
        Items.Add(new NoteItem());
    }

    public string GetAllText()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Items.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            sb.Append(Items[i].PlainText);
        }
        return sb.ToString();
    }
}
