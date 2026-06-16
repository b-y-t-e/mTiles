using Avalonia.Controls;
using Avalonia.Input;
using MTerminal.ViewModels;

namespace MTerminal.Views;

public partial class LeafPaneView : UserControl
{
    private object? _currentContentVm;
    private string _originalPaneName = "";

    public LeafPaneView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is LeafPaneNodeViewModel leaf)
            SetContent(leaf.Content);
    }

    private void SetContent(object? contentVm)
    {
        if (contentVm == _currentContentVm && ContentHost.Children.Count > 0)
            return;

        _currentContentVm = contentVm;
        ContentHost.Children.Clear();

        if (contentVm == null) return;

        UserControl view = contentVm switch
        {
            TerminalPaneViewModel => new TerminalPaneView { DataContext = contentVm },
            EditorPaneViewModel => new EditorPaneView { DataContext = contentVm },
            _ => throw new InvalidOperationException($"Unknown content type: {contentVm.GetType()}")
        };

        ContentHost.Children.Add(view);
    }

    private void PaneNameLabel_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is LeafPaneNodeViewModel leaf)
            _originalPaneName = leaf.PaneName;

        PaneNameLabel.IsVisible = false;
        PaneNameEditor.IsVisible = true;
        PaneNameEditor.Focus();
        PaneNameEditor.SelectAll();
    }

    private void PaneNameEditor_Confirm(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CommitRename();
    }

    private void PaneNameEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (DataContext is LeafPaneNodeViewModel leaf)
                leaf.PaneName = _originalPaneName;
            PaneNameEditor.IsVisible = false;
            PaneNameLabel.IsVisible = true;
            e.Handled = true;
        }
    }

    private void CommitRename()
    {
        if (!PaneNameEditor.IsVisible) return;

        if (DataContext is LeafPaneNodeViewModel leaf && string.IsNullOrWhiteSpace(leaf.PaneName))
            leaf.PaneName = _originalPaneName;

        PaneNameEditor.IsVisible = false;
        PaneNameLabel.IsVisible = true;
    }
}
