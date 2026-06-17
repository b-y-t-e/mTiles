using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using MTerminal.ViewModels;

namespace MTerminal.Views;

public partial class NoteTileView : UserControl
{
    private NoteTileViewModel? _subscribedVm;

    public NoteTileView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;

        if (DataContext is not NoteTileViewModel vm) return;

        _subscribedVm = vm;
        vm.PropertyChanged += OnVmPropertyChanged;

        if (vm.CachedControl is TextEditor cached)
        {
            ControlHelper.DetachFromParent(cached);
            Content = cached;
            return;
        }

        var editor = new TextEditor
        {
            FontFamily = new FontFamily(vm.FontFamily),
            FontSize = vm.FontSize,
            ShowLineNumbers = false,
            WordWrap = true,
            Padding = new Thickness(8, 8),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
            Foreground = Brushes.White
        };

        editor.Bind(TextEditor.BackgroundProperty, editor.GetResourceObservable("BgBase"));
        editor.Bind(TextEditor.ForegroundProperty, editor.GetResourceObservable("TextPrimary"));

        editor.Text = vm.Text;

        editor.Document.Changed += (_, _) => vm.Text = editor.Text;

        vm.CachedControl = editor;
        Content = editor;

        AttachedToVisualTree += OnceAttached;

        void OnceAttached(object? s, VisualTreeAttachmentEventArgs args)
        {
            AttachedToVisualTree -= OnceAttached;
            Dispatcher.UIThread.Post(() => editor.TextArea?.Focus(), DispatcherPriority.Input);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NoteTileViewModel vm) return;
        if (vm.CachedControl is not TextEditor editor) return;

        Dispatcher.UIThread.Post(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(NoteTileViewModel.FontFamily):
                    editor.FontFamily = new FontFamily(vm.FontFamily);
                    break;
                case nameof(NoteTileViewModel.FontSize):
                    editor.FontSize = vm.FontSize;
                    break;
            }
        });
    }
}
