using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;
using mTiles.Services;

namespace mTiles.ViewModels;

/// <summary>
/// The machinery a markdown-backed tile needs: the text, the file behind it, the debounced save and the
/// watcher that follows an edit made outside the application.
/// </summary>
/// <remarks>
/// Abstract, because it is not a kind of tile — <see cref="NoteTileViewModel"/> and
/// <see cref="TodoTileViewModel"/> are, and they differ by what they are called and where their files
/// go rather than by any of this.
/// </remarks>
public abstract partial class MarkdownTileViewModel : ObservableObject, IFileContent, IMaximizableTile
{
    /// <inheritdoc />
    public abstract string KindId { get; }

    [ObservableProperty]
    private string _fontFamily;

    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private string? _mdText;

    private string _filePath;
    private readonly SettingsService? _settingsService;
    private Timer? _saveTimer;
    private Timer? _reloadTimer;
    private bool _isLoading;
    private FileSystemWatcher? _watcher;
    private volatile bool _hasPendingChanges;
    private volatile string _lastSavedText = "";

    /// <summary>The file was there and could not be read — held by an editor, a sync client, an antivirus.
    /// </summary>
    /// <remarks>Until a read succeeds the tile is showing nothing in front of a file that has something, so
    /// nothing here may delete that file: an empty tile disposed in that state used to take the note with it.
    /// </remarks>
    private volatile bool _loadFailed;

    /// <summary>How many more times a file that could not be read is tried again before the tile gives up.
    /// </summary>
    /// <remarks>Bounded, so a file unreadable for good does not arm a timer for the life of the session; long
    /// enough to outlast a sync client hydrating the file or an antivirus scan.</remarks>
    private int _loadRetriesLeft = MaxLoadRetries;

    private const int MaxLoadRetries = 20;

    public string FilePath => _filePath;

    protected MarkdownTileViewModel(string filePath, SettingsService? settingsService = null)
    {
        _filePath = filePath;
        _settingsService = settingsService;
        var s = settingsService?.Settings;
        _fontFamily = s?.FontFamily ?? AppDefaults.FontFamily;
        _fontSize = s is null ? AppDefaults.FontSize : TextScale.UiFontSize(s);
        _isLoading = true;
        LoadFromFile();
        _isLoading = false;

        if (_settingsService != null)
            _settingsService.SettingsChanged += OnSettingsChanged;

        StartWatching();
    }

    partial void OnMdTextChanged(string? value)
    {
        ScheduleSave();
    }

    private void OnSettingsChanged()
    {
        var s = _settingsService!.Settings;
        if (s.FontFamily != FontFamily)
            FontFamily = s.FontFamily;
        if (Math.Abs(TextScale.UiFontSize(s) - FontSize) > AppDefaults.FontSizeEpsilon)
            FontSize = TextScale.UiFontSize(s);
    }

    public void RenameFile(string newName)
    {
        var sanitized = IFileContent.SanitizeFileName(newName);
        if (string.IsNullOrEmpty(sanitized)) return;

        var dir = Path.GetDirectoryName(_filePath);
        if (dir == null) return;

        var newPath = FreePathFor(dir, sanitized);
        if (newPath is null) return;

        _saveTimer?.Dispose();
        _saveTimer = null;
        if (_hasPendingChanges) SaveToFile(_filePath);

        try
        {
            _watcher?.Dispose();
            _watcher = null;

            if (File.Exists(_filePath))
                File.Move(_filePath, newPath, overwrite: false);

            _filePath = newPath;
            _hasPendingChanges = false;
            StartWatching();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("MarkdownTile rename failed: {0}", ex.Message);
            if (_hasPendingChanges) ScheduleSave();
            StartWatching();
        }
    }

