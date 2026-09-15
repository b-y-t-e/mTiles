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
    /// <summary>How close to the end the transcript has to be for new content to pull it along. Further
    /// up, the reader has scrolled back on purpose, and moving the page under them is the one thing a
    /// transcript must not do.</summary>
    private const double FollowDistance = 48;

    private AgentConversationTileViewModel? _subscribed;
    private bool _scrollQueued;

    public AgentConversationTileView() => InitializeComponent();

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

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_subscribed is null) return;

        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            if (_subscribed.SendCommand.CanExecute(null)) _subscribed.SendCommand.Execute(null);
        }
        else if (e.Key == Key.Escape && _subscribed.IsWorking)
        {
            e.Handled = true;
            _subscribed.InterruptCommand.Execute(null);
        }
    }

    /// <summary>Scrolls to the end after the new content is laid out, if the reader was at the end.</summary>
    private void FollowTheEnd()
    {
        var scroll = ChatScroll;
        var atEnd = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - FollowDistance;
        if (!atEnd || _scrollQueued) return;

        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            scroll.ScrollToEnd();
        }, DispatcherPriority.Loaded);
    }
}
