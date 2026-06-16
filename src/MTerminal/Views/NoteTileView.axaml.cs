using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using MTerminal.ViewModels;

namespace MTerminal.Views;

public partial class NoteTileView : UserControl
{
    public NoteTileView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not NoteTileViewModel vm) return;

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
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Background = this.FindBrush("BgBase"),
            Foreground = this.FindBrush("TextPrimary")
        };

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

}
