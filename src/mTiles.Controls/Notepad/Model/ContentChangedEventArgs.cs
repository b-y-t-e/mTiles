using System;

namespace Notepad.Avalonia.Model;

public enum ChangeKind
{
    TextChanged,
    StructureChanged,
    ImageChanged
}

public class ContentChangedEventArgs : EventArgs
{
    public ChangeKind Kind { get; }

    public ContentChangedEventArgs(ChangeKind kind)
    {
        Kind = kind;
    }
}
