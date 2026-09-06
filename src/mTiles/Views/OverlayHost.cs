using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Linq;
using mTiles.Services;

namespace mTiles.Views;

/// <summary>
/// Where a dialog is drawn: inside the main window, over a scrim, rather than as a window of its own.
/// </summary>
/// <remarks>
/// <para>A dialog used to be a <see cref="Window"/> with <c>WindowStartupLocation="CenterOwner"</c>
/// and <c>CanResize="False"</c> — two requests a tiling window manager does not honour. On Hyprland
/// (and i3, sway, and the rest) every one of them is placed into the layout as an ordinary window:
/// the wizard lands in a tile beside the terminal it was supposed to be modal over, at whatever size
/// the layout decides. Settings never had the problem because it was already drawn this way, and
/// this is that arrangement made reusable.</para>
/// <para><b>Only the X closes an overlay.</b> Settings closes on a click in its own scrim as well,
/// and keeps that — it is a place you leave, and the gesture is forgiving there because nothing in
/// it is lost. These are questions with answers, where the same gesture is a misclick that discards
/// what was typed. A dialog's own Cancel button and its own Escape handler are untouched: those are
/// the user saying so, which a click on the background is not.</para>
/// <para>Overlays stack, because they nest in practice: Settings is itself drawn this way and asks
/// for confirmation before deleting a speech model. Each entry brings its own scrim, and
/// <see cref="Panel"/> draws its children in order, so the newest is on top and takes the pointer.</para>
/// </remarks>
public sealed class OverlayHost : Panel
{
    /// <summary>Content that wants the keyboard when its overlay opens.</summary>
    /// <remarks>Replaces <c>Window.OnOpened</c>, which a control no longer has. Without it the first
    /// thing a user does after being asked for a tag name is reach for the mouse.</remarks>
    public interface IFocusOnOpen
    {
        void FocusOnOpen();
    }

    /// <summary>The host drawn in <paramref name="anchor"/>'s own window, if there is one.</summary>
    /// <remarks>Found by walking rather than by name: the caller is a tile's code-behind somewhere
    /// deep in the tree, and it should not have to know what the main window called its host.</remarks>
    public static OverlayHost? For(Visual? anchor)
    {
        if (anchor is null)
            return null;

        var top = anchor as Window ?? TopLevel.GetTopLevel(anchor) as Window;
        return top?.GetVisualDescendants().OfType<OverlayHost>().FirstOrDefault();
    }

    /// <summary>Draws <paramref name="content"/> as a modal card and completes when it closes.</summary>
    /// <param name="width">The card's width. A dialog that used to be a window has one already.</param>
    /// <param name="height">A fixed height, for content that does not size itself.</param>
    public Task<T?> ShowAsync<T>(Control content, double width, double? height = null)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new OverlayEntry(content, tcs, width, height);

        Children.Add(entry);
        IsVisible = true;
        entry.Modal = ModalScope.Enter();
        TrapFocus();

        if (content is IFocusOnOpen wantsFocus)
            Dispatcher.UIThread.Post(wantsFocus.FocusOnOpen, DispatcherPriority.Input);

        return tcs.Task.ContinueWith(
            t => t.Result is T value ? value : default,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Closes the overlay <paramref name="content"/> is drawn in, answering with
    /// <paramref name="result"/>.</summary>
    /// <remarks>Walks up from the content rather than taking a handle, so the call reads the same
    /// wherever inside a dialog it is made — which is what lets a converted dialog keep the shape its
    /// <c>Window.Close(result)</c> calls already had.</remarks>
    public static void CloseWith(Visual content, object? result)
    {
        var entry = content as OverlayEntry ?? content.FindAncestorOfType<OverlayEntry>();
        if (entry is null)
            return;

        if (entry.Parent is OverlayHost host)
        {
            host.Children.Remove(entry);
            host.IsVisible = host.Children.Count > 0;
            entry.Modal?.Dispose();
            host.TrapFocus();
        }

        entry.Tcs.TrySetResult(result);
    }

    /// <summary>Keeps the keyboard inside the topmost dialog while one is open.</summary>
    /// <remarks>
    /// <para>The scrim is hit-testable, so the pointer cannot reach past it — the keyboard can. Tab
    /// walks the visual tree and knows nothing about a Border drawn over things, so it stepped
    /// straight into the workspace list and the tile behind the question being asked.</para>
    /// <para>Two halves, and both are needed. <c>KeyboardNavigation.TabNavigation = Cycle</c> on the
    /// card makes Tab wrap within the dialog once focus is inside it; the handler below is what
    /// answers the case Cycle cannot, which is focus arriving from outside — a click that lands
    /// before the scrim swallows it, a control restoring focus as it goes away, the very first Tab of
    /// a dialog whose content took no focus of its own.</para>
    /// <para>Attached to the top level rather than to the entry: the focus we have to catch is the
    /// one going somewhere else, which never bubbles through the dialog at all.</para>
    /// </remarks>
    private void TrapFocus()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;

        top.RemoveHandler(InputElement.GotFocusEvent, OnFocusMoved);
        if (Children.Count > 0)
            top.AddHandler(InputElement.GotFocusEvent, OnFocusMoved, RoutingStrategies.Bubble);
    }

