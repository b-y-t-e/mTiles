namespace Notepad.Avalonia.Model;

public readonly record struct CursorPosition(int ItemIndex, int Offset)
{
    public static CursorPosition Start => new(0, 0);
}

public readonly record struct SelectionRange(CursorPosition Start, CursorPosition End)
{
    public bool IsEmpty => Start == End;

    public (CursorPosition first, CursorPosition last) Ordered()
    {
        if (Start.ItemIndex < End.ItemIndex ||
            (Start.ItemIndex == End.ItemIndex && Start.Offset <= End.Offset))
            return (Start, End);
        return (End, Start);
    }
}
