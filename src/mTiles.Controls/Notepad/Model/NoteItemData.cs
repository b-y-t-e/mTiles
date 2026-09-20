using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Notepad.Avalonia.Model;

internal class NoteItemData : INotifyPropertyChanged
{
    private string _text = string.Empty;

    public string Text
    {
        get => _text;
        set
        {
            if (_text != value)
            {
                _text = value;
                OnPropertyChanged();
            }
        }
    }

    public NoteItemData() { }

    public NoteItemData(string text)
    {
        _text = text;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
