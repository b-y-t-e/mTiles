using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using mTiles.ViewModels.AgentConversation;

namespace mTiles.Views;

/// <summary>
/// Draws an agent conversation, follows its end, and asks the questions its view model needs asked.
/// </summary>
public partial class AgentConversationTileView : UserControl
{
    private AgentConversationTileViewModel? _subscribed;
    private bool _scrollQueued;

    public AgentConversationTileView()
    {
        InitializeComponent();
        ModelBox.ItemFilter = (search, item) => ModelSearch.Matches(search, item as string);
        Composer.AddHandler(DragDrop.DragOverEvent, Composer_DragOver);
        Composer.AddHandler(DragDrop.DropEvent, Composer_Drop);

        // The keys and gestures every conversation's composer answers to — see ComposerInput.
        ComposerInput.Attach(InputBox, Send, () => IsPickingAFile, Composer,
            bitmap => _subscribed?.AttachImageCommand.Execute(ComposerImages.FromBitmap(bitmap, "pasted image")));
    }

    private void ModelBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _subscribed is null) return;
        e.Handled = true;
        _subscribed.ApplyModelCommand.Execute(null);
    }

    /// <summary>A model picked from the list is a choice; the list closing over a half-typed filter is not.</summary>
    private void ModelBox_DropDownClosed(object? sender, EventArgs e)
    {
        if (ModelBox.SelectedItem is string picked && picked == ModelBox.Text?.Trim())
            _subscribed?.ApplyModelCommand.Execute(null);
    }

    /// <summary>Only Enter or a pick confirms a model, so leaving the field puts back the one running.</summary>
    private void ModelBox_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!ModelBox.IsDropDownOpen && !ModelBox.IsKeyboardFocusWithin) _subscribed?.DiscardTypedModel();
    }

    private static void Composer_DragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;

    private async void Composer_Drop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is not { Length: > 0 } files) return;
        e.Handled = true;
        await AttachFilesAsync(files);
    }

    private async void AttachButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Attach images",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("Images")
                {
                    Patterns = [.. ComposerImages.Extensions.Select(extension => $"*.{extension}")],
                },
            ],
        });
        await AttachFilesAsync(files);
    }

    private async Task AttachFilesAsync(IEnumerable<Avalonia.Platform.Storage.IStorageItem> files)
    {
        foreach (var file in files)
            if (await ComposerImages.FromFileAsync(file) is { } image)
                _subscribed?.AttachImageCommand.Execute(image);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscribed is not null)
        {
            _subscribed.TimelineChanged -= FollowTheEnd;
            _subscribed.ConfirmAction = null;
            _subscribed = null;
        }

        if (DataContext is not AgentConversationTileViewModel vm) return;
        _subscribed = vm;
        vm.TimelineChanged += FollowTheEnd;
        vm.ConfirmAction = message => MessageDialog.ConfirmAsync(this, "Confirm", message, whenUnavailable: false);
        if (VisualRoot is not null) vm.EnsureStarted();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _subscribed?.EnsureStarted();
    }

    private void Send()
    {
        if (_subscribed?.SendCommand is { } send && send.CanExecute(null)) send.Execute(null);
    }

    /// <summary>Escape stops a working agent; everything else the composer answers to is ComposerInput's.</summary>
    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _subscribed is not { IsWorking: true } vm) return;
        e.Handled = true;
        vm.InterruptCommand.Execute(null);
    }

    /// <summary>Enter sends the round, as it does in the Goal tile's answer boxes.</summary>
    private void AnswerBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || IsPickingAFile) return;
        if (_subscribed?.PendingQuestions is not { } round) return;

        round.SubmitCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Whether the <c>@</c> suggestions are up, in which case Enter takes the file rather than sends
    /// — the second lock the Goal tile keeps behind <see cref="FileMentionBehavior"/>'s own.</summary>
    private bool IsPickingAFile => _subscribed?.FileMentions.IsOpen == true;

    /// <summary>Scrolls to the end after the new content is laid out, if the reader was at the end.</summary>
    private void FollowTheEnd()
    {
        var scroll = ChatScroll;
        // The Goal tile's rule: further up, the reader has scrolled back on purpose.
        var atEnd = TranscriptFollow.ShouldFollow(scroll.Extent.Height, scroll.Viewport.Height, scroll.Offset.Y);
        if (!atEnd || _scrollQueued) return;

        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            scroll.ScrollToEnd();
        }, DispatcherPriority.Loaded);
    }
}
