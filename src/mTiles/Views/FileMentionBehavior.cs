using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>
/// Gives a <see cref="TextBox"/> the <c>@</c> file suggestions.
/// </summary>
/// <remarks>
/// An attached property rather than a control, because the boxes it is wanted on are three different
/// shapes in three different places — the composer, the plan box and an answer box inside a data
/// template — and replacing each with a subclass would have made the markup say which control it is
/// instead of what the box does.
/// </remarks>
public static class FileMentionBehavior
{
    /// <summary>The suggestions this box offers, or null for a box that offers none.</summary>
    public static readonly AttachedProperty<FileMentionsViewModel?> MentionsProperty =
        AvaloniaProperty.RegisterAttached<TextBox, FileMentionsViewModel?>(
            "Mentions", typeof(FileMentionBehavior));

    /// <summary>What is currently wired to the box, so it can be unwired again.</summary>
    private static readonly AttachedProperty<MentionPopup?> PopupProperty =
        AvaloniaProperty.RegisterAttached<TextBox, MentionPopup?>("Popup", typeof(FileMentionBehavior));

    static FileMentionBehavior() =>
        MentionsProperty.Changed.AddClassHandler<TextBox, FileMentionsViewModel?>(OnMentionsChanged);

    public static void SetMentions(TextBox box, FileMentionsViewModel? value) =>
        box.SetValue(MentionsProperty, value);

    public static FileMentionsViewModel? GetMentions(TextBox box) => box.GetValue(MentionsProperty);

    /// <summary>The list this box is offering, or null when it offers none.</summary>
    /// <remarks>
    /// For tests, and for the one question they could not otherwise ask: the popup lives in a top level
    /// of its own and its rows are the only place the pick is visible, so which row is lit is not
    /// reachable from either the box or the view model.
    /// </remarks>
    internal static ListBox? GetSuggestionList(TextBox box) => box.GetValue(PopupProperty)?.List;


    private static void OnMentionsChanged(
        TextBox box, AvaloniaPropertyChangedEventArgs<FileMentionsViewModel?> e)
    {
        box.GetValue(PopupProperty)?.Detach();

        // The old wiring goes whether or not there is new wiring to put in its place: a template is
        // applied more than once and the binding re-evaluates with it, and two popups on one box answer
        // every keystroke twice while only one of them is ever put away.
        box.SetValue(PopupProperty,
            e.NewValue.GetValueOrDefault() is { } mentions ? new MentionPopup(box, mentions) : null);
    }

    /// <summary>
    /// One box's popup: what it shows, where it sits, and which keys belong to it while it is up.
    /// </summary>
    private sealed class MentionPopup
    {
        /// <summary>How tall the list may get before it scrolls. Taller than this and it covers the
        /// sentence being written, which is the one thing the user is looking at.</summary>
        private const double MaxListHeight = 220;

        private readonly TextBox _box;
        private readonly FileMentionsViewModel _mentions;
        private readonly Popup _popup;
        private readonly ListBox _list;
        private bool _wired;

        internal ListBox List => _list;

        internal MentionPopup(TextBox box, FileMentionsViewModel mentions)
        {
            _box = box;
            _mentions = mentions;

            _list = new ListBox
            {
                // The rows' own colours live in Styles/Controls.axaml under this class. Left unstyled a
                // ListBox paints selected and hover from Fluent's palette, which ThemeBridge does not
                // derive — so the one row this feature is judged on, the one Enter takes, was the only
                // thing here not following the terminal's theme.
                Classes = { "file-mentions" },

                // Not focusable, so opening the list does not take the keyboard away from the box being
                // typed in — which would close the popup exactly as the user reached for it.
                Focusable = false,
                ItemsSource = mentions.Suggestions,
                MaxHeight = MaxListHeight,
                Background = null,
                BorderThickness = default,
                ItemTemplate = RowTemplate,
            };
            _list.AddHandler(InputElement.PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel);

            _popup = new Popup
            {
                PlacementTarget = box,
                // Above the box: every one of these sits at the bottom of the tile, where a list hung
                // underneath has nowhere to go.
                Placement = PlacementMode.TopEdgeAlignedLeft,
                // The box keeps the keyboard while the list is up, so there is no dismiss to be light
                // about. Escape and losing focus are what close it, and both are handled here.
                IsLightDismissEnabled = false,
                Child = Card(_list),
            };
            // The box's own lifetime events, which are the only two subscriptions that outlive the box:
            // they are on the box, so they die with it. Everything else is put up and taken down by them.
            box.AttachedToVisualTree += OnBoxAttached;
            box.DetachedFromVisualTree += OnBoxDetached;

            if (TopLevel.GetTopLevel(box) is not null) Wire();
        }

