using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MTerminal.Models;

namespace MTerminal.ViewModels;

public partial class LeafPaneNodeViewModel : PaneNodeViewModel
{
    [ObservableProperty]
    private ObservableObject? _content;

    [ObservableProperty]
    private PaneContentType _contentType;

    [ObservableProperty]
    private string _paneName = "";

    partial void OnPaneNameChanged(string value) => NotifyLayoutChanged();

    private readonly Func<PaneContentType, string, ObservableObject>? _contentFactory;
    private readonly Func<PaneContentType, string>? _nameFactory;
    private readonly string _workingDirectory;

    public Action<PaneNodeViewModel>? RootReplaced { get; set; }
    public Action? RootCleared { get; set; }

    public LeafPaneNodeViewModel(PaneContentType contentType, ObservableObject content, string workingDirectory,
        Func<PaneContentType, string, ObservableObject>? contentFactory = null,
        Func<PaneContentType, string>? nameFactory = null)
    {
        _contentType = contentType;
        _content = content;
        _workingDirectory = workingDirectory;
        _contentFactory = contentFactory;
        _nameFactory = nameFactory;
    }

    [RelayCommand]
    private void SplitHorizontalTerminal() => Split(Orientation.Horizontal, PaneContentType.Terminal);

    [RelayCommand]
    private void SplitVerticalTerminal() => Split(Orientation.Vertical, PaneContentType.Terminal);

    [RelayCommand]
    private void SplitHorizontalEditor() => Split(Orientation.Horizontal, PaneContentType.TextEditor);

    [RelayCommand]
    private void SplitVerticalEditor() => Split(Orientation.Vertical, PaneContentType.TextEditor);

    private void Split(Orientation orientation, PaneContentType newPaneType)
    {
        var newContent = _contentFactory?.Invoke(newPaneType, _workingDirectory);
        if (newContent == null) return;

        var newLeaf = new LeafPaneNodeViewModel(newPaneType, newContent, _workingDirectory, _contentFactory, _nameFactory)
        {
            PaneName = _nameFactory?.Invoke(newPaneType) ?? newPaneType.ToString(),
            LayoutChanged = LayoutChanged,
            RootReplaced = RootReplaced,
            RootCleared = RootCleared
        };

        var oldParent = Parent;

        var split = new SplitPaneNodeViewModel(orientation, this, newLeaf)
        {
            Parent = oldParent,
            LayoutChanged = LayoutChanged
        };

        this.Parent = split;
        newLeaf.Parent = split;

        if (oldParent is SplitPaneNodeViewModel parentSplit)
        {
            if (parentSplit.First == this)
                parentSplit.First = split;
            else
                parentSplit.Second = split;
        }
        else
        {
            RootReplaced?.Invoke(split);
        }

        NotifyLayoutChanged();
    }

    [RelayCommand]
    private void Close()
    {
        if (Content is IDisposable disposable)
            disposable.Dispose();

        if (Parent is not SplitPaneNodeViewModel parentSplit)
        {
            RootCleared?.Invoke();
            return;
        }

        var sibling = (this == parentSplit.First) ? parentSplit.Second : parentSplit.First;
        if (sibling == null) { RootCleared?.Invoke(); return; }

        sibling.Parent = parentSplit.Parent;
        sibling.LayoutChanged = LayoutChanged;
        PropagateSiblingCallbacks(sibling);

        if (parentSplit.Parent is SplitPaneNodeViewModel grandParent)
        {
            if (parentSplit == grandParent.First)
                grandParent.First = sibling;
            else
                grandParent.Second = sibling;
        }
        else
        {
            RootReplaced?.Invoke(sibling);
        }

        NotifyLayoutChanged();
    }

    private void PropagateSiblingCallbacks(PaneNodeViewModel node)
    {
        if (node is LeafPaneNodeViewModel leaf)
        {
            leaf.RootReplaced = RootReplaced;
            leaf.RootCleared = RootCleared;
            leaf.LayoutChanged = LayoutChanged;
        }
        else if (node is SplitPaneNodeViewModel split)
        {
            split.LayoutChanged = LayoutChanged;
            if (split.First != null) PropagateSiblingCallbacks(split.First);
            if (split.Second != null) PropagateSiblingCallbacks(split.Second);
        }
    }
}
