using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using mTiles.AgentSessions.Events;
using mTiles.Controls;
using Avalonia.Platform.Storage;
using mTiles.Services;
using mTiles.ViewModels.AgentConversation;

namespace mTiles.Views;

/// <summary>
/// Draws an agent conversation, follows its end, and asks the questions its view model needs asked.
/// </summary>
public partial class AgentConversationTileView : UserControl, IFocusTargetView
{
    /// <summary>The composer: a conversation is driven by what is typed into it.</summary>
    public InputElement? PreferredFocusTarget => InputBox;

    private AgentConversationTileViewModel? _subscribed;

    /// <summary>What keeps the reader in place — and what a send asks for the end of.</summary>
    private readonly TranscriptAnchor _anchor;

    public AgentConversationTileView()
    {
        InitializeComponent();
        _anchor = TranscriptAnchor.Attach(ChatScroll);
        TeachThePickers();
        FitTheRows();
        // Anywhere on the tile, not only on the composer: the transcript is most of the card, and a
        // picture let go over it used to be dropped on the floor. It lands in the composer either way —
        // attached, never sent.
        ImageDrop.Attach(this, DropHint,
            items => _subscribed is not null && (items.HasPicture || items.Files.Count > 0),
            AttachDroppedAsync);

        // The keys and gestures every conversation's composer answers to — see ComposerInput.
        ComposerInput.Attach(InputBox, Send, () => IsPickingAFile, Composer,
            bitmap => _subscribed?.AttachImageCommand.Execute(ComposerImages.FromBitmap(bitmap, "pasted image")),
            AttachFilesAsync);
        ComposerHistoryInput.Attach(InputBox, SentMessages, () => IsPickingAFile, HistoryPicker);
    }

    /// <summary>What was sent in this conversation, oldest first.</summary>
    private IReadOnlyList<string> SentMessages() => _subscribed is null
        ? []
        : _subscribed.Timeline.OfType<MessageItemViewModel>().Where(m => m.IsUser && m.HasText)
            .Select(m => m.Text).ToList();

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

    /// <summary>Takes a drop the way the composer takes one: a picture is attached, anything else is named.</summary>
    /// <remarks>A file this application cannot decode as an image is still something the agent can open
    /// for itself, so its path goes into the message where the caret is — never a send.</remarks>
    private async Task AttachDroppedAsync(DroppedItems items)
    {
        if (items.Bitmap is { } bitmap)
            using (bitmap) _subscribed?.AttachImageCommand.Execute(ComposerImages.FromBitmap(bitmap, "dropped image"));

        await AttachFilesAsync(items.Files);
    }

    private async void AttachButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        await AttachFilesAsync(await ComposerImages.PickAsync(storage));
        InputBox.Focus();
    }

    /// <summary>Attaches every file in the order given — see <see cref="ComposerImages.AttachAllAsync"/>.</summary>
    private Task AttachFilesAsync(IEnumerable<IStorageItem> files) =>
        ComposerImages.AttachAllAsync(files,
            (picture, name) => _subscribed?.AttachImageCommand.Execute(ComposerImages.FromBitmap(picture, name)),
            path => _subscribed?.AttachFileAsync(path) ?? Task.CompletedTask);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscribed is not null)
        {
            _subscribed.SentByUser -= GoToEnd;
            _subscribed.ConfirmAction = null;
            _subscribed = null;
        }

        if (DataContext is not AgentConversationTileViewModel vm) return;
        _subscribed = vm;
        vm.SentByUser += GoToEnd;
        vm.ConfirmAction = message => MessageDialog.ConfirmAsync(this, "Confirm", message, whenUnavailable: false);
        if (VisualRoot is not null) vm.EnsureStarted();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _subscribed?.EnsureStarted();
    }

    /// <summary>Takes the reader to the end because they have just sent something.</summary>
    private void GoToEnd() => _anchor.GoToEnd();

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

    /// <summary>Keeps the composer's settings row and the top strip on one line each, giving up words
    /// before width, in the orders <see cref="ComposerPickerLayout"/> and <see cref="AgentStripLayout"/>
    /// write down. Nothing keeps the fitters but the handlers they hang on these controls, which is
    /// exactly as long as they are needed.</summary>
    private void FitTheRows()
    {
        _ = new RowFitter(PickerRow, ComposerPickerLayout.Steps, ModelPicker, EffortPicker, ModePicker);
        var strip = new RowFitter(StripRow, AgentStripLayout.Steps,
            AgentPicker, StatusView, StopButton, ConversationPicker, NewConversationButton);
        strip.Watch(StatusView, StripStatus.TextProperty);
    }
}