        /// <summary>Undoes the wiring for good, box events included.</summary>
        internal void Detach()
        {
            _box.AttachedToVisualTree -= OnBoxAttached;
            _box.DetachedFromVisualTree -= OnBoxDetached;
            Unwire();
        }

        private void OnBoxAttached(object? sender, VisualTreeAttachmentEventArgs e) => Wire();

        /// <summary>
        /// A box that has left the screen stops listening to the view model.
        /// </summary>
        /// <remarks>
        /// The answer boxes live in the question list's data template, so every round of questions
        /// builds new ones and drops the old. The view model outlives all of them — it belongs to the
        /// tile — so a popup left subscribed to it keeps a dead box, list and popup alive and does
        /// layout work in them on every refresh, once more for every row ever shown. Reattaching is
        /// what a container being recycled looks like, so the wiring goes back up rather than being
        /// gone for good.
        /// </remarks>
        private void OnBoxDetached(object? sender, VisualTreeAttachmentEventArgs e) => Unwire();

        private void Wire()
        {
            if (_wired) return;
            _wired = true;

            ((ISetLogicalParent)_popup).SetParent(_box);

            _box.PropertyChanged += OnBoxPropertyChanged;
            _box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            _box.GotFocus += OnGotFocus;
            _box.LostFocus += OnLostFocus;
            _mentions.PropertyChanged += OnMentionsPropertyChanged;
            _mentions.Suggestions.CollectionChanged += OnSuggestionsChanged;

            ShowSelection();
            SyncVisibility();
        }

        private void Unwire()
        {
            if (!_wired) return;
            _wired = false;

            _box.PropertyChanged -= OnBoxPropertyChanged;
            _box.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            _box.GotFocus -= OnGotFocus;
            _box.LostFocus -= OnLostFocus;
            _mentions.PropertyChanged -= OnMentionsPropertyChanged;
            _mentions.Suggestions.CollectionChanged -= OnSuggestionsChanged;

            _popup.IsOpen = false;
            ((ISetLogicalParent)_popup).SetParent(null);
        }

