using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    /// <summary>Content that puts something of its own in the dialog's header row.</summary>
    /// <remarks>The header is the host's — one close button, in one place, for every dialog — and this
    /// is the seam for a dialog that has more to put there. Settings has its four tabs, which is why
    /// its card was hand-written in <c>MainWindow</c> rather than drawn here; without this the tabs
    /// would have had to move into the page below, one row down from where the eye expects them.
    /// </remarks>
    public interface IOverlayHeader
    {
        /// <summary>Drawn to the left of the close button. Null leaves the row to the button.</summary>
        Control? OverlayHeader { get; }
    }

    /// <summary>Content that may refuse to be closed.</summary>
    /// <remarks>Asked before the X or Escape takes the dialog down, and only then — a dialog closing
    /// itself has already decided. Settings is the one that needs it: it holds database changes that
    /// are applied rather than saved as you type, and closing with those pending is a question, not an
    /// action.</remarks>
    public interface IConfirmsClose
    {
        Task<bool> CanCloseAsync();
    }

    /// <summary>Content that handles the dictation shortcut itself.</summary>
    /// <remarks>Implemented by the speech wizard alone: its last step teaches the shortcut by having
    /// the user press it, so the window-level handler must not take the keystroke first. See
    /// <see cref="Services.ModalScope.ShortcutIsSpokenFor"/> for what went wrong without it.</remarks>
    public interface IOwnsDictationShortcut;

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
    public Task<T?> ShowAsync<T>(Control content, double width, double? height = null) =>
        ShowAsync<T>(content, OverlaySize.Fixed(width, height));

    /// <summary>Draws <paramref name="content"/> as a modal card and completes when it closes.</summary>
    public Task<T?> ShowAsync<T>(Control content, OverlaySize size)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new OverlayEntry(content, tcs, size);

        Children.Add(entry);
        IsVisible = true;
        // The same claim the settings dialog makes — see ModalSurface. It was the host's own private
        // arrangement, which is exactly why Settings, drawn by hand in MainWindow, had none. Taking
        // the surface already pulls focus in through entry.TakeFocus, which asks the content for
        // IFocusOnOpen itself — a second call here posted the same FocusOnOpen twice per open.
        entry.Modal = ModalSurface.Take(entry, entry.TakeFocus,
            ownsDictationShortcut: content is IOwnsDictationShortcut);

        return tcs.Task.ContinueWith(
            t => t.Result is T value ? value : default,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Closes a dialog the way the user asks for it — after letting it object.</summary>
    /// <remarks>The X and Escape both come through here; <see cref="CloseWith"/> is what a dialog
    /// calls when it has decided for itself, and does not ask. Keeping the two apart is what lets
    /// Settings put its unsaved-changes question in front of the X without every other dialog
    /// growing a hook it does not use.</remarks>
    internal static async void RequestClose(Control content)
    {
        try
        {
            if (content is IConfirmsClose asks && !await asks.CanCloseAsync())
                return;

            CloseWith(content, null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Closing a dialog failed: {ex.Message}");
        }
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
        }

        entry.Tcs.TrySetResult(result);
    }

    /// <summary>One open dialog: its scrim, its card, and the answer it owes its caller.</summary>
    private sealed class OverlayEntry : Panel
    {
        public TaskCompletionSource<object?> Tcs { get; }

        /// <summary>Held for as long as this dialog is on screen — see <see cref="ModalScope"/>.</summary>
        public IDisposable? Modal { get; set; }

        private readonly Border _card;
        private readonly OverlaySize _size;
        private readonly Control _content;

        public OverlayEntry(Control content, TaskCompletionSource<object?> tcs, OverlaySize size)
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
            close.Click += (_, _) => RequestClose(content);

            // One header row: the content's own on the left where it has one, the close button always
            // on the right. A dialog that draws a title of its own keeps drawing it below; this is for
            // what has to sit *beside* the button, which so far is Settings' tabs.
            var header = new DockPanel();
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
            if (content is IOverlayHeader { OverlayHeader: { } own })
                header.Children.Add(own);

            var layout = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);
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
                Width = size.Width ?? double.NaN,
                Height = size.Height ?? double.NaN,
                MinWidth = size.MinWidth,
                MinHeight = size.MinHeight,
                Child = layout,
            };
            _card = card;
            _size = size;

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
                RequestClose(_content);
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

            ModalSurface.FocusInto(_card);
        }

        /// <summary>Gives the modality back when this dialog leaves the tree by any route.</summary>
        /// <remarks>
        /// Closing an overlay removes it from the host, which releases the claim — but a window taken
        /// down with a dialog still on it never goes through that path, and the claim would outlive
        /// everything it was modal to. Harmless in an application that is exiting, and not harmless
        /// at all in a test run, where <c>ModalScope</c> is process-wide: one leaked claim there left
        /// every later test believing something was being asked of the user.
        /// </remarks>
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);

            Modal?.Dispose();
            Modal = null;
            Tcs.TrySetResult(null);
        }

        /// <summary>Keeps the card inside the window it is drawn in.</summary>
        /// <remarks>A margin either side, so the card is visibly laid on the window rather than
        /// filling it — the same reason every tile has a gutter.</remarks>
        protected override Size ArrangeOverride(Size finalSize)
        {
            // A share of the window rather than a fixed size, for a dialog that is a page rather than
            // a question - Settings asks for half the width and four fifths of the height, which is
            // what its own card worked out for itself before the host drew it.
            if (_size.WidthFraction > 0)
                _card.Width = Math.Max(_size.MinWidth, finalSize.Width * _size.WidthFraction);
            if (_size.HeightFraction > 0)
                _card.Height = Math.Max(_size.MinHeight, finalSize.Height * _size.HeightFraction);

            _card.MaxWidth = Math.Max(1, finalSize.Width - 48);
            _card.MaxHeight = Math.Max(1, finalSize.Height - 48);
            return base.ArrangeOverride(finalSize);
        }
    }
}
