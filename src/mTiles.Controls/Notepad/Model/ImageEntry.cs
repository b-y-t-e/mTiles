using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace Notepad.Avalonia.Model;

public class ImageEntry : INotifyPropertyChanged
{
    private string _key;
    private Bitmap? _bitmap;

    public string Key
    {
        get => _key;
        set { if (_key != value) { _key = !string.IsNullOrEmpty(value) ? value : throw new ArgumentException("Key cannot be null or empty.", nameof(value)); OnPropertyChanged(); } }
    }

    public Bitmap? Bitmap
    {
        get => _bitmap;
        set { if (!ReferenceEquals(_bitmap, value)) { _bitmap = value; OnPropertyChanged(); } }
    }

    public ImageEntry(string key, Bitmap? bitmap = null)
    {
        _key = !string.IsNullOrEmpty(key) ? key : throw new ArgumentException("Key cannot be null or empty.", nameof(key));
        _bitmap = bitmap;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
