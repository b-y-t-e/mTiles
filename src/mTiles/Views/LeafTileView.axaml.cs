using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using mTiles.Services.Tiles;
using mTiles.ViewModels;

namespace mTiles.Views;

public partial class LeafTileView : UserControl
{
    private ITile? _currentContentVm;
    private string _originalTileName = "";
    private LeafTileNodeViewModel? _subscribedLeaf;
    private ITile? _subscribedContent;
    private Point? _dragStartPoint;
    private PointerPressedEventArgs? _dragPressedArgs;

    public LeafTileView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddHandler(InputElement.KeyDownEvent, OnTileKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerPressedEvent, OnTilePointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(InputElement.GotFocusEvent, OnTileGotFocus, Avalonia.Interactivity.RoutingStrategies.Bubble);

        TileToolbar.AddHandler(InputElement.PointerPressedEvent, OnToolbarPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        TileToolbar.AddHandler(InputElement.PointerMovedEvent, OnToolbarPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        TileToolbar.AddHandler(InputElement.PointerReleasedEvent, OnToolbarPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        DropOverlay.BorderThickness = new Thickness(2);
        TileToolbar.SizeChanged += (_, e) => ApplyHeaderWidth(e.NewSize.Width);
    }

    /// <summary>Builds the "Run as" list at the moment the overflow menu is drawn.</summary>
    /// <remarks>Instances are added, renamed and deleted in Settings while the tile lives, and the
    /// flyout's own bindings are evaluated once — so a list held from when the tile was built would be
    /// the one that was true then. The menu asking for it is the one event that matters.</remarks>
    private void OnOverflowOpening(object? sender, EventArgs e)
    {
        if (DataContext is not LeafTileNodeViewModel leaf) return;

        leaf.RefreshAgentInstances();
        leaf.RefreshChangeKindOptions();
    }

    /// <summary>Below this, the header stops offering to split the tile.</summary>
    private const double SplitButtonsNeedWidth = 260;

    /// <summary>Below this, restarting and starting a new session are in the menu only.</summary>
    private const double SessionButtonsNeedWidth = 190;

    /// <summary>How wide the tile's name may grow before the note beside it starts being squeezed.
    /// </summary>
    /// <remarks>A named threshold like the two below it, rather than a number in the markup: every
    /// other width the header decides by lives here, and one of them hiding in a XAML attribute is the
    /// one nobody finds when the header stops behaving.</remarks>
    public const double NameMaxWidth = 220;

    /// <summary>Below this, the header says only which tile this is, not what it is running.</summary>
    /// <remarks>Wider than the button thresholds, and deliberately so: the note is the <em>first</em>
    /// thing to give way. A button that stands down is still in the overflow menu; the note has nowhere
    /// else to be shown, but it is also the only thing here the user is not currently trying to click.
    /// Below this width the name has the row to itself, which is the promise that matters.</remarks>
    private const double HeaderNoteNeedsWidth = 320;

    /// <summary>Which of the header's buttons a tile this wide can afford.</summary>
    /// <remarks>
    /// <para>The name is what identifies the tile, and in a <c>DockPanel</c> it is the one thing that
    /// gets whatever the docked buttons leave — which in a narrow column was nothing at all: four tiles
    /// in a stack showed a row of icons each and not one name between them. The buttons give way
    /// instead.</para>
    /// <para>In the order they are least missed. The splits go first: dragging a tile onto another does
    /// the same job, so a split is the one action here with a second route. Restart and New session go
    /// last, and only when there is really no room, because they are among the most pressed things in
    /// the application.</para>
    /// <para>Nothing is lost either way — every one of them is in the overflow as well, which is why
    /// the overflow never stands down. The buttons are the fast path, not the only path.</para>
    /// <para>Driven off the toolbar's own width rather than the tile's: it is the toolbar that runs out
    /// of room, and the two differ by the card's border.</para>
    /// </remarks>
    private void ApplyHeaderWidth(double width)
    {
        // Splitting a tile that is filling the workspace puts a tile beside it that nobody can see, so
        // the command restores the layout first — but offering it here would still read as "split this
        // full-screen view in two", which is not what happens. Width *and* state, in the one writer.
        var roomToSplit = width >= SplitButtonsNeedWidth && _subscribedLeaf?.IsMaximized != true;
        SplitRightButton.IsVisible = roomToSplit;
        SplitDownButton.IsVisible = roomToSplit;

        // Width *and* what the tile is: a Note has no shell to restart. Both conditions in one place,
        // because this is the only thing that writes these two properties — see the markup for what
        // happened when a binding wrote them as well.
        var roomForSession = width >= SessionButtonsNeedWidth;
        RestartButton.IsVisible = roomForSession && _subscribedLeaf?.CanRestart == true;
        NewSessionButton.IsVisible = roomForSession && _subscribedLeaf?.HasSession == true;

        // Width *and* whether there is anything to say, in one place, for the reason the two above are:
        // this method is the single writer of every header visibility, and a binding writing the same
        // property at the same priority is how the Restart button came to depend on whether the tile had
        // been resized or its content changed more recently.
        TileHeaderNote.IsVisible = width >= HeaderNoteNeedsWidth && TileHeaderNote.Text?.Length > 0;

        // No width of its own: whether this tile can fill the workspace is the whole condition. A tile
        // narrow enough for the splits to stand down is the one this button is most wanted on.
        var canMaximize = _subscribedLeaf?.CanMaximize == true;
        MaximizeButton.IsVisible = canMaximize;
        MaximizeMenuItem.IsVisible = canMaximize;
    }

    /// <summary>
    /// Which way the full-screen button will go, said on the button itself.
    /// </summary>
    /// <remarks>The glyph and the tooltip, because the way back out has to be as findable as the way in
    /// — the header of a maximized tile is the only chrome left on screen. The visibility is
    /// <see cref="ApplyHeaderWidth"/>'s, which this calls: one writer for the picture, one for the flag.
    /// </remarks>
    private void UpdateMaximizeButton(LeafTileNodeViewModel leaf)
    {
        var kind = leaf.IsMaximized ? MaterialIconKind.FullscreenExit : MaterialIconKind.Fullscreen;
        MaximizeGlyph.Kind = kind;
        MaximizeMenuGlyph.Kind = kind;

        var label = leaf.IsMaximized ? "Exit full screen (Ctrl+Shift+F)" : "Full screen (Ctrl+Shift+F)";
        ToolTip.SetTip(MaximizeButton, label);
        MaximizeMenuItem.Header = leaf.IsMaximized ? "Exit full screen" : "Full screen";

        // The one button on this header that stays lit rather than only changing shape: it is the only
        // thing on screen saying that the rest of the workspace is still there.
        MaximizeButton.Classes.Set("tile-btn-on", leaf.IsMaximized);

        ApplyHeaderWidth(TileToolbar.Bounds.Width);
    }

    /// <summary>
    /// Fills in what the tile is running, for a kind that has an answer.
    /// </summary>
    /// <remarks>The text is set here and its visibility in <see cref="ApplyHeaderWidth"/>, which is then
    /// called: one writer for the text, one for the flag, and neither guessing what the other did. The
    /// full note is the tooltip whether or not it fits, because trimming it is exactly when the rest of
    /// it is worth having.</remarks>
    private void UpdateHeaderNote(LeafTileNodeViewModel leaf)
    {
        var note = (leaf.Content as IDescribedTile)?.HeaderNote ?? "";

        TileHeaderNote.Text = note;
        ToolTip.SetTip(TileHeaderNote, note.Length > 0 ? note : null);
        ApplyHeaderWidth(TileToolbar.Bounds.Width);
    }

    private void OnTilePointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        (DataContext as LeafTileNodeViewModel)?.Activate();
    }

    private void OnTileGotFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        (DataContext as LeafTileNodeViewModel)?.Activate();
    }

    private void OnTileKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            if (DataContext is LeafTileNodeViewModel { CanMaximize: true } maximizable)
            {
                maximizable.ToggleMaximizeCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.R && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            if (DataContext is LeafTileNodeViewModel { CanRestart: true } leaf)
            {
                leaf.RestartTerminalCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedLeaf != null)
        {
            _subscribedLeaf.PropertyChanged -= OnLeafPropertyChanged;
            _subscribedLeaf.FocusRequested -= FocusContent;
        }

        if (DataContext is LeafTileNodeViewModel leaf)
        {
            _subscribedLeaf = leaf;
            leaf.PropertyChanged += OnLeafPropertyChanged;
            leaf.FocusRequested += FocusContent;
            leaf.ConfirmAction = async message =>
            {
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null) return true;
                var box = MessageBoxManager.GetMessageBoxStandard(
                    "Confirm", message, ButtonEnum.YesNo, Icon.Question);
                var result = await box.ShowWindowDialogAsync(window);
                return result == ButtonResult.Yes;
            };
            UpdateTypeGlyph(leaf);
            UpdateMaximizeButton(leaf);
            UpdateActiveIndicator(leaf);
            UpdateDictationIndicator(leaf);
            UpdateContentDisplay(leaf);
        }
    }