    /// <summary>Where a tile named <paramref name="name"/> keeps its file, or null when that is where it
    /// already is.</summary>
    /// <remarks><b>Never a file somebody else's text is in.</b> The tile used to take the name's file whether
    /// or not it existed, without loading it: a new note numbered <c>Note#2</c> — a number freed by a note
    /// closed earlier, or one a lost layout save no longer remembered — adopted <c>Note#2.md</c>, showed an
    /// empty page over it, and deleted it the moment the empty tile was closed. A name that is taken gets a
    /// suffix instead, and the tile keeps its own name.</remarks>
    private string? FreePathFor(string dir, string name)
    {
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, i == 1 ? name + ".md" : $"{name} ({i}).md");
            if (string.Equals(candidate, _filePath, StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private void ScheduleSave()
    {
        if (_isLoading) return;
        _hasPendingChanges = true;
        var text = MdText ?? "";
        var path = _filePath;
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ =>
        {
            if (!TrySaveContent(text, path)) return;
            _lastSavedText = text;
            _hasPendingChanges = false;
        }, null, AppDefaults.SaveDebounceMs, Timeout.Infinite);
    }

    private void LoadFromFile()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var text = File.ReadAllText(_filePath);
            _lastSavedText = text;
            MdText = text;
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            System.Diagnostics.Trace.TraceWarning("MarkdownTile load failed: {0}", ex.Message);
            // Tried again shortly, the way an outside edit is followed: the usual cause is a file held open
            // for a moment, and a tile left empty for the session is one the user types over.
            RetryLoad();
        }
    }

    private void RetryLoad() =>
        OnFileChanged(this, new FileSystemEventArgs(WatcherChangeTypes.Changed, "", null));

    private void SaveToFile(string path)
    {
        var text = MdText ?? "";
        if (TrySaveContent(text, path)) _lastSavedText = text;
    }

    /// <summary>Saves the text, unless the file on disk has never been read — then beside it.</summary>
    /// <remarks>While a load has failed the page is not the file's content, so writing it — typed text or
    /// nothing — would replace a note nobody has seen with whatever was typed over the empty tile. What was
    /// typed is not the file's either, and must not vanish with the tile: it goes to
    /// <see cref="RecoveryPathFor"/>, where both texts survive and the user can put them together.</remarks>
    private bool TrySaveContent(string text, string path)
    {
        if (_loadFailed && File.Exists(path))
        {
            System.Diagnostics.Trace.TraceWarning("MarkdownTile save skipped: '{0}' has not been read yet", path);
            if (string.IsNullOrWhiteSpace(text)) return false;
            var recovery = RecoveryPathFor(path);
            FileHelper.WriteWithRetry(recovery, p => File.WriteAllText(p, text));
            System.Diagnostics.Trace.TraceWarning("MarkdownTile text typed over an unread file kept in '{0}'", recovery);
            return true;
        }

        SaveContent(text, path);
        return true;
    }

    /// <summary>Where text typed over a file that could not be read is kept: <c>Note.unsaved.md</c> beside
    /// <c>Note.md</c>.</summary>
    internal static string RecoveryPathFor(string path) => Path.ChangeExtension(path, ".unsaved.md");

    /// <remarks>An empty page deletes its file: somebody cleared the note. Reached only through
    /// <see cref="TrySaveContent"/>, which refuses while the file has not been read.</remarks>
    private static void SaveContent(string text, string path)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            DeleteFile(path);
            return;
        }

        FileHelper.WriteWithRetry(path, p => File.WriteAllText(p, text));
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("MarkdownTile delete failed: {0}", ex.Message);
        }
    }

    private void StartWatching()
    {
        var dir = Path.GetDirectoryName(_filePath);
        var name = Path.GetFileName(_filePath);
        if (dir == null || name == null) return;

        try
        {
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += (_, e) => OnFileChanged(e, e);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("MarkdownTile watcher failed: {0}", ex.Message);
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        _reloadTimer?.Dispose();
        _reloadTimer = new Timer(_ =>
            Dispatcher.UIThread.Post(ReloadFromFile), null, AppDefaults.WatcherDebounceMs, Timeout.Infinite);
    }

    private void ReloadFromFile()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var text = File.ReadAllText(_filePath);
            if (_loadFailed) KeepTextTypedBeforeTheRead(text);
            _loadFailed = false;
            if (text == _lastSavedText || text == MdText)
                return;

            // A file caught empty in the middle of somebody else's write — a sync client, a checkout, a second
            // copy of this application — is not a page somebody cleared. Taking it would put an empty text in
            // the tile, and the next save or close would then delete the note.
            if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(MdText))
                return;

            _isLoading = true;
            _lastSavedText = text;
            MdText = text;
            _isLoading = false;
        }
        catch (Exception ex)
        {
            _isLoading = false;
            System.Diagnostics.Trace.TraceWarning("MarkdownTile reload failed: {0}", ex.Message);
            if (_loadFailed && --_loadRetriesLeft > 0) RetryLoad();
        }
    }

    /// <summary>Takes back the save still waiting from before the file could be read, and keeps its text
    /// beside the note instead.</summary>
    /// <remarks>That save was armed over an empty page; once the read succeeds, <see cref="TrySaveContent"/>
    /// would no longer refuse it, and the text typed over the empty tile would replace the note just
    /// read.</remarks>
    private void KeepTextTypedBeforeTheRead(string fileText)
    {
        if (!_hasPendingChanges) return;
        _saveTimer?.Dispose();
        _saveTimer = null;
        _hasPendingChanges = false;

        var typed = MdText ?? "";
        if (typed != fileText) TrySaveContent(typed, _filePath);
    }

    public void Dispose()
    {
        if (_settingsService != null)
            _settingsService.SettingsChanged -= OnSettingsChanged;
        _saveTimer?.Dispose();
        _reloadTimer?.Dispose();
        // Only what is still waiting to be written. Saving unconditionally wrote whatever the tile held over
        // the file — and an empty tile deleted it — whether or not anybody had touched the page, which is how
        // a note that never loaded, or a tile standing over somebody else's file, took the text with it.
        if (_hasPendingChanges) SaveToFile(_filePath);
        _watcher?.Dispose();
    }
}
