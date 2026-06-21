using System.ComponentModel;
using Avalonia.Controls;
using MTerminal.ViewModels;
using TodoList.Avalonia.Model;

namespace MTerminal.Views;

public partial class TodoTileView : UserControl
{
    private ImageSyncHelper<TodoImageEntry>? _imageSync;
    private EventHandler<ImagePastedEventArgs>? _imagePastedHandler;
    private TodoTileViewModel? _subscribedVm;

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
        var baseDir = Path.GetDirectoryName(vm.FilePath) ?? ".";
        _imageSync = new ImageSyncHelper<TodoImageEntry>(
            baseDir,
            (key, bmp) => new TodoImageEntry(key, bmp),
            entry => entry.Key,
            entry => entry.Bitmap);

        Editor.Images = _imageSync.Images;
        vm.PropertyChanged += OnVmPropertyChanged;
        _imageSync.SyncWithMarkdown(vm.MdText);

        _imagePastedHandler = (_, args) =>
        {
            var entry = _imageSync.HandleImagePasted(args.Bitmap);
            if (entry != null)
                args.NewKey = entry.Key;
        };
        Editor.ImagePasted += _imagePastedHandler;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MarkdownTileViewModel.MdText))
            _imageSync?.SyncWithMarkdown(_subscribedVm?.MdText);
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

        _imageSync?.DisposeAll();
        _imageSync = null;
    }
}
