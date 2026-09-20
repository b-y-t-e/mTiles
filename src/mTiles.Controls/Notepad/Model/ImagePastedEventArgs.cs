using System;
using Avalonia.Media.Imaging;

namespace Notepad.Avalonia.Model;

public class ImagePastedEventArgs : EventArgs
{
    public Bitmap Bitmap { get; }
    public string Key { get; }
    public string? NewKey { get; set; }

    public ImagePastedEventArgs(Bitmap bitmap, string key)
    {
        Bitmap = bitmap;
        Key = key;
    }
}
