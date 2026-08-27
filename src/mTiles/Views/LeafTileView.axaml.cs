using System.ComponentModel;
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
using mTiles.Models;
using mTiles.ViewModels;

namespace mTiles.Views;

public partial class LeafTileView : UserControl
{
    private object? _currentContentVm;
    private string _originalTileName = "";
    private LeafTileNodeViewModel? _subscribedLeaf;
    private INotifyPropertyChanged? _subscribedContent;
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

    /// <summary>
    /// Below this, the header stops offering to split the tile.
    /// </summary>
    /// <remarks>
    /// Measured against what the header holds: the type glyph, five buttons at 24px, and enough left
    /// for a name to be worth reading. A tile narrower than this is one of a row of four in a column,
    /// and splitting it again is not the thing its user is about to do.
    /// </remarks>
    private const double SplitButtonsNeedWidth = 190;

    /// <summary>Which of the header's buttons a tile this wide can afford.</summary>
    /// <remarks>
    /// <para>The name is what identifies the tile, and in a <c>DockPanel</c> it is the one thing that
    /// gets whatever the docked buttons leave — which in a narrow column was nothing at all: four tiles
    /// in a stack showed a row of icons each and not one name between them. The buttons give way
    /// instead, starting with the two that split, because closing and the overflow have no other route
    /// while a split is also a drag away.</para>
    /// <para>Driven off the toolbar's own width rather than the tile's: it is the toolbar that runs out
    /// of room, and the two differ by the card's border.</para>
    /// </remarks>
    private void ApplyHeaderWidth(double width)
    {
        var roomToSplit = width >= SplitButtonsNeedWidth;
        SplitRightButton.IsVisible = roomToSplit;
        SplitDownButton.IsVisible = roomToSplit;
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
        if (e.Key == Key.R && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            if (DataContext is LeafTileNodeViewModel { ContentType: TileContentType.Terminal } leaf)
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
            UpdateActiveIndicator(leaf);
            UpdateDictationIndicator(leaf);
            UpdateContentDisplay(leaf);
        }
    }