    private void OnFocusMoved(object? sender, RoutedEventArgs e)
    {
        if (Children.Count == 0 || Children[^1] is not OverlayEntry topmost)
            return;

        // Already inside the dialog that is asking: nothing to do, including when it is a nested one.
        if (e.Source is Visual v && (ReferenceEquals(v, topmost) || v.FindAncestorOfType<OverlayEntry>() == topmost))
            return;

        // Posted rather than done here: this runs *during* the focus change, and moving focus again
        // inside the notification is how a focus manager ends up in a loop with itself.
        Dispatcher.UIThread.Post(() => topmost.TakeFocus(), DispatcherPriority.Input);
    }

    /// <summary>One open dialog: its scrim, its card, and the answer it owes its caller.</summary>
    private sealed class OverlayEntry : Panel
    {
        public TaskCompletionSource<object?> Tcs { get; }

        /// <summary>Held for as long as this dialog is on screen — see <see cref="ModalScope"/>.</summary>
        public IDisposable? Modal { get; set; }

        private readonly Border _card;
        private readonly Control _content;

        public OverlayEntry(Control content, TaskCompletionSource<object?> tcs, double width, double? height)
        {
            Tcs = tcs;
            _content = content;

            // Hit-testable and silent: it stops the pointer reaching the application behind it, and
            // does nothing when clicked. That difference from Settings is the whole point — see the
            // class remarks.
            var scrim = new Border();

            // Bound rather than resolved once: this runs in the constructor, before the entry is in
            // the visual tree, where a one-shot lookup finds nothing and leaves the scrim transparent
            // — the dialog then floats over a live-looking application with no dimming at all. It is
            // also what makes the scrim follow a theme change, the same as every other colour here.
            scrim.Bind(Border.BackgroundProperty,
                this.GetResourceObservable("OverlayModal").ToBinding());
            scrim.PointerPressed += (_, e) => e.Handled = true;

            var close = new Button
            {
                Classes = { "toolbar" },
                HorizontalAlignment = HorizontalAlignment.Right,
                Content = new Material.Icons.Avalonia.MaterialIcon
                {
                    Kind = Material.Icons.MaterialIconKind.Close,
                    Width = 16,
                    Height = 16,
                },
            };
            close.Click += (_, _) => CloseWith(this, null);

            var layout = new DockPanel();
            DockPanel.SetDock(close, Dock.Top);
            layout.Children.Add(close);
            layout.Children.Add(content);

            // The card the content no longer draws for itself: one radius, one hairline, one shadow,
            // the same as the Settings card, so two dialogs cannot disagree about what a dialog is.
            // The size is the card's, not the content's, and it is a preference rather than a
            // demand: MaxWidth/MaxHeight are clamped to the window in OnSizeChanged below, and in
            // Avalonia a max beats a fixed size. A dialog written for a 900px window therefore
            // narrows instead of running off the edge of a smaller one — which a real window got
            // from the desktop for free and an overlay has to be given.
            var card = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 6, 8, 8),
                Width = width,
                Height = height ?? double.NaN,
                Child = layout,
            };
            _card = card;

            // Tab wraps within the dialog instead of walking out of the bottom of it. The other half
            // of the trap — focus arriving from outside — is OverlayHost.OnFocusMoved.
            KeyboardNavigation.SetTabNavigation(card, KeyboardNavigationMode.Cycle);
            card.Bind(Border.BackgroundProperty, this.GetResourceObservable("BgElevated").ToBinding());
            card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BorderSubtle").ToBinding());
            card.Bind(Border.BorderThicknessProperty, this.GetResourceObservable("BorderThin").ToBinding());
            card.Bind(Border.CornerRadiusProperty, this.GetResourceObservable("RadiusXl").ToBinding());
            card.BoxShadow = BoxShadows.Parse("0 8 32 0 #60000000");

            Children.Add(scrim);
            Children.Add(card);

            // Escape, once, for every dialog. It was written out four times — in InputDialog, the two
            // wizards and the QR panel — and each copy had to remember to answer with the same thing
            // the X answers with. Bubbling is what makes this safe to share: a dialog that means
            // something else by Escape handles it first and marks it handled, which is exactly what
            // SpeechSetupWizard does while it is waiting for a shortcut to be pressed.
            //
            // Escape is not the gesture the class remarks refuse. That is a click in the scrim, which
            // is a misclick; Escape is the user saying cancel.
            AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Handled || e.Key != Key.Escape)
                    return;
                e.Handled = true;
                CloseWith(this, null);
            }, RoutingStrategies.Bubble);
        }

        /// <summary>Puts the keyboard back inside this dialog.</summary>
        /// <remarks>The content first, when it asked for a particular control — the same answer it
        /// gave when the dialog opened, so focus pulled back lands where it started rather than on
        /// whatever happens to be first in the tree. Otherwise the card takes it, which is enough for
        /// Tab to continue from inside.</remarks>
        public void TakeFocus()
        {
            if (_content is OverlayHost.IFocusOnOpen wantsFocus)
            {
                wantsFocus.FocusOnOpen();
                return;
            }

            _card.Focus();
        }

        /// <summary>Keeps the card inside the window it is drawn in.</summary>
        /// <remarks>A margin either side, so the card is visibly laid on the window rather than
        /// filling it — the same reason every tile has a gutter.</remarks>
        protected override Size ArrangeOverride(Size finalSize)
        {
            _card.MaxWidth = Math.Max(1, finalSize.Width - 48);
            _card.MaxHeight = Math.Max(1, finalSize.Height - 48);
            return base.ArrangeOverride(finalSize);
        }
    }
}
