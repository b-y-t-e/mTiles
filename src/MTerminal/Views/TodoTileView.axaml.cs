using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using MTerminal.ViewModels;
using TodoList.Avalonia.Model;

namespace MTerminal.Views;

public partial class TodoTileView : UserControl
{
    private static readonly Regex ImagePattern = new(@"!\[[^\]]*\]\(([^)]+)\)", RegexOptions.Compiled);

    private EventHandler<ImagePastedEventArgs>? _imagePastedHandler;
    private readonly ObservableCollection<TodoImageEntry> _images = [];
    private TodoTileViewModel? _subscribedVm;
    private string? _baseDir;

    public TodoTileView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Cleanup();

        if (DataContext is not TodoTileViewModel vm) return;

        _subscribedVm = vm;
        _baseDir = Path.GetDirectoryName(vm.FilePath) ?? ".";
        Editor.Images = _images;

        vm.PropertyChanged += OnVmPropertyChanged;

        SyncImagesWithMarkdown(vm.MdText);

        _imagePastedHandler = (_, args) =>
        {
            Directory.CreateDirectory(_baseDir);
            var fileName = $"img_{Guid.NewGuid():N}.png";
            var fullPath = Path.Combine(_baseDir, fileName);
            try
            {
                args.Bitmap.Save(fullPath);
                args.NewKey = fileName;
                _images.Add(new TodoImageEntry(fileName, args.Bitmap));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("TodoTile image save failed: {0}", ex.Message);
            }
        };
        Editor.ImagePasted += _imagePastedHandler;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TodoTileViewModel.MdText))
            SyncImagesWithMarkdown(_subscribedVm?.MdText);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        Cleanup();
        base.OnDetachedFromVisualTree(e);
    }

    private void Cleanup()
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm = null;
        }

        if (_imagePastedHandler != null)
        {
            Editor.ImagePasted -= _imagePastedHandler;
            _imagePastedHandler = null;
        }

        DisposeImages();
    }

    private void SyncImagesWithMarkdown(string? markdown)
    {
        var activeKeys = new HashSet<string>();
        if (!string.IsNullOrEmpty(markdown))
        {
            foreach (Match match in ImagePattern.Matches(markdown))
                activeKeys.Add(match.Groups[1].Value);
        }

        for (int i = _images.Count - 1; i >= 0; i--)
        {
            if (!activeKeys.Contains(_images[i].Key))
            {
                _images[i].Bitmap?.Dispose();
                _images.RemoveAt(i);
            }
        }

        if (_baseDir == null) return;

        foreach (var key in activeKeys)
        {
            if (_images.Any(i => i.Key == key)) continue;

            var fullPath = Path.IsPathRooted(key) ? key : Path.Combine(_baseDir, key);
            if (!File.Exists(fullPath)) continue;

            try
            {
                _images.Add(new TodoImageEntry(key, new Bitmap(fullPath)));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("TodoTile image load failed: {0}", ex.Message);
            }
        }
    }

    private void DisposeImages()
    {
        // Clear collection first so the control removes references from _imageCache,
        // then dispose the bitmaps (avoids use-after-dispose in the control's renderer).
        var toDispose = _images.ToList();
        _images.Clear();
        foreach (var entry in toDispose)
            entry.Bitmap?.Dispose();
    }
}