        /// <summary>What the user types, and where the caret ends up, are the same question asked twice.</summary>
        private void OnBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TextBox.TextProperty || e.Property == TextBox.CaretIndexProperty)
                Refresh();
        }

        /// <summary>
        /// Asks for the suggestions that fit what is in the box now.
        /// </summary>
        /// <remarks>
        /// Only for the box the user is typing in. The composer and the plan box are bound to the same
        /// property, so a keystroke in one is a text change in both, and without this the box that is
        /// not on screen would answer for the one that is.
        /// </remarks>
        private void Refresh()
        {
            if (!_box.IsFocused) return;

            _ = _mentions.UpdateAsync(_box.Text, _box.CaretIndex);
        }

        private void OnLostFocus(object? sender, RoutedEventArgs e) => _mentions.Close();

        /// <summary>The box has the keyboard again, so the list may belong on screen again.</summary>
        private void OnGotFocus(object? sender, RoutedEventArgs e) => SyncVisibility();

        /// <summary>
        /// Puts the popup where the view model and the focus say it should be.
        /// </summary>
        /// <remarks>
        /// <para><b>Asked, not only announced.</b> The popup used to learn its visibility from a change
        /// notification alone, and <c>IsOpen</c> is a bool: a view model that is already open sets true
        /// over true and raises nothing. So any wiring that began life disagreeing with it never heard
        /// otherwise, and the list stayed down for the rest of the tile's life.</para>
        /// <para>Both moments are needed and neither is enough. <see cref="Wire"/> covers a box that is
        /// already focused when it is wired; focus arriving <em>after</em> the wiring — which is the
        /// ordinary order when a box goes off the visual tree and comes back — is what
        /// <see cref="OnGotFocus"/> covers. <c>Unwire</c> deliberately does not close the view model:
        /// it is shared between this tile's boxes, and closing it would disown a reading another box is
        /// waiting on.</para>
        /// </remarks>
        private void SyncVisibility() => _popup.IsOpen = _mentions.IsOpen && _box.IsFocused;

        private void OnMentionsPropertyChanged(
            object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(FileMentionsViewModel.IsOpen):
                    SyncVisibility();
                    break;
                case nameof(FileMentionsViewModel.SelectedIndex):
                    ShowSelection();
                    break;
            }
        }

        /// <summary>
        /// The rows change under a selection that did not, so the highlight is put back after every one
        /// of them.
        /// </summary>
        /// <remarks>
        /// A keystroke refills the list while the pick stays at the top row, so the view model announces
        /// nothing and the list — which drops its own selection the moment its items go — would be left
        /// with no row lit and Enter still taking one.
        /// </remarks>
        private void OnSuggestionsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
            ShowSelection();

        /// <summary>
        /// Lights the row Enter would take.
        /// </summary>
        /// <remarks>
        /// The list is told rather than bound: it is not focusable and its rows are marked handled in
        /// the tunnel phase — the caret belongs to the box — so nothing it does on its own ever selects
        /// anything, and without this the arrows moved a pick the user could not see.
        /// </remarks>
        private void ShowSelection()
        {
            _list.SelectedIndex = _mentions.SelectedIndex;

            if (_mentions.SelectedIndex >= 0) _list.ScrollIntoView(_mentions.SelectedIndex);
        }

        /// <summary>
        /// The keys the list takes while it is up, and only while it is up.
        /// </summary>
        /// <remarks>
        /// Tunnelling, because Enter in every one of these boxes sends: the bubble phase would reach
        /// this after the box's own handler had already sent a goal with a half-typed <c>@go</c> in it.
        /// </remarks>
        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (!_popup.IsOpen) return;

            switch (e.Key)
            {
                case Key.Down:
                    e.Handled = Move(1);
                    break;
                case Key.Up:
                    e.Handled = Move(-1);
                    break;
                case Key.Enter:
                    e.Handled = Apply();
                    break;
                // Tab narrows before it picks, as it does in a shell. Handled either way while the
                // list is up, or Tab would move the focus out of the box the user is completing in.
                case Key.Tab:
                    e.Handled = Apply(commonPrefix: true);
                    break;
                case Key.Escape:
                    _mentions.Close();
                    e.Handled = true;
                    break;
            }
        }

        private bool Move(int delta) => _mentions.MoveSelection(delta);

        /// <summary>
        /// Clicking a row picks it.
        /// </summary>
        /// <remarks>
        /// Marked handled in the tunnel phase so the row never gets as far as focusing itself: the box
        /// has to keep the caret, because the caret is what says where the mention goes.
        /// </remarks>
        private void OnRowPressed(object? sender, PointerPressedEventArgs e)
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)
                is not { DataContext: string path }) return;

            Apply(path);
            e.Handled = true;
        }

        private bool Apply(string? path = null, bool commonPrefix = false)
        {
            var result = commonPrefix
                ? _mentions.CompleteCommonPrefix(_box.Text, _box.CaretIndex)
                : _mentions.Complete(_box.Text, _box.CaretIndex, path);

            if (result is not { } completed) return false;

            _box.Text = completed.Text;
            _box.CaretIndex = completed.CaretIndex;
            return true;
        }

        /// <summary>The card the list sits on, in the language every other floating thing here uses.</summary>
        private static Border Card(Control content)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(2),
                MinWidth = 220,
                MaxWidth = 520,
                Child = content,
            };

            // Bound rather than resolved once: the palette follows the terminal theme, and a colour read
            // at construction is the colour the tile happened to be built in.
            card.Bind(Border.BackgroundProperty, card.GetResourceObservable("BgElevated"));
            card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("BorderSubtle"));
            card.Bind(Border.CornerRadiusProperty, card.GetResourceObservable("RadiusSm"));

            return card;
        }

        /// <summary>
        /// One row: the file's name, and the folder it is in beside it.
        /// </summary>
        /// <remarks>
        /// The name first and the folder after it, muted, because the name is what was typed and the
        /// folder is only what tells two files of that name apart. The whole path in one shade put the
        /// answer at the end of a line that is mostly directories.
        /// </remarks>
        private static readonly IDataTemplate RowTemplate = new FuncDataTemplate<string>((path, _) =>
        {
            // A null is not a path that came out empty, it is the list building a row for nothing:
            // `FuncDataTemplate<T>` matches a null for any reference type, and a `ListBox` asks for a
            // row whenever its items go — which here is every keystroke, because a refill is a Clear
            // followed by the new matches. Left to reach `LastIndexOf` it is a NullReferenceException
            // out of a layout pass, so the tile dies while somebody is typing into it.
            if (path is null) return new TextBlock();

            // A folder ends in the separator, so the last one in it is not the boundary between what
            // this row is and where it lives — the one before it is. Without this every folder row came
            // out with an empty name and its whole path as the muted half beside it.
            var cut = path.EndsWith('/')
                ? path.LastIndexOf('/', Math.Max(path.Length - 2, 0))
                : path.LastIndexOf('/');

            var name = new TextBlock { Text = cut < 0 ? path : path[(cut + 1)..] };
            var folder = new TextBlock
            {
                Text = cut < 0 ? "" : path[..cut],
                Margin = new Thickness(8, 0, 0, 0),
                IsVisible = cut >= 0,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable("TextPrimary"));
            folder.Bind(TextBlock.ForegroundProperty, folder.GetResourceObservable("TextSecondary"));
            folder.Bind(TextBlock.FontSizeProperty, folder.GetResourceObservable("FontXs"));

            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { name, folder },
            };
        });
    }
}
