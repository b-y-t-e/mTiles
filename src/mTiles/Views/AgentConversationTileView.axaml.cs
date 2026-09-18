using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using mTiles.AgentSessions.Events;
using mTiles.Controls;
using mTiles.Services;
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
        TeachThePickers();
        Composer.AddHandler(DragDrop.DragOverEvent, Composer_DragOver);
        Composer.AddHandler(DragDrop.DropEvent, Composer_Drop);

        // The keys and gestures every conversation's composer answers to — see ComposerInput.
        ComposerInput.Attach(InputBox, Send, () => IsPickingAFile, Composer,
            bitmap => _subscribed?.AttachImageCommand.Execute(ComposerImages.FromBitmap(bitmap, "pasted image")));
    }

    /// <summary>Reads the conversations as the list is opened.</summary>
    /// <remarks>Here rather than on a timer or on every change: the answer moves only when something is said,
    /// and the tile redraws every frame while an agent replies — a list rebuilt in that loop would flicker and
    /// lose the highlight, which is what <c>AgentInstanceChooser.DrawIfBindingChanged</c> exists to avoid one
    /// control along. Nothing awaits it: the list already holds what was last read, and the newer answer
    /// replaces it when it arrives.</remarks>
    private void ConversationPicker_Opened(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetNewValue<bool>()) _ = _subscribed?.Conversations.RefreshAsync();
    }

    /// <summary>Teaches the composer's three pickers how to read this tile's own lists.</summary>
    /// <remarks><para>The pickers take the view model's collections as they are — a list of model ids, a
    /// list of <see cref="SessionOption"/> — and one line each says how one entry reads. The alternative is
    /// three more <c>ObservableCollection&lt;PickerOption&gt;</c> on the view model, kept in step with the
    /// three that already exist, which is three more things that can disagree with what is running.</para>
    /// <para>The permission list is where the extra words earn their place: <c>AiBehaviours</c> already
    /// writes a sentence per mode, and until now nothing drew it — the strip's combo box had room for the
    /// label alone, so the one place saying what <c>bypass</c> actually grants was a tooltip on the whole
    /// control.</para></remarks>
    private void TeachThePickers()
    {
        ModelPicker.OptionSelector = item =>
            item is string id && id.Length > 0 ? new PickerOption { Id = id, Title = id } : null;

        // The sentence is on the option where we built the list ourselves. Where the agent reported its own
        // options over its protocol it is not, so the vocabulary is asked by id — the same words either
        // way, which is the point of the vocabulary being one place.
        EffortPicker.OptionSelector = item => item is SessionOption option ? SettingPickerRows.Effort(option) : null;
        ModePicker.OptionSelector = item => item is SessionOption option ? SettingPickerRows.Mode(option) : null;

        // The two on the top strip. Their rows are refusable — an agent this conversation cannot be moved
        // to, a conversation another tile is holding — so each one's reason travels with it and the picker
        // draws it dimmed with that sentence under it, which is the arrangement both choosers were written
        // for and which a ComboBox could only approximate with a tooltip nothing could reach.
        ConversationPicker.OptionSelector = item => item is not ConversationOption conversation
            ? null
            : new PickerOption
            {
                Id = conversation.Summary.Id,
                Title = conversation.Title,
                Detail = conversation.Note,
                IsEnabled = conversation.IsPickable,
                DisabledReason = conversation.Reason,
                Keywords = conversation.AgentName,
            };
        ConversationPicker.SelectionRequested += (_, e) => PickConversation(e.Option.Id);
        ConversationPicker.PropertyChanged += (_, e) =>
        {
            if (e.Property == Picker.IsDropDownOpenProperty) ConversationPicker_Opened(null, e);
        };

        AgentPicker.OptionSelector = item => item is not AgentInstanceOption agent
            ? null
            : new PickerOption
            {
                Id = agent.Instance.Id,
                Title = agent.Instance.Name,
                Detail = agent.AgentName,
                IsEnabled = agent.IsPickable,
                DisabledReason = agent.Reason,
            };
        AgentPicker.SelectionRequested += (_, e) => PickAgent(e.Option.Id);

        ModelPicker.SelectionRequested += (_, e) => _subscribed?.ApplyPickedModel(e.Option.Id);
        EffortPicker.SelectionRequested += (_, e) => Choose(e, options => options.EffortOptions,
            (vm, option) => vm.SelectedEffort = option);
        ModePicker.SelectionRequested += (_, e) => Choose(e, options => options.ModeOptions,
            (vm, option) => vm.SelectedMode = option);
    }

    /// <summary>Hands a picked conversation back to the chooser, which owns what picking one means.</summary>
    /// <remarks>Through <c>Selected</c> rather than by calling the chooser's own callbacks: everything the
    /// pick has to do — the refusal that puts the selection back, the switch itself — is already
    /// written there, once, for whatever control is drawing the list.</remarks>
    private void PickConversation(string id)
    {
        if (_subscribed?.Conversations is not { } chooser) return;
        chooser.Selected = chooser.Options.FirstOrDefault(option => option.Summary.Id == id);
    }

    private void PickAgent(string instanceId)
    {
        if (_subscribed?.Chooser is not { } chooser) return;
        if (chooser.Options.FirstOrDefault(option => option.Instance.Id == instanceId) is { } picked)
            chooser.Selected = picked;
    }

    /// <summary>Puts a picked row back on the view model as the option object it came from.</summary>
    /// <remarks>By id rather than by carrying the object through <see cref="PickerOption.Tag"/>: the list
    /// is rebuilt whenever the session reports its options again, so an object captured when the popup
    /// opened can be a different instance from the one the view model is comparing against.</remarks>
    private void Choose(PickerSelectionEventArgs e,
        Func<AgentConversationTileViewModel, IEnumerable<SessionOption>> from,
        Action<AgentConversationTileViewModel, SessionOption> onto)
    {
        if (_subscribed is not { } vm) return;
        if (from(vm).FirstOrDefault(option => option.Id == e.Option.Id) is { } picked) onto(vm, picked);
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