    private void OnLeafPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not LeafTileNodeViewModel leaf) return;

        if (e.PropertyName is nameof(LeafTileNodeViewModel.Content) or nameof(LeafTileNodeViewModel.ContentType))
        {
            UpdateTypeGlyph(leaf);
            UpdateContentDisplay(leaf);
        }
        // The property the strip is drawn from, not the two it is computed from. Listening to the inputs
        // meant the strip was repainted while one of them had not been updated yet: on the way in the
        // recording flag arrived before IsDictating, so the strip was still lit and showed through the
        // half-transparent border; on the way out IsDictating went false *last*, and since nothing was
        // listening for it the strip never came back at all. Rendering a derived value means subscribing
        // to the derived value.
        else if (e.PropertyName is nameof(LeafTileNodeViewModel.ShowsActiveOutline)
                 or nameof(LeafTileNodeViewModel.IsActive))
            UpdateActiveIndicator(leaf);
        else if (e.PropertyName is nameof(LeafTileNodeViewModel.IsRecordingDictation)
                 or nameof(LeafTileNodeViewModel.IsTranscribingDictation))
            UpdateDictationIndicator(leaf);
        else if (e.PropertyName == nameof(LeafTileNodeViewModel.IsChoosingProfile))
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
        TileTypeGlyph.Kind = TileTypeIcon.Kind(leaf.ContentType);
        TileTypeGlyph.Bind(MaterialIcon.ForegroundProperty,
            TileTypeGlyph.GetResourceObservable(TileTypeIcon.AccentKey(leaf.ContentType)));
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
        if (leaf.ContentType == TileContentType.Empty)
        {
            ContentChooser.IsVisible = !leaf.IsChoosingProfile;
            ProfileChooser.IsVisible = leaf.IsChoosingProfile;
            if (leaf.IsChoosingProfile)
                PopulateProfileButtons(leaf);
            ContentHost.IsVisible = false;
            ContentHost.Children.Clear();
            _currentContentVm = null;
        }
        else
        {
            ContentChooser.IsVisible = false;
            ProfileChooser.IsVisible = false;
            ContentHost.IsVisible = true;
            SetContent(leaf.Content);
        }
    }

    private void UpdateChooserVisibility(LeafTileNodeViewModel leaf)
    {
        if (leaf.ContentType != TileContentType.Empty) return;
        ContentChooser.IsVisible = !leaf.IsChoosingProfile;
        ProfileChooser.IsVisible = leaf.IsChoosingProfile;
        if (leaf.IsChoosingProfile)
            PopulateProfileButtons(leaf);
    }

    private void PopulateProfileButtons(LeafTileNodeViewModel leaf)
    {
        var markerIndex = ProfileChooser.Children.IndexOf(ProfileButtonsMarker);
        if (markerIndex < 0) return;
        while (ProfileChooser.Children.Count > markerIndex + 1)
            ProfileChooser.Children.RemoveAt(ProfileChooser.Children.Count - 1);

        var profiles = leaf.AvailableProfiles;
        if (profiles == null) return;

        foreach (var profile in profiles)
        {
            var icon = new MaterialIcon
            {
                Kind = MaterialIconKind.ScriptOutline, Width = 22, Height = 22,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };
            icon.Bind(MaterialIcon.ForegroundProperty, icon.GetResourceObservable("TextMuted"));

            var label = new TextBlock
            {
                Text = profile.Name,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };
            label.Bind(TextBlock.FontSizeProperty, label.GetResourceObservable("FontSm"));

            var accent = new Border
            {
                Width = 3, CornerRadius = new CornerRadius(2),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                Margin = new Thickness(-16, 0, 0, 0),
            };
            accent.Bind(Border.BackgroundProperty, accent.GetResourceObservable("TileAccentTerminal"));

            var btn = new Button { Classes = { "chooser-card" } };
            btn.Command = leaf.SelectProfileCommand;
            btn.CommandParameter = profile;
            btn.Content = new Grid
            {
                Children =
                {
                    accent,
                    new StackPanel
                    {
                        Spacing = 4,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        Children = { icon, label }
                    }
                }
            };
            ProfileChooser.Children.Add(btn);
        }
    }

    private void SetContent(object? contentVm)
    {
        if (contentVm == _currentContentVm && ContentHost.Children.Count > 0)
            return;

        _currentContentVm = contentVm;

        ContentHost.Children.Clear();

        if (contentVm == null)
            return;

        UserControl view = contentVm switch
        {
            TerminalTileViewModel => new TerminalTileView { DataContext = contentVm },
            NoteTileViewModel => new NoteTileView { DataContext = contentVm },
            TodoTileViewModel => new TodoTileView { DataContext = contentVm },
            GitTileViewModel => new GitTileView { DataContext = contentVm },
            DatabaseTileViewModel => new DatabaseTileView { DataContext = contentVm },
            GoalTileViewModel => new GoalTileView { DataContext = contentVm },
            _ => throw new InvalidOperationException($"Unknown content type: {contentVm.GetType()}")
        };

        if (_subscribedContent != null)
            _subscribedContent.PropertyChanged -= OnContentPropertyChanged;
        _subscribedContent = contentVm as INotifyPropertyChanged;
        if (_subscribedContent != null)
            _subscribedContent.PropertyChanged += OnContentPropertyChanged;

        UpdateContentBackground(contentVm);
        ContentHost.Children.Add(view);
    }

    private void OnContentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalTileViewModel.Theme))
            UpdateContentBackground(sender);
    }

    /// <summary>
    /// How far a tile's content sits inside its card.
    /// </summary>
    /// <remarks>
    /// <para>A terminal is text against an edge and wants the gap; every other tile's content is its
    /// own chrome — bars, lists, a composer — and drawing that inside an inset leaves a square-cornered
    /// rectangle floating in a rounded card, with a sliver of card colour showing round the bottom
    /// corners where the two shapes disagree. Those tiles run to the card's edge instead, and the
    /// card's <c>ClipToBounds</c> gives them its corners.</para>
    /// <para>No inset at the top either way: the header is already there.</para>
    /// </remarks>
    private static Thickness ContentInset(object? contentVm) =>
        contentVm is TerminalTileViewModel ? new Thickness(6, 0, 6, 6) : default;

    private void UpdateContentBackground(object? contentVm)
    {
        ContentHost.Margin = ContentInset(contentVm);

        if (contentVm is TerminalTileViewModel t)
            ContentHost.Background = new SolidColorBrush(Color.Parse(t.Theme.Background));
        else
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
