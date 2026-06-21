using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;

namespace MTerminal.Views;

internal sealed class ImageSyncHelper<TEntry> where TEntry : class
{
    private static readonly Regex ImagePattern = new(@"!\[[^\]]*\]\(([^)]+)\)", RegexOptions.Compiled);

    private readonly ObservableCollection<TEntry> _images = [];
    private readonly Func<string, Bitmap, TEntry> _createEntry;
    private readonly Func<TEntry, string> _getKey;
    private readonly Func<TEntry, Bitmap?> _getBitmap;
    private readonly string _baseDir;
    private HashSet<string>? _lastKeys;

    public ObservableCollection<TEntry> Images => _images;

    public ImageSyncHelper(
        string baseDir,
        Func<string, Bitmap, TEntry> createEntry,
        Func<TEntry, string> getKey,
        Func<TEntry, Bitmap?> getBitmap)
    {
        _baseDir = baseDir;
        _createEntry = createEntry;
        _getKey = getKey;
        _getBitmap = getBitmap;
    }

    public TEntry? HandleImagePasted(Bitmap bitmap)
    {
        Directory.CreateDirectory(_baseDir);
        var fileName = $"img_{Guid.NewGuid():N}.png";
        var fullPath = Path.Combine(_baseDir, fileName);
        try
        {
            bitmap.Save(fullPath);
            var entry = _createEntry(fileName, bitmap);
            _images.Add(entry);
            return entry;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Image save failed: {0}", ex.Message);
            return null;
        }
    }

    public void SyncWithMarkdown(string? markdown)
    {
        var activeKeys = new HashSet<string>();
        if (!string.IsNullOrEmpty(markdown))
        {
            foreach (Match match in ImagePattern.Matches(markdown))
                activeKeys.Add(match.Groups[1].Value);
        }

        if (_lastKeys != null && _lastKeys.SetEquals(activeKeys))
            return;
        _lastKeys = activeKeys;

        for (int i = _images.Count - 1; i >= 0; i--)
        {
            if (!activeKeys.Contains(_getKey(_images[i])))
            {
                _getBitmap(_images[i])?.Dispose();
                _images.RemoveAt(i);
            }
        }

        foreach (var key in activeKeys)
        {
            if (_images.Any(i => _getKey(i) == key)) continue;

            var fullPath = Path.IsPathRooted(key) ? key : Path.Combine(_baseDir, key);
            if (!File.Exists(fullPath)) continue;

            try
            {
                _images.Add(_createEntry(key, new Bitmap(fullPath)));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("Image load failed: {0}", ex.Message);
            }
        }
    }

    public void DisposeAll()
    {
        var toDispose = _images.ToList();
        _images.Clear();
        foreach (var entry in toDispose)
            _getBitmap(entry)?.Dispose();
        _lastKeys = null;
    }
}