    private void OnLeafPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not LeafTileNodeViewModel leaf) return;

        if (e.PropertyName is nameof(LeafTileNodeViewModel.Content) or nameof(LeafTileNodeViewModel.KindId))
        {
            UpdateTypeGlyph(leaf);
            UpdateContentDisplay(leaf);
            UpdateMaximizeButton(leaf);
        }
        else if (e.PropertyName is nameof(LeafTileNodeViewModel.IsMaximized)
                 or nameof(LeafTileNodeViewModel.CanMaximize))
            UpdateMaximizeButton(leaf);
        // The property the strip is drawn from, not the two it is computed from. Listening to the inputs
        // meant the strip was repainted while one of them had not been updated yet: on the way in the
        // recording flag arrived before IsDictating, so the strip was still lit and showed through the
        // half-transparent border; on the way out IsDictating went false *last*, and since nothing was
        // listening for it the strip never came back at all. Rendering a derived value means subscribing
        // to the derived value.
        else if (e.PropertyName is nameof(LeafTileNodeViewModel.ShowsActiveOutline)
                 or nameof(LeafTileNodeViewModel.IsActive))
            UpdateActiveIndicator(leaf);
        else if (e.PropertyName is nameof(LeafTileNodeViewModel.HasSession)
                 or nameof(LeafTileNodeViewModel.CanRestart))
            ApplyHeaderWidth(TileToolbar.Bounds.Width);
        else if (e.PropertyName is nameof(LeafTileNodeViewModel.IsRecordingDictation)
                 or nameof(LeafTileNodeViewModel.IsTranscribingDictation))
            UpdateDictationIndicator(leaf);
        else if (e.PropertyName == nameof(LeafTileNodeViewModel.IsChoosingSetup))
            UpdateChooserVisibility(leaf);
    }

    /// <summary>
    /// Shows which of the two dictation states this tile is in, or hides the border entirely.
    /// </summary>
    /// <remarks>
    /// The classes are what carry the animation; hiding the border as well is what stops it running at
    /// all when there is nothing to show. An animation on a hidden element is still an animation.
    /// </remarks>
    private void UpdateDictationIndicator(LeafTileNodeViewModel leaf)
    {
        DictationBorder.Classes.Set("recording", leaf.IsRecordingDictation);
        DictationBorder.Classes.Set("processing", leaf.IsTranscribingDictation);
        DictationBorder.IsVisible = leaf.IsRecordingDictation || leaf.IsTranscribingDictation;
    }

    /// <summary>The icon and colour this tile's type wears in its header.</summary>
    /// <remarks>
    /// The colour is bound to the resource rather than resolved to a brush, so a theme switch reaches
    /// it — the same reason <see cref="UpdateActiveIndicator"/> binds instead of assigning. Both of
    /// them then have to be re-run whenever the value they read changes, which is what the two callers
    /// above are.
    /// </remarks>
    private void UpdateTypeGlyph(LeafTileNodeViewModel leaf)
    {
        // The note follows the same two things the glyph does - which kind this is, and which content
        // object is in it - so it is refreshed by the same callers rather than by a third subscription
        // that would have to be kept in step with them.
        UpdateHeaderNote(leaf);

        var kind = leaf.Kind;
        TileTypeGlyph.Kind = kind is null ? TileIcons.Placeholder : TileIcons.Kind(kind.IconId);

        // An empty tile has no kind yet, so it gets the one colour that says nothing about which one it
        // is going to become.
        TileTypeGlyph.Bind(MaterialIcon.ForegroundProperty,
            TileTypeGlyph.GetResourceObservable(kind?.AccentKey ?? "TextFaint"));
    }

    /// <summary>
    /// The active markers: the card's outline and the toolbar's lift.
    /// </summary>
    /// <remarks>
    /// The outline follows <see cref="LeafTileNodeViewModel.ShowsActiveOutline"/> rather than
    /// <c>IsActive</c>, so it goes back to the ordinary card edge while this tile is being dictated
    /// into — the dictation border frames the same rectangle and says the same thing more loudly. The
    /// toolbar keeps its lift throughout: it is the quiet half of the signal, it is not at the tile's
    /// edge, and flickering it as the microphone opens and closes would be a change of background under
    /// the buttons the user is about to click.
    /// </remarks>
    private void UpdateActiveIndicator(LeafTileNodeViewModel leaf)
    {
        TileCard.Bind(Border.BorderBrushProperty,
            TileCard.GetResourceObservable(leaf.ShowsActiveOutline ? "AccentOutline" : "BorderSubtle"));
        TileToolbar.Bind(Border.BackgroundProperty,
            TileToolbar.GetResourceObservable(leaf.IsActive ? "BgElevated" : "BgSurface"));

        // The other half of the same signal, and the half that survives a colour-blind eye and a
        // washed-out screen: an inactive tile's header recedes rather than only changing shade. On the
        // header alone — the content of an inactive tile is still being read, and dimming a running
        // terminal because the focus is elsewhere would make every split worse than no split.
        TileHeaderContent.Opacity = leaf.IsActive ? 1.0 : 0.55;
    }

    private void UpdateContentDisplay(LeafTileNodeViewModel leaf)
    {
        if (leaf.KindId.Length == 0)
        {
            PopulateKindButtons(leaf);
            ContentHost.Children.Clear();
            _currentContentVm = null;
        }
        else
        {
            SetContent(leaf.Content);
        }

        UpdateChooserVisibility(leaf);
    }

    /// <summary>Which of the three things a tile can be showing is on screen.</summary>
    /// <remarks>A kind's own step is no longer only what an empty tile draws: a tile being changed into
    /// another kind picks the new kind's shell or instance <em>before</em> anything is destroyed, so the
    /// step is drawn over content that is still running. Which is why the content host stands down on
    /// the step rather than on the tile having no kind.</remarks>
    private void UpdateChooserVisibility(LeafTileNodeViewModel leaf)
    {
        var empty = leaf.KindId.Length == 0;

        // The scroller is what is shown and hidden, not the panel inside it: a visible scroller wrapped
        // round a collapsed panel is still a hit-testable sheet lying over the tile's content.
        ContentChooserScroll.IsVisible = empty && !leaf.IsChoosingSetup;
        SetupChooserScroll.IsVisible = leaf.IsChoosingSetup;
        ContentHost.IsVisible = !empty && !leaf.IsChoosingSetup;

        if (leaf.IsChoosingSetup)
            PopulateSetupButtons(leaf);
    }

    /// <summary>One card per registered kind.</summary>
    /// <remarks>Built from the catalog rather than written out in the markup, so a kind added later
    /// appears here by being registered — which is the whole point of the registry. The cards are
    /// rebuilt rather than kept, because an empty tile is shown at most once before it becomes
    /// something and the list is six buttons long.</remarks>
    private void PopulateKindButtons(LeafTileNodeViewModel leaf)
    {
        ContentChooser.Children.Clear();

        foreach (var kind in leaf.AvailableKinds)
        {
            var button = new Button
            {
                Classes = { "chooser-card" },
                Command = leaf.SelectKindCommand,
                CommandParameter = kind.Id,
                Content = ChooserCardContent(TileIcons.Kind(kind.IconId), kind.DisplayName, kind.AccentKey),
            };
            ContentChooser.Children.Add(button);
        }
    }

    /// <summary>A glyph over a label, which is what every card in both choosers is.</summary>
    private static StackPanel ChooserCardContent(MaterialIconKind glyph, string label, string colourKey)
    {
        var icon = new MaterialIcon
        {
            Kind = glyph, Width = 22, Height = 22,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        icon.Bind(MaterialIcon.ForegroundProperty, icon.GetResourceObservable(colourKey));

        var text = new TextBlock
        {
            Text = label,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        text.Bind(TextBlock.FontSizeProperty, text.GetResourceObservable("FontSm"));

        return new StackPanel
        {
            Spacing = 7,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Children = { icon, text }
        };
    }

    /// <summary>One card per option the kind being set up offers, after the Back card.</summary>
    /// <remarks>The same loop as the kind cards, over a list the kind describes: what the options are
    /// and what they mean is <see cref="ITileKind.SetupOptions"/>'s business, and this file knows only
    /// that each is a glyph over a label.</remarks>
    private void PopulateSetupButtons(LeafTileNodeViewModel leaf)
    {
        var backIndex = SetupChooser.Children.IndexOf(SetupBackButton);
        if (backIndex < 0) return;
        while (SetupChooser.Children.Count > backIndex + 1)
            SetupChooser.Children.RemoveAt(SetupChooser.Children.Count - 1);

        foreach (var option in leaf.SetupOptions)
        {
            // No accent rail: the glyph carries the colour, here as on the kind cards beside them.
            SetupChooser.Children.Add(new Button
            {
                Classes = { "chooser-card" },
                Command = leaf.SelectSetupOptionCommand,
                CommandParameter = option,
                Content = ChooserCardContent(
                    TileIcons.Kind(option.IconId), option.Label, option.AccentKey),
            });
        }
    }

    /// <summary>Puts the control that draws this content into the tile.</summary>
    /// <remarks>Resolved through the catalog by kind id, <b>never by switching on the view model's
    /// type</b>: a dictionary lookup is simpler than a six-armed switch, it does not care whether two
    /// kinds ever share a view model class, and a kind registered by code this file has never heard of
    /// draws itself without this file changing.</remarks>
    private void SetContent(ITile? content)
    {
        if (ReferenceEquals(content, _currentContentVm) && ContentHost.Children.Count > 0)
            return;

        _currentContentVm = content;

        ContentHost.Children.Clear();

        if (_subscribedContent != null)
            _subscribedContent.PropertyChanged -= OnContentPropertyChanged;
        _subscribedContent = content;
        if (_subscribedContent != null)
            _subscribedContent.PropertyChanged += OnContentPropertyChanged;

        if (content == null)
            return;

        if (_subscribedLeaf?.Catalog?.Entry(content.KindId) is not { } entry)
        {
            // The kind was resolved when the tile was built, so getting here means the catalog changed
            // underneath a live tile. Nothing to draw is better than an exception on the UI thread.
            Trace.TraceWarning("No view is registered for tile kind '{0}'.", content.KindId);
            return;
        }

        UpdateContentBackground(content);
        ContentHost.Children.Add(entry.CreateView(content));
    }

    private void OnContentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ICustomBackgroundTile.ContentBackground))
            UpdateContentBackground(sender as ITile);

        // A tile whose note has changed under it - an agent relaunched on an instance edited in
        // Settings, or a model the launch has only just resolved - says so through its own change
        // notification, which is what ITile's INotifyPropertyChanged is here for. Without this the
        // header would keep showing the model of a session that has already been replaced.
        if (e.PropertyName == nameof(IDescribedTile.HeaderNote)
            && DataContext is LeafTileNodeViewModel leaf)
            UpdateHeaderNote(leaf);
    }

    /// <summary>
    /// How far a tile's content sits inside its card, and what that gap is painted in.
    /// </summary>
    /// <remarks>
    /// <para>Asked of the content rather than decided from its type: a terminal is text against an edge
    /// and wants the gap, and every other tile's content is its own chrome — bars, lists, a composer —
    /// which drawn inside an inset leaves a square-cornered rectangle floating in a rounded card, with a
    /// sliver of card colour showing round the bottom corners where the two shapes disagree. Those tiles
    /// run to the card's edge instead and take its corners from the clip.</para>
    /// <para>No inset at the top either way: the header is already there.</para>
    /// </remarks>
    private void UpdateContentBackground(ITile? content)
    {
        if (content is ICustomBackgroundTile custom)
        {
            ContentHost.Margin = custom.ContentInset;
            ContentHost.Background = new SolidColorBrush(Color.Parse(custom.ContentBackground));
            return;
        }

        ContentHost.Margin = default;
        ContentHost.Bind(Panel.BackgroundProperty, ContentHost.GetResourceObservable("BgBase"));
    }

    private void TileNameLabel_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is LeafTileNodeViewModel leaf)
            _originalTileName = leaf.TileName;

        TileNameLabel.IsVisible = false;
        TileNameEditor.IsVisible = true;
        TileNameEditor.Focus();
        TileNameEditor.SelectAll();
    }

    private void TileNameEditor_Confirm(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CommitRename();
    }

    private void TileNameEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (DataContext is LeafTileNodeViewModel leaf)
                leaf.TileName = _originalTileName;
            TileNameEditor.IsVisible = false;
            TileNameLabel.IsVisible = true;
            e.Handled = true;
        }
    }

    private void CommitRename()
    {
        if (!TileNameEditor.IsVisible) return;

        if (DataContext is LeafTileNodeViewModel leaf)
        {
            if (string.IsNullOrWhiteSpace(leaf.TileName))
                leaf.TileName = _originalTileName;

            if (leaf.TileName != _originalTileName)
                (leaf.Content as IFileContent)?.RenameFile(leaf.TileName);
        }

        TileNameEditor.IsVisible = false;
        TileNameLabel.IsVisible = true;
    }

    // Suppress activation during Focus() to prevent GotFocus → Activate → FocusContent ping-pong
    private void FocusContent()
    {
        if (_subscribedLeaf == null) return;

        // Terminal najpierw i wprost: kontrolka terminala sama czyta klawiaturę (nie ma
        // template'u ani wewnętrznego ScrollBara), więc fokus siada deterministycznie.
        // Dla Note/Git/Todo fallback na pierwszy focusable.
        InputElement? focusable = ContentHost.GetVisualDescendants()
            .OfType<Terminal.Avalonia.TerminalControl>()
            .FirstOrDefault();
        focusable ??= ContentHost.GetVisualDescendants()
            .OfType<InputElement>()
            .FirstOrDefault(e => e.Focusable);
        if (focusable == null) return;

        using (_subscribedLeaf.ActivationScope.SuppressActivation())
            focusable.Focus();

        // Przy zmianie workspace/terminala widok bywa jeszcze nierozłożony w chwili
        // pierwszej próby (post na Input jest za wcześnie) → fokus nie siada. Jedna
        // ponowna próba po layoucie (Loaded). Twardy limit = 1 (bez pętli).
        // Guard IsActive: nie kradnij fokusu, jeśli użytkownik w międzyczasie
        // uaktywnił inny kafel.
        Dispatcher.UIThread.Post(() =>
        {
            if (_subscribedLeaf is not { IsActive: true }) return;
            if (focusable.IsFocused) return;
            using (_subscribedLeaf.ActivationScope.SuppressActivation())
                focusable.Focus();
        }, DispatcherPriority.Loaded);
    }

    #region Drag & Drop

    private void OnToolbarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (IsInsideButton(e.Source as Control)) return;
        if (TileNameEditor.IsVisible) return;
        _dragStartPoint = e.GetPosition(this);
        _dragPressedArgs = e;
    }

    private async void OnToolbarPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStartPoint == null || _dragPressedArgs == null) return;

        var pos = e.GetPosition(this);
        var delta = pos - _dragStartPoint.Value;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;

        var pressedArgs = _dragPressedArgs;
        _dragStartPoint = null;
        _dragPressedArgs = null;

        if (DataContext is not LeafTileNodeViewModel leaf) return;

        TileDragDrop.DragSource = leaf;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(TileDragDrop.DataFormat));

        Opacity = 0.4;
        try
        {
            await DragDrop.DoDragDropAsync(pressedArgs, data, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("DragDrop failed: {0}", ex.Message);
        }
        finally
        {
            Opacity = 1.0;
            TileDragDrop.DragSource = null;
        }
    }

    private void OnToolbarPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragStartPoint = null;
        _dragPressedArgs = null;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var source = TileDragDrop.DragSource;
        if (source == null || source == DataContext)
        {
            e.DragEffects = DragDropEffects.None;
            HideDropOverlay();
            return;
        }

        var pos = e.GetPosition(this);
        var zone = TileDragDrop.GetDropZone(pos, Bounds.Size);
        ShowDropOverlay(zone);
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        HideDropOverlay();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        HideDropOverlay();

        var source = TileDragDrop.DragSource;
        var target = DataContext as LeafTileNodeViewModel;
        if (source == null || target == null || source == target) return;

        var pos = e.GetPosition(this);
        var zone = TileDragDrop.GetDropZone(pos, Bounds.Size);
        TileDragDrop.Execute(source, target, zone);
        e.Handled = true;
    }

    private void ShowDropOverlay(DropZone zone)
    {
        if (zone == DropZone.None) { HideDropOverlay(); return; }

        var accent = this.FindResource("AccentHover") as ISolidColorBrush;
        var accentColor = accent?.Color ?? Color.FromRgb(0x3a, 0x6f, 0xa0);
        var fillBrush = new SolidColorBrush(Color.FromArgb(55, accentColor.R, accentColor.G, accentColor.B));
        var borderBrush = new SolidColorBrush(Color.FromArgb(140, accentColor.R, accentColor.G, accentColor.B));

        var w = Bounds.Width;
        var h = Bounds.Height;

        if (zone == DropZone.Center)
        {
            DropOverlay.Background = Brushes.Transparent;
            DropOverlay.BorderBrush = borderBrush;
            DropOverlay.BorderThickness = new Thickness(3);
            DropOverlay.Margin = new Thickness(3);
        }
        else
        {
            DropOverlay.Background = fillBrush;
            DropOverlay.BorderBrush = borderBrush;
            DropOverlay.BorderThickness = new Thickness(2);
            DropOverlay.Margin = zone switch
            {
                DropZone.Left   => new Thickness(2, 2, w * 0.70, 2),
                DropZone.Right  => new Thickness(w * 0.70, 2, 2, 2),
                DropZone.Top    => new Thickness(2, 2, 2, h * 0.70),
                DropZone.Bottom => new Thickness(2, h * 0.70, 2, 2),
                _ => default
            };
        }
        DropOverlay.IsVisible = true;
    }

    private void HideDropOverlay()
    {
        DropOverlay.IsVisible = false;
    }

    private static bool IsInsideButton(Control? control)
    {
        while (control != null)
        {
            if (control is Button) return true;
            control = control.Parent as Control;
        }
        return false;
    }

    #endregion
}
