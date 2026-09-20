using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Data;
using global::Avalonia.Controls.Primitives;
using global::Avalonia.Input;
using global::Avalonia.Layout;
using global::Avalonia.Input.Platform;
using global::Avalonia.Media;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Threading;
using global::Avalonia.VisualTree;
using Notepad.Avalonia.Model;

namespace Notepad.Avalonia.Controls;

public enum ImageDisplayMode { Inline, Block }

public enum EditorTheme { None, Light, Dark }

/// <summary>
/// Single-note markdown editor control with a built-in vertical scrollbar.
/// </summary>
/// <remarks>
/// The editor scrolls its own content: give it a finite height (e.g. place it in
/// a <c>DockPanel</c> or <c>Grid</c> cell) and it shows its own scrollbar and only
/// renders the visible items (viewport virtualization).
///
/// Do NOT wrap it in an external <c>ScrollViewer</c>. Doing so gives the control an
/// unbounded height, which disables the built-in scrollbar AND virtualization, so
/// every item is rendered on each frame — a performance regression on long notes.
/// </remarks>
public class NoteEditor : Control
{
    public static readonly StyledProperty<EditorTheme> ColorThemeProperty =
        AvaloniaProperty.Register<NoteEditor, EditorTheme>(nameof(ColorTheme), EditorTheme.Light);

    public static readonly StyledProperty<FontFamily> DefaultFontProperty =
        AvaloniaProperty.Register<NoteEditor, FontFamily>(nameof(DefaultFont), FontFamily.Default);

    public static readonly StyledProperty<double> DefaultFontSizeProperty =
        AvaloniaProperty.Register<NoteEditor, double>(nameof(DefaultFontSize), 14.0);

    internal static readonly StyledProperty<IList<NoteItemData>?> ItemsProperty =
        AvaloniaProperty.Register<NoteEditor, IList<NoteItemData>?>(nameof(Items));

    public static readonly StyledProperty<double> InlineImageMaxHeightProperty =
        AvaloniaProperty.Register<NoteEditor, double>(nameof(InlineImageMaxHeight), 100.0);

    public static readonly StyledProperty<ImageDisplayMode> ImageDisplayProperty =
        AvaloniaProperty.Register<NoteEditor, ImageDisplayMode>(
            nameof(ImageDisplay), ImageDisplayMode.Block);

    public static readonly StyledProperty<IBrush> BackgroundBrushProperty =
        AvaloniaProperty.Register<NoteEditor, IBrush>(nameof(BackgroundBrush), Brushes.White);

    public static readonly StyledProperty<IBrush> ForegroundProperty =
        AvaloniaProperty.Register<NoteEditor, IBrush>(nameof(Foreground), Brushes.Black);

    public static readonly StyledProperty<IBrush> SelectionBrushProperty =
        AvaloniaProperty.Register<NoteEditor, IBrush>(nameof(SelectionBrush),
            new SolidColorBrush(Color.FromArgb(80, 30, 144, 255)));

    public static readonly StyledProperty<IBrush> InactiveSelectionBrushProperty =
        AvaloniaProperty.Register<NoteEditor, IBrush>(nameof(InactiveSelectionBrush),
            new SolidColorBrush(Color.FromArgb(60, 130, 130, 130)));

    public static readonly StyledProperty<bool> ClearSelectionOnLostFocusProperty =
        AvaloniaProperty.Register<NoteEditor, bool>(nameof(ClearSelectionOnLostFocus), true);

    public static readonly StyledProperty<IBrush> CaretBrushProperty =
        AvaloniaProperty.Register<NoteEditor, IBrush>(nameof(CaretBrush), Brushes.Black);

    public static readonly StyledProperty<IBrush> ScrollTrackBrushProperty =
        AvaloniaProperty.Register<NoteEditor, IBrush>(nameof(ScrollTrackBrush),
            new SolidColorBrush(Color.FromArgb(24, 0, 0, 0)));

    public static readonly StyledProperty<IBrush> ScrollThumbBrushProperty =
        AvaloniaProperty.Register<NoteEditor, IBrush>(nameof(ScrollThumbBrush),
            new SolidColorBrush(Color.FromArgb(96, 0, 0, 0)));

    public static readonly StyledProperty<Thickness> EditorPaddingProperty =
        AvaloniaProperty.Register<NoteEditor, Thickness>(nameof(EditorPadding), new Thickness(8, 8, 8, 8));

    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<NoteEditor, double>(nameof(LineSpacing), 6.0);

    public static readonly StyledProperty<double> WrapLineSpacingProperty =
        AvaloniaProperty.Register<NoteEditor, double>(nameof(WrapLineSpacing), 2.0);

    public static readonly StyledProperty<IEnumerable<ImageEntry>?> ImagesProperty =
        AvaloniaProperty.Register<NoteEditor, IEnumerable<ImageEntry>?>(nameof(Images));

    public static readonly DirectProperty<NoteEditor, bool> IsDirtyProperty =
        AvaloniaProperty.RegisterDirect<NoteEditor, bool>(nameof(IsDirty), o => o.IsDirty);

    public static readonly StyledProperty<string?> DefaultFontNameProperty =
        AvaloniaProperty.Register<NoteEditor, string?>(nameof(DefaultFontName));

    public static readonly StyledProperty<string?> MarkdownTextProperty =
        AvaloniaProperty.Register<NoteEditor, string?>(nameof(MarkdownText),
            defaultBindingMode: BindingMode.TwoWay);

    public EditorTheme ColorTheme
    {
        get => GetValue(ColorThemeProperty);
        set => SetValue(ColorThemeProperty, value);
    }

    public FontFamily DefaultFont
    {
        get => GetValue(DefaultFontProperty);
        set => SetValue(DefaultFontProperty, value);
    }

    public double DefaultFontSize
    {
        get => GetValue(DefaultFontSizeProperty);
        set => SetValue(DefaultFontSizeProperty, value);
    }

    internal IList<NoteItemData>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public double InlineImageMaxHeight
    {
        get => GetValue(InlineImageMaxHeightProperty);
        set => SetValue(InlineImageMaxHeightProperty, value);
    }

    public ImageDisplayMode ImageDisplay
    {
        get => GetValue(ImageDisplayProperty);
        set => SetValue(ImageDisplayProperty, value);
    }

    public IBrush BackgroundBrush
    {
        get => GetValue(BackgroundBrushProperty);
        set => SetValue(BackgroundBrushProperty, value);
    }

    public IBrush Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    /// <summary>Highlight used while the control does not have focus.</summary>
    public IBrush InactiveSelectionBrush
    {
        get => GetValue(InactiveSelectionBrushProperty);
        set => SetValue(InactiveSelectionBrushProperty, value);
    }

    /// <summary>
    /// Collapses the selection when the control loses focus, matching Avalonia's
    /// <c>TextBox</c>. Set to false to keep it, which is then drawn with
    /// <see cref="InactiveSelectionBrush"/> — useful when an external toolbar
    /// acts on the selection.
    /// </summary>
    public bool ClearSelectionOnLostFocus
    {
        get => GetValue(ClearSelectionOnLostFocusProperty);
        set => SetValue(ClearSelectionOnLostFocusProperty, value);
    }

    public IBrush CaretBrush
    {
        get => GetValue(CaretBrushProperty);
        set => SetValue(CaretBrushProperty, value);
    }

    public IBrush ScrollTrackBrush
    {
        get => GetValue(ScrollTrackBrushProperty);
        set => SetValue(ScrollTrackBrushProperty, value);
    }

    public IBrush ScrollThumbBrush
    {
        get => GetValue(ScrollThumbBrushProperty);
        set => SetValue(ScrollThumbBrushProperty, value);
    }

    public Thickness EditorPadding
    {
        get => GetValue(EditorPaddingProperty);
        set => SetValue(EditorPaddingProperty, value);
    }

    public double LineSpacing
    {
        get => GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    public double WrapLineSpacing
    {
        get => GetValue(WrapLineSpacingProperty);
        set => SetValue(WrapLineSpacingProperty, value);
    }

    public IEnumerable<ImageEntry>? Images
    {
        get => GetValue(ImagesProperty);
        set => SetValue(ImagesProperty, value);
    }

    private bool _isDirty;
    public bool IsDirty => _isDirty;

    public string? DefaultFontName
    {
        get => GetValue(DefaultFontNameProperty);
        set => SetValue(DefaultFontNameProperty, value);
    }

    public string? MarkdownText
    {
        get => GetValue(MarkdownTextProperty);
        set => SetValue(MarkdownTextProperty, value);
    }

    private readonly Dictionary<string, Bitmap> _legacyImageStore = new();

    [Obsolete("Use the Images StyledProperty instead.")]
    public Dictionary<string, Bitmap> ImageStore => _legacyImageStore;

    public event EventHandler? ContentChanged;
    public event EventHandler<ContentChangedEventArgs>? ContentDetailChanged;
    public event EventHandler<ImagePastedEventArgs>? ImagePasted;
    public event EventHandler? DirtyChanged;

    public NoteDocument Document { get; } = new();
    public CursorPosition Caret { get; set; } = CursorPosition.Start;
    public CursorPosition SelectionAnchor { get; set; } = CursorPosition.Start;
    public bool HasSelection => !CurrentSelection.IsEmpty;

    private bool _mouseSelecting;

    private readonly List<double> _itemYPositions = new();
    private readonly List<double> _itemHeights = new();
    private readonly List<List<WrappedLine>> _itemWrapping = new();
    private double _desiredHeight;
    private double _desiredWidth;

    private List<List<ContentElement>>? _internalClipboard;
    private string? _internalClipboardText;
    private int _suppressSyncCount;
    private readonly Dictionary<string, Bitmap> _imageCache = new();
    private readonly List<ImageEntry> _subscribedImageEntries = new();
    private long _changeGeneration;
    private long _cleanGeneration;
    private bool _syncingFontProperties;
    private bool _syncingMarkdownText;
    private double _goalX = -1;
    private readonly FocusSelectionBehavior _focusBehavior;
    private const int MaxCacheEntries = 4096;
    private const int ResizeDebounceMs = 150;
    private readonly Dictionary<(string text, Typeface typeface, double fontSize), double> _measureCache = new();
    private readonly Dictionary<(string text, Typeface typeface, double fontSize), FormattedText> _fmtCache = new();

    private readonly HashSet<int> _dirtyItems = new();
    private bool _fullLayoutRequired = true;
    private double _viewportTop;
    private double _viewportBottom;
    private bool _resizePending;
    private DispatcherTimer? _resizeTimer;

    // ---- Built-in vertical scrolling ----
    private double _offset;
    private double _viewportHeight;
    private bool _internalScroll;
    private bool _draggingThumb;
    private double _thumbDragStartY;
    private double _thumbDragStartOffset;
    private bool _scrollBarWasVisible;
    private const double ScrollBarWidth = 12;
    private const double MinThumbHeight = 30;

    private double MaxOffset =>
        _internalScroll ? Math.Max(0, _desiredHeight - _viewportHeight) : 0;
    private bool ScrollBarVisible => _internalScroll && _viewportHeight > 0 && MaxOffset > 0.5;

    private void ClearTextCaches()
    {
        _measureCache.Clear();
        _fmtCache.Clear();
    }

    private void InvalidateItem(int itemIndex)
    {
        _dirtyItems.Add(itemIndex);
        InvalidateMeasure();
    }

    private void SyncMarkdownTextFromItems()
    {
        if (_syncingMarkdownText) return;
        _syncingMarkdownText = true;
        try { MarkdownText = NoteMarkdown.ToMarkdown(Items); }
        finally { _syncingMarkdownText = false; }
    }

    private void ApplyParsedMarkdownToItems(List<NoteItemData> parsed)
    {
        var items = Items;
        if (items == null)
        {
            Items = new ObservableCollection<NoteItemData>(parsed);
            return;
        }

        using (SuppressSync())
        {
            for (int i = 0; i < parsed.Count; i++)
            {
                if (i < items.Count)
                {
                    if (items[i].Text != parsed[i].Text) items[i].Text = parsed[i].Text;
                }
                else
                {
                    var newData = new NoteItemData(parsed[i].Text);
                    newData.PropertyChanged += OnItemDataPropertyChanged;
                    items.Add(newData);
                }
            }

            while (items.Count > parsed.Count)
            {
                items[items.Count - 1].PropertyChanged -= OnItemDataPropertyChanged;
                items.RemoveAt(items.Count - 1);
            }
        }

        ResetDocumentFromItems();
    }

    private SyncGuard SuppressSync() => new(this);

    private readonly struct SyncGuard : IDisposable
    {
        private readonly NoteEditor _editor;
        public SyncGuard(NoteEditor editor)
        {
            _editor = editor;
            _editor._suppressSyncCount++;
        }
        public void Dispose() => _editor._suppressSyncCount--;
    }

    private const int MaxUndoSteps = 100;
    private const long CoalesceThresholdMs = 800;
    private readonly List<UndoSnapshot> _undoStack = new();
    private readonly List<UndoSnapshot> _redoStack = new();
    private UndoActionKind _lastUndoAction;
    private long _lastUndoTimestamp;

    internal enum UndoActionKind { Other, Typing, Backspace, Delete }

    private record UndoSnapshot(
        List<List<ContentElement>> Items,
        CursorPosition Caret,
        CursorPosition Anchor);

    private static readonly Regex ImagePattern =
        new(@"!\[([^\]]*)\]\(([^)]+)\)", RegexOptions.Compiled);

    public SelectionRange CurrentSelection => new(SelectionAnchor, Caret);

    private class WrappedLine
    {
        public double YOffset;
        public double Height;
        public int StartGlobalOffset;
        public int EndGlobalOffset;
        public readonly List<WrappedSegment> Segments = new();
    }

    private class WrappedSegment
    {
        public int ElementIndex;
        public int LocalStart;
        public int LocalEnd;
        public double X;
        public double Width;
        public double Height;
    }

    public NoteEditor()
    {
        // Created first: the property system can raise OnPropertyChanged while the
        // rest of the constructor runs.
        _focusBehavior = new FocusSelectionBehavior(this,
            () => ClearSelectionOnLostFocus && HasSelection,
            () => { ClearSelectionNoDelete(); InvalidateVisual(); });

        Focusable = true;
        IsTabStop = true;
        ClipToBounds = true;
        SyncDocumentDefaults();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _focusBehavior.Attach();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _focusBehavior.HandleGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _focusBehavior.HandleLostFocus();
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _focusBehavior.Detach();
        _resizeTimer?.Stop();
        base.OnDetachedFromVisualTree(e);
        UnsubscribeItems(Items);
        UnsubscribeImages(Images);
        _imageCache.Clear();
    }

    private void UpdateViewportBounds()
    {
        _viewportTop = _offset;
        _viewportBottom = _offset + (_viewportHeight > 0 ? _viewportHeight : _desiredHeight);
    }

    private void SetOffset(double value)
    {
        double clamped = Math.Clamp(value, 0, MaxOffset);
        if (Math.Abs(clamped - _offset) < 0.001) return;
        _offset = clamped;
        InvalidateVisual();
    }

    private (double y, double height) GetThumbMetrics()
    {
        double track = _viewportHeight;
        double thumbH = _desiredHeight > 0
            ? Math.Max(MinThumbHeight, track * _viewportHeight / _desiredHeight)
            : track;
        thumbH = Math.Min(thumbH, track);
        double max = MaxOffset;
        double y = max > 0 ? (_offset / max) * (track - thumbH) : 0;
        return (y, thumbH);
    }

    private (int first, int last) GetVisibleItemRange()
    {
        if (_itemYPositions.Count == 0)
            return (0, -1);

        int first = BinarySearchFirstVisible();
        int last = BinarySearchLastVisible();

        first = Math.Max(0, first - 1);
        last = Math.Min(_itemYPositions.Count - 1, last + 1);

        return (first, last);
    }

    private int BinarySearchFirstVisible()
    {
        int lo = 0, hi = _itemYPositions.Count - 1;
        int result = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_itemYPositions[mid] + _itemHeights[mid] >= _viewportTop)
            {
                result = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return result;
    }

    private int BinarySearchLastVisible()
    {
        int lo = 0, hi = _itemYPositions.Count - 1;
        int result = hi;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_itemYPositions[mid] <= _viewportBottom)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }

    private void EnsureCaretVisible()
    {
        if (!_internalScroll || _viewportHeight <= 0)
            return;

        // Structural edits mark items dirty but defer the recompute to the next
        // measure pass; flush it here so we scroll against current positions.
        FlushPendingLayout();
        if (_itemYPositions.Count == 0)
            return;

        int idx = Math.Clamp(Caret.ItemIndex, 0, _itemYPositions.Count - 1);
        double caretTop = _itemYPositions[idx];
        double caretBottom = caretTop + _itemHeights[idx];

        if (caretTop < _offset)
            SetOffset(caretTop);
        else if (caretBottom > _offset + _viewportHeight)
            SetOffset(caretBottom - _viewportHeight);
    }

    private void UnsubscribeItems(IList<NoteItemData>? items)
    {
        if (items == null) return;
        if (items is INotifyCollectionChanged ncc)
            ncc.CollectionChanged -= OnItemsCollectionChanged;
        foreach (var item in items)
            item.PropertyChanged -= OnItemDataPropertyChanged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DefaultFontProperty || change.Property == DefaultFontSizeProperty)
        {
            ClearTextCaches();
            SyncDocumentDefaults();
            if (change.Property == DefaultFontProperty && !_syncingFontProperties)
            {
                _syncingFontProperties = true;
                try
                {
                    var font = (FontFamily?)change.NewValue;
                    if (font != null)
                        DefaultFontName = font.Name;
                }
                finally { _syncingFontProperties = false; }
            }
            InvalidateMeasure();
        }
        else if (change.Property == DefaultFontNameProperty && !_syncingFontProperties)
        {
            _syncingFontProperties = true;
            try
            {
                var name = (string?)change.NewValue;
                if (name != null)
                    DefaultFont = new FontFamily(name);
            }
            finally { _syncingFontProperties = false; }
        }
        else if (change.Property == InlineImageMaxHeightProperty
            || change.Property == ImageDisplayProperty
            || change.Property == EditorPaddingProperty
            || change.Property == LineSpacingProperty
            || change.Property == WrapLineSpacingProperty)
        {
            InvalidateMeasure();
        }
        else if (change.Property == ColorThemeProperty)
        {
            ApplyTheme((EditorTheme)change.NewValue!);
        }
        else if (change.Property == BackgroundBrushProperty
            || change.Property == ForegroundProperty
            || change.Property == SelectionBrushProperty
            || change.Property == InactiveSelectionBrushProperty
            || change.Property == CaretBrushProperty
            || change.Property == ScrollTrackBrushProperty
            || change.Property == ScrollThumbBrushProperty)
        {
            if (change.Property == ForegroundProperty)
                _fmtCache.Clear();
            InvalidateVisual();
        }
        else if (change.Property == ItemsProperty)
        {
            OnItemsPropertyChanged(
                change.OldValue as IList<NoteItemData>,
                change.NewValue as IList<NoteItemData>);
        }
        else if (change.Property == ContextMenuProperty)
        {
            _focusBehavior?.HandleContextMenuChanged(change.NewValue as ContextMenu);
        }
        else if (change.Property == ImagesProperty)
        {
            OnImagesPropertyChanged(
                change.OldValue as IEnumerable<ImageEntry>,
                change.NewValue as IEnumerable<ImageEntry>);
        }
        else if (change.Property == MarkdownTextProperty && !_syncingMarkdownText)
        {
            _syncingMarkdownText = true;
            try
            {
                var parsed = NoteMarkdown.ParseMarkdown(change.NewValue as string);
                ApplyParsedMarkdownToItems(parsed);
            }
            finally { _syncingMarkdownText = false; }
        }
    }

    private void ApplyTheme(EditorTheme theme)
    {
        if (theme == EditorTheme.None) return;

        if (theme == EditorTheme.Light)
        {
            BackgroundBrush = Brushes.White;
            Foreground = Brushes.Black;
            SelectionBrush = new SolidColorBrush(Color.FromArgb(80, 30, 144, 255));
            InactiveSelectionBrush = new SolidColorBrush(Color.FromArgb(60, 130, 130, 130));
            CaretBrush = Brushes.Black;
            ScrollTrackBrush = new SolidColorBrush(Color.FromArgb(24, 0, 0, 0));
            ScrollThumbBrush = new SolidColorBrush(Color.FromArgb(96, 0, 0, 0));
        }
        else if (theme == EditorTheme.Dark)
        {
            BackgroundBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220));
            SelectionBrush = new SolidColorBrush(Color.FromArgb(80, 60, 140, 230));
            InactiveSelectionBrush = new SolidColorBrush(Color.FromArgb(70, 150, 150, 150));
            CaretBrush = Brushes.White;
            ScrollTrackBrush = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
            ScrollThumbBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
        }
    }

    private void SyncDocumentDefaults()
    {
        Document.DefaultFont = DefaultFont;
        Document.DefaultFontSize = DefaultFontSize;
    }

    // ---- Layout ----

    // Reserve a gutter for the scrollbar only while it is actually visible, so a
    // note that fits on screen uses the full width. ScrollBarVisible is derived
    // from the previous layout pass's _desiredHeight, which makes this converge
    // without a width<->visibility feedback loop (a note crossing the one-screen
    // boundary just re-wraps once on the next pass).
    private double AvailableContentWidth =>
        Math.Max((_desiredWidth > 0 ? _desiredWidth : 400)
            - EditorPadding.Left - EditorPadding.Right
            - (ScrollBarVisible ? ScrollBarWidth : 0), 50);

    private double ComputeItemHeight(List<WrappedLine> wrappedLines)
    {
        double height = 0;
        for (int li = 0; li < wrappedLines.Count; li++)
        {
            height += wrappedLines[li].Height;
            if (li < wrappedLines.Count - 1) height += WrapLineSpacing;
        }
        return Math.Max(height, Document.DefaultFontSize + 4);
    }

    // Bring _itemYPositions/_itemHeights up to date when a recompute is pending
    // but the next measure pass has not run yet (e.g. right after a structural edit).
    private void FlushPendingLayout()
    {
        if (_fullLayoutRequired || _itemWrapping.Count != Document.Items.Count)
            ComputeLayout();
        else if (_dirtyItems.Count > 0)
            ComputeIncrementalLayout();
    }

    private void ComputeLayout()
    {
        _itemYPositions.Clear();
        _itemHeights.Clear();
        _itemWrapping.Clear();
        _dirtyItems.Clear();
        _fullLayoutRequired = false;
        _resizePending = false;

        double availWidth = AvailableContentWidth;
        double y = EditorPadding.Top;

        for (int i = 0; i < Document.Items.Count; i++)
        {
            _itemYPositions.Add(y);

            var wrappedLines = ComputeItemWrapping(Document.Items[i], availWidth);
            _itemWrapping.Add(wrappedLines);

            double itemH = ComputeItemHeight(wrappedLines);
            _itemHeights.Add(itemH);
            y += itemH + LineSpacing;
        }

        _desiredHeight = y + EditorPadding.Bottom;
    }

    private void ComputeIncrementalLayout()
    {
        double availWidth = AvailableContentWidth;

        foreach (int i in _dirtyItems.OrderBy(x => x))
        {
            if (i >= Document.Items.Count || i >= _itemWrapping.Count) continue;

            double oldHeight = _itemHeights[i];
            var wrappedLines = ComputeItemWrapping(Document.Items[i], availWidth);
            _itemWrapping[i] = wrappedLines;

            double newHeight = ComputeItemHeight(wrappedLines);
            double delta = newHeight - oldHeight;
            _itemHeights[i] = newHeight;

            if (Math.Abs(delta) > 0.001)
            {
                for (int j = i + 1; j < _itemYPositions.Count; j++)
                    _itemYPositions[j] += delta;
                _desiredHeight += delta;
            }
        }
        _dirtyItems.Clear();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;
        double newWidth = Math.Max(w, 200);

        UpdateViewportMetrics(availableSize.Height);

        if (_fullLayoutRequired || _itemWrapping.Count != Document.Items.Count)
        {
            _desiredWidth = newWidth;
            ComputeLayout();
        }
        else if (Math.Abs(newWidth - _desiredWidth) > 0.1)
        {
            _desiredWidth = newWidth;
            ComputeViewportResizeLayout();
        }
        else if (_dirtyItems.Count > 0)
        {
            ComputeIncrementalLayout();
        }

        if (_offset > MaxOffset) _offset = MaxOffset;

        // When the scrollbar appears/disappears the content gutter changes, so the
        // items were wrapped at the wrong width. Force a full re-wrap on the next
        // pass. This converges in one extra pass: showing the bar makes content
        // narrower (still overflowing), hiding it makes content fit.
        bool scrollBarVisible = ScrollBarVisible;
        if (scrollBarVisible != _scrollBarWasVisible)
        {
            _scrollBarWasVisible = scrollBarVisible;
            _fullLayoutRequired = true;
            InvalidateMeasure();
        }

        // In internal-scroll mode the control fills the available height and
        // scrolls its content; otherwise it reports the full content height.
        double measuredHeight = _internalScroll ? availableSize.Height : _desiredHeight;
        return new Size(_desiredWidth, measuredHeight);
    }

    // Internal scrolling is active whenever the control is given a finite height
    // (the common case); an infinite constraint means an outer container scrolls.
    private void UpdateViewportMetrics(double availableHeight)
    {
        _internalScroll = !double.IsInfinity(availableHeight);
        _viewportHeight = _internalScroll ? availableHeight : 0;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double newWidth = Math.Max(finalSize.Width, 200);
        UpdateViewportMetrics(finalSize.Height);

        if (_itemWrapping.Count == 0)
        {
            _desiredWidth = newWidth;
            ComputeLayout();
        }
        else if (Math.Abs(newWidth - _desiredWidth) > 0.1)
        {
            _desiredWidth = newWidth;
            ComputeViewportResizeLayout();
        }

        if (_offset > MaxOffset) _offset = MaxOffset;

        double height = _internalScroll ? finalSize.Height : Math.Max(finalSize.Height, _desiredHeight);
        return new Size(_desiredWidth, height);
    }

    private void ComputeViewportResizeLayout()
    {
        double availWidth = AvailableContentWidth;
        var (first, last) = GetVisibleItemRange();

        for (int i = first; i <= last && i < Document.Items.Count && i < _itemHeights.Count; i++)
        {
            double oldHeight = _itemHeights[i];
            var wrappedLines = ComputeItemWrapping(Document.Items[i], availWidth);
            _itemWrapping[i] = wrappedLines;
            _itemHeights[i] = ComputeItemHeight(wrappedLines);
            double delta = _itemHeights[i] - oldHeight;

            if (Math.Abs(delta) > 0.001)
            {
                for (int j = i + 1; j < _itemYPositions.Count; j++)
                    _itemYPositions[j] += delta;
                _desiredHeight += delta;
            }
        }

        ScheduleDeferredLayout();
    }

    private void ScheduleDeferredLayout()
    {
        _resizePending = true;
        if (_resizeTimer == null)
        {
            _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ResizeDebounceMs) };
            _resizeTimer.Tick += OnResizeTimerTick;
        }
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void OnResizeTimerTick(object? sender, EventArgs e)
    {
        _resizeTimer?.Stop();
        if (!_resizePending) return;
        _resizePending = false;
        _fullLayoutRequired = true;
        InvalidateMeasure();
    }

    // ---- Rendering ----

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));

        if (_itemWrapping.Count != Document.Items.Count)
            ComputeLayout();

        UpdateViewportBounds();
        var (firstVisible, lastVisible) = GetVisibleItemRange();

        var (selFirst, selLast) = CurrentSelection.Ordered();
        var selBrush = IsFocused ? SelectionBrush : InactiveSelectionBrush;

        // Content is translated by the scroll offset; the scrollbar (drawn after)
        // stays in viewport coordinates. using() keeps the push exception-safe.
        using (context.PushTransform(Matrix.CreateTranslation(0, -_offset)))
            RenderContent(context, firstVisible, lastVisible, selFirst, selLast, selBrush);

        RenderScrollBar(context);
    }

    private void RenderContent(DrawingContext context, int firstVisible, int lastVisible,
        CursorPosition selFirst, CursorPosition selLast, IBrush selBrush)
    {
        for (int i = firstVisible; i <= lastVisible && i < Document.Items.Count; i++)
        {
            var item = Document.Items[i];
            double itemY = _itemYPositions[i];
            var wrappedLines = _itemWrapping[i];

            if (item.Elements.Count == 0)
            {
                if (i == Caret.ItemIndex && !HasSelection && IsFocused)
                    context.FillRectangle(CaretBrush,
                        new Rect(EditorPadding.Left, itemY, 1.5, Document.DefaultFontSize + 4));
                continue;
            }

            foreach (var wLine in wrappedLines)
            {
                double lineY = itemY + wLine.YOffset;

                foreach (var seg in wLine.Segments)
                {
                    var el = item.Elements[seg.ElementIndex];
                    double segX = EditorPadding.Left + seg.X;
                    int segGlobalStart = item.GlobalOffset(seg.ElementIndex, seg.LocalStart);

                    double segY = lineY + (wLine.Height - seg.Height);

                    if (el.Type == ContentElementType.Image && el.Image != null)
                    {
                        var imgRect = new Rect(segX, segY, seg.Width - 2, seg.Height);
                        context.DrawImage(el.Image, imgRect);

                        bool inSel = IsOffsetInSelection(i, segGlobalStart, selFirst, selLast);
                        if (inSel)
                            context.FillRectangle(selBrush, imgRect);
                    }
                    else
                    {
                        var (typeface, fs) = ResolveFont(el);
                        string segText = el.Text[seg.LocalStart..seg.LocalEnd];

                        int selStartInSeg = Math.Max(0,
                            (i == selFirst.ItemIndex ? selFirst.Offset : 0) - segGlobalStart);
                        int selEndInSeg = Math.Min(segText.Length,
                            (i == selLast.ItemIndex ? selLast.Offset : int.MaxValue) - segGlobalStart);
                        if (!CurrentSelection.IsEmpty && selStartInSeg < selEndInSeg
                            && i >= selFirst.ItemIndex && i <= selLast.ItemIndex)
                        {
                            double sX = selStartInSeg > 0
                                ? MeasureTextWidth(segText[..selStartInSeg], typeface, fs) : 0;
                            double sW = MeasureTextWidth(segText[selStartInSeg..selEndInSeg], typeface, fs);
                            context.FillRectangle(selBrush, new Rect(segX + sX, segY, sW, seg.Height));
                        }

                        var fmtKey = (segText, typeface, fs);
                        if (!_fmtCache.TryGetValue(fmtKey, out var fmt))
                        {
                            fmt = new FormattedText(segText,
                                System.Globalization.CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight, typeface, fs, Foreground);
                            if (_fmtCache.Count > MaxCacheEntries) _fmtCache.Clear();
                            _fmtCache[fmtKey] = fmt;
                        }

                        context.DrawText(fmt, new Point(segX, segY));
                    }
                }
            }

            if (i == Caret.ItemIndex && !HasSelection && IsFocused)
            {
                var (caretX, caretYOff, caretLineH) = CalculateCaretPosition(i, Caret.Offset);
                context.FillRectangle(CaretBrush,
                    new Rect(EditorPadding.Left + caretX, itemY + caretYOff, 1.5, caretLineH));
            }
        }
    }

    private void RenderScrollBar(DrawingContext context)
    {
        if (!ScrollBarVisible) return;

        double x = Bounds.Width - ScrollBarWidth;
        context.FillRectangle(ScrollTrackBrush, new Rect(x, 0, ScrollBarWidth, _viewportHeight));

        var (thumbY, thumbH) = GetThumbMetrics();
        context.FillRectangle(ScrollThumbBrush,
            new Rect(x + 2, thumbY, ScrollBarWidth - 4, thumbH), 4);
    }

    private bool IsOffsetInSelection(int itemIdx, int offset,
        CursorPosition selFirst, CursorPosition selLast)
    {
        if (CurrentSelection.IsEmpty) return false;
        if (itemIdx < selFirst.ItemIndex || itemIdx > selLast.ItemIndex) return false;
        if (itemIdx == selFirst.ItemIndex && itemIdx == selLast.ItemIndex)
            return offset >= selFirst.Offset && offset < selLast.Offset;
        if (itemIdx == selFirst.ItemIndex)
            return offset >= selFirst.Offset;
        if (itemIdx == selLast.ItemIndex)
            return offset < selLast.Offset;
        return true;
    }

    private (double x, double yOffset, double lineHeight) CalculateCaretPosition(
        int itemIndex, int globalOffset)
    {
        double textH = Document.DefaultFontSize + 4;

        if (itemIndex >= _itemWrapping.Count)
            return (0, 0, textH);

        var wrappedLines = _itemWrapping[itemIndex];
        var item = Document.Items[itemIndex];

        for (int li = 0; li < wrappedLines.Count; li++)
        {
            var wLine = wrappedLines[li];
            bool isLastLine = li == wrappedLines.Count - 1;

            if (globalOffset <= wLine.EndGlobalOffset
                || isLastLine)
            {
                foreach (var seg in wLine.Segments)
                {
                    int segGlobalStart = item.GlobalOffset(seg.ElementIndex, seg.LocalStart);
                    int segLen = seg.LocalEnd - seg.LocalStart;

                    if (globalOffset <= segGlobalStart + segLen)
                    {
                        int localOff = Math.Max(0, globalOffset - segGlobalStart);
                        var el = item.Elements[seg.ElementIndex];
                        double x;

                        if (el.Type == ContentElementType.Image)
                        {
                            x = seg.X + (localOff > 0 ? seg.Width : 0);
                        }
                        else
                        {
                            var (typeface, fs) = ResolveFont(el);
                            x = seg.X + MeasureTextWidth(
                                el.Text[seg.LocalStart..(seg.LocalStart + localOff)], typeface, fs);
                        }

                        double h = seg.Height;
                        return (x, wLine.YOffset + (wLine.Height - h), h);
                    }
                }

                double endX = 0;
                double endH = textH;
                if (wLine.Segments.Count > 0)
                {
                    var lastSeg = wLine.Segments[^1];
                    endX = lastSeg.X + lastSeg.Width;
                    endH = lastSeg.Height;
                }
                return (endX, wLine.YOffset + (wLine.Height - endH), endH);
            }
        }

        if (wrappedLines.Count > 0)
        {
            var lastLine = wrappedLines[^1];
            double x = 0;
            if (lastLine.Segments.Count > 0)
            {
                var lastSeg = lastLine.Segments[^1];
                x = lastSeg.X + lastSeg.Width;
            }
            double lastH = lastLine.Segments.Count > 0 ? lastLine.Segments[^1].Height : textH;
            return (x, lastLine.YOffset + (lastLine.Height - lastH), lastH);
        }

        return (0, 0, textH);
    }

    private (int start, int end) GetCurrentWrappedLineRange(int itemIndex, int globalOffset)
    {
        if (itemIndex >= _itemWrapping.Count)
            return (0, 0);

        var wrappedLines = _itemWrapping[itemIndex];
        for (int li = 0; li < wrappedLines.Count; li++)
        {
            var wLine = wrappedLines[li];
            bool isLastLine = li == wrappedLines.Count - 1;
            if (globalOffset <= wLine.EndGlobalOffset
                || isLastLine)
            {
                return (wLine.StartGlobalOffset, wLine.EndGlobalOffset);
            }
        }

        if (wrappedLines.Count > 0)
        {
            var last = wrappedLines[^1];
            return (last.StartGlobalOffset, last.EndGlobalOffset);
        }
        return (0, 0);
    }

    private int HitTestLineOffset(NoteItem item, WrappedLine wLine, double targetX)
    {
        foreach (var seg in wLine.Segments)
        {
            var el = item.Elements[seg.ElementIndex];
            int segGlobalStart = item.GlobalOffset(seg.ElementIndex, seg.LocalStart);

            if (el.Type == ContentElementType.Image)
            {
                if (targetX < seg.X + seg.Width / 2) return segGlobalStart;
                if (targetX < seg.X + seg.Width) return segGlobalStart + 1;
            }
            else
            {
                var (typeface, fs) = ResolveFont(el);
                string segText = el.Text[seg.LocalStart..seg.LocalEnd];

                if (targetX <= seg.X + seg.Width)
                {
                    double relX = targetX - seg.X;
                    int lo = 0, hi = segText.Length;
                    while (lo < hi)
                    {
                        int mid = (lo + hi) / 2;
                        double w = MeasureTextWidth(segText[..(mid + 1)], typeface, fs);
                        if (w <= relX)
                            lo = mid + 1;
                        else
                            hi = mid;
                    }
                    if (lo < segText.Length)
                    {
                        double leftEdge = lo > 0 ? MeasureTextWidth(segText[..lo], typeface, fs) : 0;
                        double charW = MeasureTextWidth(segText[lo..(lo + 1)], typeface, fs);
                        if (relX < leftEdge + charW / 2) return segGlobalStart + lo;
                        return segGlobalStart + lo + 1;
                    }
                    return segGlobalStart + segText.Length;
                }
            }
        }

        if (wLine.Segments.Count > 0)
        {
            var lastSeg = wLine.Segments[^1];
            return item.GlobalOffset(lastSeg.ElementIndex, lastSeg.LocalEnd);
        }
        return wLine.StartGlobalOffset;
    }

    private Typeface BuildTypeface(ContentElement el)
    {
        return new Typeface(
            el.Font != FontFamily.Default ? el.Font : Document.DefaultFont,
            el.Italic ? FontStyle.Italic : FontStyle.Normal,
            el.Bold ? FontWeight.Bold : FontWeight.Normal);
    }

    private double ResolveFontSize(ContentElement el) =>
        el.FontSize > 0 ? el.FontSize : Document.DefaultFontSize;

    private (Typeface typeface, double fontSize) ResolveFont(ContentElement el) =>
        (BuildTypeface(el), ResolveFontSize(el));

    private double MeasureTextWidth(string text, Typeface typeface, double fontSize)
    {
        if (text.Length == 0) return 0;
        var key = (text, typeface, fontSize);
        if (_measureCache.TryGetValue(key, out var cached))
            return cached;
        var fmt = new FormattedText(text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, fontSize, Brushes.Transparent);
        var width = fmt.WidthIncludingTrailingWhitespace;
        if (_measureCache.Count > MaxCacheEntries) _measureCache.Clear();
        _measureCache[key] = width;
        return width;
    }

    // ---- Wrapping ----

    private List<WrappedLine> ComputeItemWrapping(NoteItem item, double availWidth)
    {
        var lines = new List<WrappedLine>();
        var currentLine = new WrappedLine();
        lines.Add(currentLine);
        double lineX = 0;
        double lineH = Document.DefaultFontSize + 4;
        int globalOff = 0;

        for (int ei = 0; ei < item.Elements.Count; ei++)
        {
            var el = item.Elements[ei];

            if (el.Type == ContentElementType.Image && el.Image != null)
            {
                double imgH = Math.Min(el.ImageHeight, InlineImageMaxHeight);
                double scale = imgH / el.ImageHeight;
                double imgW = el.ImageWidth * scale + 2;
                bool blockMode = ImageDisplay == ImageDisplayMode.Block;

                if (lineX > 0 && (blockMode || lineX + imgW > availWidth))
                {
                    currentLine.Height = lineH;
                    currentLine.EndGlobalOffset = globalOff;
                    currentLine = new WrappedLine { StartGlobalOffset = globalOff };
                    lines.Add(currentLine);
                    lineX = 0;
                    lineH = Document.DefaultFontSize + 4;
                }

                currentLine.Segments.Add(new WrappedSegment
                {
                    ElementIndex = ei, LocalStart = 0, LocalEnd = 1,
                    X = lineX, Width = imgW, Height = imgH
                });

                lineX += imgW;
                lineH = Math.Max(lineH, imgH);
                globalOff += 1;

                if (blockMode)
                {
                    currentLine.Height = lineH;
                    currentLine.EndGlobalOffset = globalOff;
                    currentLine = new WrappedLine { StartGlobalOffset = globalOff };
                    lines.Add(currentLine);
                    lineX = 0;
                    lineH = Document.DefaultFontSize + 4;
                }
            }
            else if (el.Type == ContentElementType.Text && el.Text.Length > 0)
            {
                var (typeface, fs) = ResolveFont(el);
                double textH = fs + 4;

                int textStart = 0;
                while (textStart < el.Text.Length)
                {
                    string remaining = el.Text[textStart..];
                    double remainingWidth = MeasureTextWidth(remaining, typeface, fs);

                    if (lineX + remainingWidth <= availWidth)
                    {
                        currentLine.Segments.Add(new WrappedSegment
                        {
                            ElementIndex = ei, LocalStart = textStart, LocalEnd = el.Text.Length,
                            X = lineX, Width = remainingWidth, Height = textH
                        });
                        lineX += remainingWidth;
                        lineH = Math.Max(lineH, textH);
                        globalOff += el.Text.Length - textStart;
                        break;
                    }

                    double availForText = availWidth - lineX;
                    int breakPos = FindTextBreakPosition(el.Text, textStart, typeface, fs, availForText);

                    if (breakPos <= textStart)
                    {
                        if (currentLine.Segments.Count > 0)
                        {
                            currentLine.Height = lineH;
                            currentLine.EndGlobalOffset = globalOff;
                            currentLine = new WrappedLine { StartGlobalOffset = globalOff };
                            lines.Add(currentLine);
                            lineX = 0;
                            lineH = Document.DefaultFontSize + 4;
                            continue;
                        }
                        breakPos = textStart + 1;
                    }

                    string chunk = el.Text[textStart..breakPos];
                    double chunkWidth = MeasureTextWidth(chunk, typeface, fs);

                    currentLine.Segments.Add(new WrappedSegment
                    {
                        ElementIndex = ei, LocalStart = textStart, LocalEnd = breakPos,
                        X = lineX, Width = chunkWidth, Height = textH
                    });

                    lineH = Math.Max(lineH, textH);
                    globalOff += breakPos - textStart;
                    textStart = breakPos;

                    currentLine.Height = lineH;
                    currentLine.EndGlobalOffset = globalOff;
                    currentLine = new WrappedLine { StartGlobalOffset = globalOff };
                    lines.Add(currentLine);
                    lineX = 0;
                    lineH = Document.DefaultFontSize + 4;
                }
            }
        }

        currentLine.Height = lineH;
        currentLine.EndGlobalOffset = item.TextLength;

        if (lines.Count > 1 && lines[^1].Segments.Count == 0)
            lines.RemoveAt(lines.Count - 1);

        double y = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            lines[i].YOffset = y;
            y += lines[i].Height;
            if (i < lines.Count - 1) y += WrapLineSpacing;
        }

        return lines;
    }

    private int FindTextBreakPosition(string text, int start, Typeface typeface,
        double fontSize, double availWidth)
    {
        if (availWidth <= 0) return start;

        int len = text.Length - start;
        if (len <= 0) return start;

        if (MeasureTextWidth(text[start..], typeface, fontSize) <= availWidth)
            return text.Length;

        int lo = 1, hi = len;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            double w = MeasureTextWidth(text[start..(start + mid)], typeface, fontSize);
            if (w <= availWidth)
                lo = mid + 1;
            else
                hi = mid;
        }

        int fitCount = lo - 1;
        int breakAt = start + fitCount;

        if (breakAt > start)
        {
            int lastSpace = text.LastIndexOf(' ', breakAt - 1, breakAt - start);
            if (lastSpace > start) return lastSpace + 1;
        }

        return breakAt > start ? breakAt : start;
    }

    // ---- Mouse input ----

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!ScrollBarVisible || e.Delta.Y == 0) return;

        double step = (Document.DefaultFontSize + LineSpacing) * 3;
        double before = _offset;
        SetOffset(_offset - e.Delta.Y * step);

        // Only consume the event if we actually scrolled; at the extremes let it
        // bubble so an enclosing scrollable (if any) can take over.
        if (_offset != before) e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var pos = e.GetPosition(this);

        if (ScrollBarVisible && pos.X >= Bounds.Width - ScrollBarWidth)
        {
            var (thumbY, thumbH) = GetThumbMetrics();
            if (pos.Y >= thumbY && pos.Y <= thumbY + thumbH)
            {
                _draggingThumb = true;
                _thumbDragStartY = pos.Y;
                _thumbDragStartOffset = _offset;
                e.Pointer.Capture(this);
            }
            else
            {
                SetOffset(_offset + (pos.Y < thumbY ? -_viewportHeight : _viewportHeight));
            }
            e.Handled = true;
            return;
        }

        Focus(NavigationMethod.Pointer);
        _goalX = -1;

        var cursor = HitTestCursor(pos);
        var props = e.GetCurrentPoint(this).Properties;

        if (e.ClickCount == 2 && props.IsLeftButtonPressed)
        {
            Caret = cursor;
            SelectWordAtCaret();
            _mouseSelecting = false;
            InvalidateVisual();
            return;
        }

        Caret = cursor;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _mouseSelecting = true;
        }
        else
        {
            SelectionAnchor = cursor;
            _mouseSelecting = true;
        }

        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_draggingThumb)
        {
            var p = e.GetPosition(this);
            var (_, thumbH) = GetThumbMetrics();
            double usable = _viewportHeight - thumbH;
            if (usable > 0)
                SetOffset(_thumbDragStartOffset + (p.Y - _thumbDragStartY) / usable * MaxOffset);
            e.Handled = true;
            return;
        }

        if (!_mouseSelecting) return;

        var pos = e.GetPosition(this);
        Caret = HitTestCursor(pos);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _mouseSelecting = false;
        _draggingThumb = false;
        e.Pointer.Capture(null);
    }

    private int HitTestItem(double y)
    {
        if (_itemYPositions.Count == 0)
            return 0;

        int lo = 0, hi = _itemYPositions.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            double top = _itemYPositions[mid];
            double bottom = top + _itemHeights[mid] + LineSpacing;
            if (y < top)
                hi = mid - 1;
            else if (y >= bottom)
                lo = mid + 1;
            else
                return mid;
        }
        return Math.Clamp(lo, 0, Document.Items.Count - 1);
    }

    private CursorPosition HitTestCursor(Point pos)
    {
        // Translate viewport coordinates into content coordinates.
        pos = pos.WithY(pos.Y + _offset);
        int itemIdx = HitTestItem(pos.Y);
        if (itemIdx < 0) return CursorPosition.Start;
        itemIdx = Math.Clamp(itemIdx, 0, Document.Items.Count - 1);

        var item = Document.Items[itemIdx];
        double relX = pos.X - EditorPadding.Left;
        if (relX < 0) return new CursorPosition(itemIdx, 0);

        if (itemIdx >= _itemWrapping.Count)
            return new CursorPosition(itemIdx, 0);

        var wrappedLines = _itemWrapping[itemIdx];
        double itemY = _itemYPositions[itemIdx];
        double relY = pos.Y - itemY;

        WrappedLine? targetLine = null;
        foreach (var wLine in wrappedLines)
        {
            if (relY < wLine.YOffset + wLine.Height + WrapLineSpacing
                || wLine == wrappedLines[^1])
            {
                targetLine = wLine;
                break;
            }
        }

        if (targetLine == null || targetLine.Segments.Count == 0)
            return new CursorPosition(itemIdx, item.TextLength);

        return new CursorPosition(itemIdx, HitTestLineOffset(item, targetLine, relX));
    }

    // ---- Keyboard input ----

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text)) return;
        _goalX = -1;

        SaveUndoState(HasSelection ? UndoActionKind.Other : UndoActionKind.Typing);
        DeleteSelection();
        InsertTextAtCaret(e.Text);
        EnsureCaretVisible();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key != Key.Up && e.Key != Key.Down)
            _goalX = -1;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.Enter:
                SaveUndoState();
                DeleteSelection();
                SplitItemAtCaret();
                e.Handled = true;
                break;

            case Key.Back when ctrl:
                SaveUndoState();
                if (HasSelection)
                    DeleteSelection();
                else
                    DeleteWordBackward();
                e.Handled = true;
                break;

            case Key.Back:
                SaveUndoState(HasSelection ? UndoActionKind.Other : UndoActionKind.Backspace);
                if (HasSelection)
                    DeleteSelection();
                else
                    HandleBackspace();
                e.Handled = true;
                break;

            case Key.Delete when ctrl:
                SaveUndoState();
                if (HasSelection)
                    DeleteSelection();
                else
                    DeleteWordForward();
                e.Handled = true;
                break;

            case Key.Delete:
                SaveUndoState(HasSelection ? UndoActionKind.Other : UndoActionKind.Delete);
                if (HasSelection)
                    DeleteSelection();
                else
                    HandleDelete();
                e.Handled = true;
                break;

            case Key.Left when ctrl:
                MoveCaretByWord(-1, shift);
                e.Handled = true;
                break;

            case Key.Left:
                MoveCaret(-1, shift);
                e.Handled = true;
                break;

            case Key.Right when ctrl:
                MoveCaretByWord(1, shift);
                e.Handled = true;
                break;

            case Key.Right:
                MoveCaret(1, shift);
                e.Handled = true;
                break;

            case Key.Up:
                MoveCaretVertical(-1, shift);
                e.Handled = true;
                break;

            case Key.Down:
                MoveCaretVertical(1, shift);
                e.Handled = true;
                break;

            case Key.Home when ctrl:
                var docStart = CursorPosition.Start;
                if (!shift) SelectionAnchor = docStart;
                Caret = docStart;
                InvalidateVisual();
                e.Handled = true;
                break;

            case Key.Home:
                var (lineStart, _) = GetCurrentWrappedLineRange(Caret.ItemIndex, Caret.Offset);
                var home = new CursorPosition(Caret.ItemIndex, lineStart);
                if (!shift) SelectionAnchor = home;
                Caret = home;
                InvalidateVisual();
                e.Handled = true;
                break;

            case Key.End when ctrl:
                var lastItem = Document.Items[^1];
                var docEnd = new CursorPosition(Document.Items.Count - 1, lastItem.TextLength);
                if (!shift) SelectionAnchor = docEnd;
                Caret = docEnd;
                InvalidateVisual();
                e.Handled = true;
                break;

            case Key.End:
                var (_, lineEnd) = GetCurrentWrappedLineRange(Caret.ItemIndex, Caret.Offset);
                var end = new CursorPosition(Caret.ItemIndex, lineEnd);
                if (!shift) SelectionAnchor = end;
                Caret = end;
                InvalidateVisual();
                e.Handled = true;
                break;

            case Key.A when ctrl:
                SelectAll();
                e.Handled = true;
                break;

            case Key.C when ctrl:
                CopyToClipboard();
                e.Handled = true;
                break;

            case Key.X when ctrl:
                SaveUndoState();
                CopyToClipboard();
                DeleteSelection();
                e.Handled = true;
                break;

            case Key.V when ctrl:
                _ = PasteFromClipboard();
                e.Handled = true;
                break;

            case Key.Z when ctrl:
                Undo();
                e.Handled = true;
                break;

            case Key.Y when ctrl:
                Redo();
                e.Handled = true;
                break;
        }

        if (e.Handled)
            EnsureCaretVisible();
    }

    private NoteItem CurrentItem => Document.Items[Math.Clamp(Caret.ItemIndex, 0, Document.Items.Count - 1)];

    // ---- Edit operations ----

    public void InsertTextAtCaret(string text)
    {
        EnsureValidCaret();
        var item = Document.Items[Caret.ItemIndex];
        int offset = Math.Min(Caret.Offset, item.TextLength);

        if (item.Elements.Count == 0)
        {
            item.Elements.Add(ContentElement.CreateText(text));
            Caret = new CursorPosition(Caret.ItemIndex, text.Length);
        }
        else
        {
            var (elIdx, localOff) = item.ResolveOffset(offset);
            var el = item.Elements[elIdx];

            if (el.Type == ContentElementType.Text)
            {
                el.Text = el.Text.Insert(localOff, text);
                Caret = new CursorPosition(Caret.ItemIndex, offset + text.Length);
            }
            else
            {
                var newEl = ContentElement.CreateText(text);
                item.Elements.Insert(elIdx + (localOff > 0 ? 1 : 0), newEl);
                Caret = new CursorPosition(Caret.ItemIndex, offset + text.Length);
            }
        }

        ClearSelectionNoDelete();
        InvalidateItem(Caret.ItemIndex);
        NotifyDocumentChanged();
    }

    private string RegisterPastedImage(Bitmap bitmap)
    {
        string key = $"img_{Guid.NewGuid():N}";
        _imageCache[key] = bitmap;

        var args = new ImagePastedEventArgs(bitmap, key);
        ImagePasted?.Invoke(this, args);

        if (args.NewKey != null && args.NewKey != key)
        {
            _imageCache.Remove(key);
            key = args.NewKey;
            _imageCache[key] = bitmap;
        }

        if (Images is ICollection<ImageEntry> imageCollection)
            imageCollection.Add(new ImageEntry(key, bitmap));

        _legacyImageStore[key] = bitmap;
        return key;
    }

    public void InsertImageAtCaret(Bitmap bitmap)
    {
        EnsureValidCaret();
        SaveUndoState();
        InsertImageAtCaretCore(bitmap);
    }

    private void InsertImageAtCaretCore(Bitmap bitmap)
    {
        DeleteSelection();
        var item = Document.Items[Caret.ItemIndex];
        int offset = Math.Min(Caret.Offset, item.TextLength);

        string key = RegisterPastedImage(bitmap);

        var imgEl = ContentElement.CreateImage(bitmap);
        imgEl.ImageKey = key;

        if (item.Elements.Count == 0)
        {
            item.Elements.Add(imgEl);
            Caret = new CursorPosition(Caret.ItemIndex, 1);
        }
        else
        {
            var (elIdx, localOff) = item.ResolveOffset(offset);
            var el = item.Elements[elIdx];

            if (el.Type == ContentElementType.Text && localOff < el.Text.Length)
            {
                var after = ContentElement.CreateText(el.Text[localOff..], el.Font, el.FontSize);
                el.Text = el.Text[..localOff];
                item.Elements.Insert(elIdx + 1, imgEl);
                if (after.Text.Length > 0)
                    item.Elements.Insert(elIdx + 2, after);
            }
            else
            {
                item.Elements.Insert(elIdx + 1, imgEl);
            }

            Caret = new CursorPosition(Caret.ItemIndex, offset + 1);
        }

        ClearSelectionNoDelete();
        InvalidateItem(Caret.ItemIndex);
        NotifyDocumentChanged(ChangeKind.ImageChanged);
    }

    public void SplitItemAtCaret()
    {
        EnsureValidCaret();
        var item = Document.Items[Caret.ItemIndex];
        int offset = Math.Min(Caret.Offset, item.TextLength);

        var newItem = new NoteItem();

        int pos = 0;
        bool split = false;
        for (int i = 0; i < item.Elements.Count; i++)
        {
            var el = item.Elements[i];
            int elLen = el.Type == ContentElementType.Text ? el.Text.Length : 1;

            if (!split && offset <= pos + elLen)
            {
                if (el.Type == ContentElementType.Text)
                {
                    int localOff = offset - pos;
                    string remainder = el.Text[localOff..];
                    el.Text = el.Text[..localOff];

                    if (remainder.Length > 0)
                        newItem.Elements.Add(ContentElement.CreateText(remainder, el.Font, el.FontSize));
                }
                else
                {
                    if (offset == pos)
                    {
                        newItem.Elements.Add(el.Clone());
                        item.Elements.RemoveAt(i);
                        i--;
                    }
                }

                split = true;
                for (int j = i + 1; j < item.Elements.Count; j++)
                    newItem.Elements.Add(item.Elements[j]);
                for (int j = item.Elements.Count - 1; j > i; j--)
                    item.Elements.RemoveAt(j);

                if (item.Elements.Count > 0 && item.Elements[^1].Type == ContentElementType.Text
                    && item.Elements[^1].Text.Length == 0)
                    item.Elements.RemoveAt(item.Elements.Count - 1);

                break;
            }
            pos += elLen;
        }

        Document.Items.Insert(Caret.ItemIndex + 1, newItem);
        Caret = new CursorPosition(Caret.ItemIndex + 1, 0);
        ClearSelectionNoDelete();
        _fullLayoutRequired = true;
        InvalidateMeasure();
        NotifyDocumentChanged(ChangeKind.StructureChanged);
    }

    private void HandleBackspace()
    {
        EnsureValidCaret();
        var item = Document.Items[Caret.ItemIndex];

        if (Caret.Offset > 0)
        {
            int offset = Math.Min(Caret.Offset, item.TextLength);
            var (elIdx, localOff) = item.ResolveOffset(offset - 1);
            var el = item.Elements[elIdx];

            if (el.Type == ContentElementType.Text)
            {
                int deleteAt = localOff;
                if (deleteAt < el.Text.Length)
                {
                    el.Text = el.Text.Remove(deleteAt, 1);
                    if (el.Text.Length == 0) item.Elements.RemoveAt(elIdx);
                }
            }
            else
            {
                item.Elements.RemoveAt(elIdx);
            }

            Caret = new CursorPosition(Caret.ItemIndex, offset - 1);
        }
        else if (Caret.ItemIndex > 0)
        {
            var prevItem = Document.Items[Caret.ItemIndex - 1];
            int prevLen = prevItem.TextLength;

            foreach (var el in item.Elements)
                prevItem.Elements.Add(el);

            Document.Items.RemoveAt(Caret.ItemIndex);
            Caret = new CursorPosition(Caret.ItemIndex - 1, prevLen);
        }

        ClearSelectionNoDelete();
        _fullLayoutRequired = true;
        InvalidateMeasure();
        NotifyDocumentChanged();
    }

    private void HandleDelete()
    {
        EnsureValidCaret();
        var item = Document.Items[Caret.ItemIndex];

        if (Caret.Offset < item.TextLength)
        {
            var (elIdx, localOff) = item.ResolveOffset(Caret.Offset);
            var el = item.Elements[elIdx];

            if (el.Type == ContentElementType.Text)
            {
                if (localOff < el.Text.Length)
                {
                    el.Text = el.Text.Remove(localOff, 1);
                    if (el.Text.Length == 0) item.Elements.RemoveAt(elIdx);
                }
            }
            else
            {
                item.Elements.RemoveAt(elIdx);
            }
        }
        else if (Caret.ItemIndex < Document.Items.Count - 1)
        {
            var nextItem = Document.Items[Caret.ItemIndex + 1];
            foreach (var el in nextItem.Elements)
                item.Elements.Add(el);
            Document.Items.RemoveAt(Caret.ItemIndex + 1);
        }

        ClearSelectionNoDelete();
        _fullLayoutRequired = true;
        InvalidateMeasure();
        NotifyDocumentChanged();
    }

    public void DeleteSelection()
    {
        if (!HasSelection) return;

        var (first, last) = CurrentSelection.Ordered();

        if (first.ItemIndex == last.ItemIndex)
        {
            var item = Document.Items[first.ItemIndex];
            DeleteRange(item, first.Offset, last.Offset);
        }
        else
        {
            var firstItem = Document.Items[first.ItemIndex];
            var lastItem = Document.Items[last.ItemIndex];

            DeleteRange(firstItem, first.Offset, firstItem.TextLength);

            var remainingElements = new List<ContentElement>();
            CollectRange(lastItem, last.Offset, lastItem.TextLength, remainingElements);
            DeleteRange(lastItem, 0, lastItem.TextLength);

            for (int i = last.ItemIndex; i > first.ItemIndex; i--)
                Document.Items.RemoveAt(i);

            foreach (var el in remainingElements)
                firstItem.Elements.Add(el);
        }

        Caret = first;
        ClearSelectionNoDelete();
        EnsureNonEmpty();
        _fullLayoutRequired = true;
        InvalidateMeasure();
        NotifyDocumentChanged();
    }

    private void DeleteRange(NoteItem item, int fromOffset, int toOffset)
    {
        if (fromOffset >= toOffset) return;

        int pos = 0;
        for (int i = item.Elements.Count - 1; i >= 0; i--)
        {
            pos = item.GlobalOffset(i, 0);
            var el = item.Elements[i];
            int elLen = el.Type == ContentElementType.Text ? el.Text.Length : 1;
            int elStart = pos;
            int elEnd = pos + elLen;

            if (elEnd <= fromOffset || elStart >= toOffset) continue;

            int delStart = Math.Max(fromOffset - elStart, 0);
            int delEnd = Math.Min(toOffset - elStart, elLen);

            if (el.Type == ContentElementType.Text)
            {
                el.Text = el.Text[..delStart] + el.Text[delEnd..];
                if (el.Text.Length == 0) item.Elements.RemoveAt(i);
            }
            else
            {
                item.Elements.RemoveAt(i);
            }
        }
    }

    private void CollectRange(NoteItem item, int fromOffset, int toOffset, List<ContentElement> result)
    {
        int pos = 0;
        foreach (var el in item.Elements)
        {
            int elLen = el.Type == ContentElementType.Text ? el.Text.Length : 1;
            int elStart = pos;
            int elEnd = pos + elLen;

            if (elEnd <= fromOffset) { pos += elLen; continue; }
            if (elStart >= toOffset) break;

            int colStart = Math.Max(fromOffset - elStart, 0);
            int colEnd = Math.Min(toOffset - elStart, elLen);

            if (el.Type == ContentElementType.Text)
            {
                var sub = el.Text[colStart..colEnd];
                if (sub.Length > 0)
                    result.Add(ContentElement.CreateText(sub, el.Font, el.FontSize));
            }
            else
            {
                result.Add(el.Clone());
            }

            pos += elLen;
        }
    }

    // ---- Selection ----

    private void SelectWordAtCaret()
    {
        var item = Document.Items[Caret.ItemIndex];
        if (item.TextLength == 0) return;

        int offset = Math.Min(Caret.Offset, item.TextLength - 1);
        var cls = item.ClassifyAt(offset);
        if (cls == CharClass.Image)
        {
            SelectionAnchor = new CursorPosition(Caret.ItemIndex, offset);
            Caret = new CursorPosition(Caret.ItemIndex, offset + 1);
            return;
        }

        int left = offset;
        while (left > 0 && item.ClassifyAt(left - 1) == cls)
            left--;
        int right = offset;
        while (right < item.TextLength && item.ClassifyAt(right) == cls)
            right++;

        SelectionAnchor = new CursorPosition(Caret.ItemIndex, left);
        Caret = new CursorPosition(Caret.ItemIndex, right);
    }

    public void SelectAll()
    {
        SelectionAnchor = CursorPosition.Start;
        var lastItem = Document.Items[^1];
        Caret = new CursorPosition(Document.Items.Count - 1, lastItem.TextLength);
        InvalidateVisual();
    }

    private void ClearSelectionNoDelete()
    {
        SelectionAnchor = Caret;
    }

    public string GetSelectedText()
    {
        if (!HasSelection) return string.Empty;

        var (first, last) = CurrentSelection.Ordered();
        var sb = new StringBuilder();

        for (int i = first.ItemIndex; i <= last.ItemIndex; i++)
        {
            if (i > first.ItemIndex) sb.AppendLine();
            var item = Document.Items[i];
            int start = i == first.ItemIndex ? first.Offset : 0;
            int end = i == last.ItemIndex ? last.Offset : item.TextLength;

            int pos = 0;
            foreach (var el in item.Elements)
            {
                int elLen = el.Type == ContentElementType.Text ? el.Text.Length : 1;
                int elStart = pos;
                int elEnd = pos + elLen;

                if (elEnd <= start) { pos += elLen; continue; }
                if (elStart >= end) break;

                int colStart = Math.Max(start - elStart, 0);
                int colEnd = Math.Min(end - elStart, elLen);

                if (el.Type == ContentElementType.Text)
                    sb.Append(el.Text[colStart..colEnd]);
                else
                    sb.Append('￼');

                pos += elLen;
            }
        }

        return sb.ToString();
    }

    // ---- Clipboard ----

    private List<List<ContentElement>> CollectSelectedElements()
    {
        var result = new List<List<ContentElement>>();
        if (!HasSelection) return result;

        var (first, last) = CurrentSelection.Ordered();

        for (int i = first.ItemIndex; i <= last.ItemIndex; i++)
        {
            var item = Document.Items[i];
            int start = i == first.ItemIndex ? first.Offset : 0;
            int end = i == last.ItemIndex ? last.Offset : item.TextLength;

            var lineElements = new List<ContentElement>();
            CollectRange(item, start, end, lineElements);
            result.Add(lineElements);
        }

        return result;
    }

    public void CopyToClipboard()
    {
        if (HasSelection)
        {
            _internalClipboard = CollectSelectedElements();
            _internalClipboardText = GetSelectedText();
        }
        else
        {
            _internalClipboard = null;
            _internalClipboardText = null;
        }

        var text = HasSelection ? GetSelectedText() : Document.GetAllText();
        if (TopLevel.GetTopLevel(this) is { Clipboard: { } clipboard })
            clipboard.SetTextAsync(text);
    }

    public async System.Threading.Tasks.Task PasteFromClipboard()
    {
        if (TopLevel.GetTopLevel(this) is not { Clipboard: { } clipboard }) return;

        var sysText = await clipboard.TryGetTextAsync();

        if (_internalClipboard != null && _internalClipboardText != null
            && sysText == _internalClipboardText)
        {
            PasteRichContent(_internalClipboard);
            return;
        }

        var image = await clipboard.TryGetBitmapAsync();
        if (image is Bitmap bmp)
        {
            InsertImageAtCaret(bmp);
            return;
        }
        else if (image != null)
        {
            using var ms = new MemoryStream();
            image.Save(ms);
            ms.Position = 0;
            InsertImageAtCaret(new Bitmap(ms));
            return;
        }

        if (!string.IsNullOrEmpty(sysText))
            PasteMultilineText(sysText);
    }

    public void PasteRichFromInternalClipboard()
    {
        if (_internalClipboard != null)
            PasteRichContent(_internalClipboard);
    }

    private void PasteRichContent(List<List<ContentElement>> lines)
    {
        SaveUndoState();
        using (SuppressSync())
        {
            DeleteSelection();
            for (int i = 0; i < lines.Count; i++)
            {
                foreach (var el in lines[i])
                {
                    if (el.Type == ContentElementType.Image && el.Image != null)
                        InsertImageAtCaretCore(el.Image);
                    else if (el.Type == ContentElementType.Text && el.Text.Length > 0)
                        InsertTextAtCaret(el.Text);
                }
                if (i < lines.Count - 1)
                    SplitItemAtCaret();
            }
        }
        NotifyDocumentChanged();
    }

    public void PasteMultilineText(string text)
    {
        SaveUndoState();
        using (SuppressSync())
        {
            DeleteSelection();
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length > 0)
                    InsertTextAtCaret(line);
                if (i < lines.Length - 1)
                    SplitItemAtCaret();
            }
        }
        NotifyDocumentChanged();
    }

    // ---- Caret movement ----

    private void MoveCaret(int direction, bool extend)
    {
        EnsureValidCaret();
        var newPos = Caret;

        if (direction < 0)
        {
            if (Caret.Offset > 0)
                newPos = new CursorPosition(Caret.ItemIndex, Caret.Offset - 1);
            else if (Caret.ItemIndex > 0)
            {
                var prev = Document.Items[Caret.ItemIndex - 1];
                newPos = new CursorPosition(Caret.ItemIndex - 1, prev.TextLength);
            }
        }
        else
        {
            var item = Document.Items[Caret.ItemIndex];
            if (Caret.Offset < item.TextLength)
                newPos = new CursorPosition(Caret.ItemIndex, Caret.Offset + 1);
            else if (Caret.ItemIndex < Document.Items.Count - 1)
                newPos = new CursorPosition(Caret.ItemIndex + 1, 0);
        }

        Caret = newPos;
        if (!extend) SelectionAnchor = Caret;
        InvalidateVisual();
    }

    internal void MoveCaretByWord(int direction, bool extend)
    {
        EnsureValidCaret();
        var item = Document.Items[Caret.ItemIndex];

        if (direction < 0)
        {
            int boundary = item.FindWordBoundaryLeft(Caret.Offset);
            if (boundary == Caret.Offset && Caret.Offset == 0 && Caret.ItemIndex > 0)
            {
                var prev = Document.Items[Caret.ItemIndex - 1];
                Caret = new CursorPosition(Caret.ItemIndex - 1, prev.TextLength);
            }
            else
            {
                Caret = new CursorPosition(Caret.ItemIndex, boundary);
            }
        }
        else
        {
            int boundary = item.FindWordBoundaryRight(Caret.Offset);
            if (boundary == Caret.Offset && Caret.Offset == item.TextLength
                && Caret.ItemIndex < Document.Items.Count - 1)
            {
                Caret = new CursorPosition(Caret.ItemIndex + 1, 0);
            }
            else
            {
                Caret = new CursorPosition(Caret.ItemIndex, boundary);
            }
        }

        if (!extend) SelectionAnchor = Caret;
        InvalidateVisual();
    }

    private void DeleteWordBackward()
    {
        EnsureValidCaret();
        var item = Document.Items[Caret.ItemIndex];

        if (Caret.Offset > 0)
        {
            int boundary = item.FindWordBoundaryLeft(Caret.Offset);
            int offset = Math.Min(Caret.Offset, item.TextLength);
            DeleteRange(item, boundary, offset);
            Caret = new CursorPosition(Caret.ItemIndex, boundary);
        }
        else if (Caret.ItemIndex > 0)
        {
            var prevItem = Document.Items[Caret.ItemIndex - 1];
            int prevLen = prevItem.TextLength;
            foreach (var el in item.Elements)
                prevItem.Elements.Add(el);
            Document.Items.RemoveAt(Caret.ItemIndex);
            Caret = new CursorPosition(Caret.ItemIndex - 1, prevLen);
        }

        ClearSelectionNoDelete();
        _fullLayoutRequired = true;
        InvalidateMeasure();
        NotifyDocumentChanged();
    }

    private void DeleteWordForward()
    {
        EnsureValidCaret();
        var item = Document.Items[Caret.ItemIndex];

        if (Caret.Offset < item.TextLength)
        {
            int boundary = item.FindWordBoundaryRight(Caret.Offset);
            DeleteRange(item, Caret.Offset, boundary);
        }
        else if (Caret.ItemIndex < Document.Items.Count - 1)
        {
            var nextItem = Document.Items[Caret.ItemIndex + 1];
            foreach (var el in nextItem.Elements)
                item.Elements.Add(el);
            Document.Items.RemoveAt(Caret.ItemIndex + 1);
        }

        ClearSelectionNoDelete();
        _fullLayoutRequired = true;
        InvalidateMeasure();
        NotifyDocumentChanged();
    }

    internal void MoveCaretVertical(int direction, bool extend)
    {
        EnsureValidCaret();
        int itemIdx = Caret.ItemIndex;

        if (_goalX < 0)
        {
            var (cx, _, _) = CalculateCaretPosition(itemIdx, Caret.Offset);
            _goalX = cx;
        }

        double useX = _goalX;

        if (itemIdx < _itemWrapping.Count)
        {
            var wrappedLines = _itemWrapping[itemIdx];

            int currentLineIdx = FindCurrentWrappedLine(wrappedLines, Caret.Offset, direction);

            int targetLineIdx = currentLineIdx + direction;

            if (targetLineIdx >= 0 && targetLineIdx < wrappedLines.Count)
            {
                int newOffset = HitTestLineOffset(
                    Document.Items[itemIdx], wrappedLines[targetLineIdx], useX);
                Caret = new CursorPosition(itemIdx, newOffset);
                if (!extend) SelectionAnchor = Caret;
                InvalidateVisual();
                return;
            }

            if (direction < 0 && itemIdx > 0)
            {
                int prevIdx = itemIdx - 1;
                if (prevIdx < _itemWrapping.Count && _itemWrapping[prevIdx].Count > 0)
                {
                    int newOffset = HitTestLineOffset(
                        Document.Items[prevIdx], _itemWrapping[prevIdx][^1], useX);
                    Caret = new CursorPosition(prevIdx, newOffset);
                }
                else
                {
                    Caret = new CursorPosition(prevIdx, Document.Items[prevIdx].TextLength);
                }
            }
            else if (direction < 0 && itemIdx == 0 && Caret.Offset > 0)
            {
                Caret = new CursorPosition(0, 0);
            }
            else if (direction > 0 && itemIdx < Document.Items.Count - 1)
            {
                int nextIdx = itemIdx + 1;
                if (nextIdx < _itemWrapping.Count && _itemWrapping[nextIdx].Count > 0)
                {
                    int newOffset = HitTestLineOffset(
                        Document.Items[nextIdx], _itemWrapping[nextIdx][0], useX);
                    Caret = new CursorPosition(nextIdx, newOffset);
                }
                else
                {
                    Caret = new CursorPosition(nextIdx, 0);
                }
            }
            else if (direction > 0 && itemIdx == Document.Items.Count - 1)
            {
                int endOffset = Document.Items[itemIdx].TextLength;
                if (Caret.Offset < endOffset)
                    Caret = new CursorPosition(itemIdx, endOffset);
            }
        }
        else
        {
            int newItem = Caret.ItemIndex + direction;
            newItem = Math.Clamp(newItem, 0, Document.Items.Count - 1);
            int offset = Math.Min(Caret.Offset, Document.Items[newItem].TextLength);
            Caret = new CursorPosition(newItem, offset);
        }

        if (!extend) SelectionAnchor = Caret;
        InvalidateVisual();
    }

    private static int FindCurrentWrappedLine(List<WrappedLine> wrappedLines, int offset, int direction)
    {
        for (int li = 0; li < wrappedLines.Count; li++)
        {
            int end = wrappedLines[li].EndGlobalOffset;
            if (offset < end)
                return li;
            if (offset == end)
            {
                if (direction > 0 && li + 1 < wrappedLines.Count)
                    return li + 1;
                return li;
            }
        }
        return wrappedLines.Count - 1;
    }

    // ---- Undo / Redo ----

    private UndoSnapshot CaptureSnapshot()
    {
        var items = new List<List<ContentElement>>();
        foreach (var item in Document.Items)
        {
            var elements = new List<ContentElement>();
            foreach (var el in item.Elements) elements.Add(el.Clone());
            items.Add(elements);
        }
        return new UndoSnapshot(items, Caret, SelectionAnchor);
    }

    private void RestoreSnapshot(UndoSnapshot snapshot)
    {
        using (SuppressSync())
        {
            Document.Items.Clear();
            foreach (var elements in snapshot.Items)
            {
                var item = new NoteItem();
                foreach (var el in elements) item.Elements.Add(el.Clone());
                Document.Items.Add(item);
            }
            if (Document.Items.Count == 0)
                Document.Items.Add(new NoteItem());

            Caret = ClampCursorPosition(snapshot.Caret);
            SelectionAnchor = ClampCursorPosition(snapshot.Anchor);
        }
        _fullLayoutRequired = true;
        InvalidateMeasure();
        NotifyDocumentChanged(ChangeKind.StructureChanged);
    }

    internal void SaveUndoState(UndoActionKind action = UndoActionKind.Other)
    {
        long now = Environment.TickCount64;
        bool coalesce = action != UndoActionKind.Other
            && action == _lastUndoAction
            && (now - _lastUndoTimestamp) < CoalesceThresholdMs
            && _undoStack.Count > 0;

        _lastUndoAction = action;
        _lastUndoTimestamp = now;

        if (coalesce) { _redoStack.Clear(); return; }

        _undoStack.Add(CaptureSnapshot());
        if (_undoStack.Count > MaxUndoSteps)
            _undoStack.RemoveAt(0);
        _redoStack.Clear();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Add(CaptureSnapshot());
        var snapshot = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RestoreSnapshot(snapshot);
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Add(CaptureSnapshot());
        var snapshot = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        RestoreSnapshot(snapshot);
    }

    // ---- Helpers ----

    private CursorPosition ClampCursorPosition(CursorPosition pos)
    {
        int maxItem = Document.Items.Count - 1;
        int itemIdx = Math.Clamp(pos.ItemIndex, 0, maxItem);
        int offset = Math.Clamp(pos.Offset, 0, Document.Items[itemIdx].TextLength);
        return new CursorPosition(itemIdx, offset);
    }

    private void EnsureValidCaret()
    {
        EnsureNonEmpty();
        if (Caret.ItemIndex >= Document.Items.Count)
            Caret = new CursorPosition(Document.Items.Count - 1, 0);
        if (Caret.ItemIndex < 0)
            Caret = CursorPosition.Start;
    }

    private void EnsureNonEmpty()
    {
        if (Document.Items.Count == 0)
            Document.Items.Add(new NoteItem());
    }

    // ---- MVVM synchronization ----

    private void NotifyDocumentChanged(ChangeKind kind = ChangeKind.TextChanged)
    {
        if (_suppressSyncCount == 0)
        {
            _changeGeneration++;
            SyncToItems(kind);
            UpdateDirtyState();
        }
    }

    private void OnItemsPropertyChanged(IList<NoteItemData>? oldItems, IList<NoteItemData>? newItems)
    {
        UnsubscribeItems(oldItems);

        if (newItems is INotifyCollectionChanged newNcc)
            newNcc.CollectionChanged += OnItemsCollectionChanged;
        if (newItems != null)
            foreach (var item in newItems)
                item.PropertyChanged += OnItemDataPropertyChanged;

        ResetDocumentFromItems();
        SyncMarkdownTextFromItems();
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressSyncCount > 0) return;

        if (e.OldItems != null)
            foreach (NoteItemData item in e.OldItems)
                item.PropertyChanged -= OnItemDataPropertyChanged;
        if (e.NewItems != null)
            foreach (NoteItemData item in e.NewItems)
                item.PropertyChanged += OnItemDataPropertyChanged;

        ApplyItemsCollectionChange();
    }

    private void ApplyItemsCollectionChange()
    {
        LoadDocumentFromItems(isFullLoad: false);
        SyncMarkdownTextFromItems();
    }

    private void OnItemDataPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSyncCount > 0 || sender is not NoteItemData data) return;

        var items = Items;
        if (items == null) return;

        int idx = -1;
        for (int i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], data)) { idx = i; break; }
        }
        if (idx < 0 || idx >= Document.Items.Count) return;

        using (SuppressSync())
        {
            if (e.PropertyName == nameof(NoteItemData.Text))
            {
                var newItem = ParseTextToItem(data.Text);
                Document.Items[idx] = newItem;
            }
        }

        _changeGeneration++;
        UpdateDirtyState();
        InvalidateItem(idx);
        SyncMarkdownTextFromItems();
    }

    private void ResetDocumentFromItems()
    {
        LoadDocumentFromItems(isFullLoad: true);
    }

    private void LoadDocumentFromItems(bool isFullLoad)
    {
        var items = Items;
        if (items == null) return;

        if (isFullLoad)
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }

        using (SuppressSync())
        {
            Document.Items.Clear();

            foreach (var data in items)
            {
                var item = ParseTextToItem(data.Text);
                Document.Items.Add(item);
            }

            if (Document.Items.Count == 0)
                Document.Items.Add(new NoteItem());

            Caret = ClampCursorPosition(Caret);
            SelectionAnchor = ClampCursorPosition(SelectionAnchor);
        }

        if (isFullLoad)
        {
            _fullLayoutRequired = true;
            InvalidateMeasure();
            MarkClean();
        }
        else
        {
            _changeGeneration++;
            UpdateDirtyState();
            _fullLayoutRequired = true;
            InvalidateMeasure();
        }
    }

    private void SyncToItems(ChangeKind kind = ChangeKind.TextChanged)
    {
        var items = Items;
        if (items == null || _suppressSyncCount > 0) return;

        using (SuppressSync())
        {
            for (int i = 0; i < Document.Items.Count; i++)
            {
                var text = SerializeItemToText(Document.Items[i]);

                if (i < items.Count)
                {
                    if (items[i].Text != text) items[i].Text = text;
                }
                else
                {
                    var newData = new NoteItemData(text);
                    newData.PropertyChanged += OnItemDataPropertyChanged;
                    items.Add(newData);
                }
            }

            while (items.Count > Document.Items.Count)
            {
                items[items.Count - 1].PropertyChanged -= OnItemDataPropertyChanged;
                items.RemoveAt(items.Count - 1);
            }
        }
        SyncMarkdownTextFromItems();
        ContentChanged?.Invoke(this, EventArgs.Empty);
        ContentDetailChanged?.Invoke(this, new ContentChangedEventArgs(kind));
    }

    // ---- Images collection ----

    private void OnImagesPropertyChanged(IEnumerable<ImageEntry>? oldImages, IEnumerable<ImageEntry>? newImages)
    {
        UnsubscribeImages(oldImages);
        RebuildImageCache();
        SubscribeImages(newImages);
        RefreshAllImageElements();
    }

    private void SubscribeImages(IEnumerable<ImageEntry>? images)
    {
        if (images is INotifyCollectionChanged ncc)
            ncc.CollectionChanged += OnImagesCollectionChanged;
        if (images != null)
            foreach (var entry in images)
                SubscribeImageEntry(entry);
    }

    private void UnsubscribeImages(IEnumerable<ImageEntry>? images)
    {
        UnsubscribeAllImageEntries();
        if (images is INotifyCollectionChanged ncc)
            ncc.CollectionChanged -= OnImagesCollectionChanged;
    }

    private void SubscribeImageEntry(ImageEntry entry)
    {
        entry.PropertyChanged += OnImageEntryPropertyChanged;
        _subscribedImageEntries.Add(entry);
    }

    private void UnsubscribeAllImageEntries()
    {
        foreach (var entry in _subscribedImageEntries)
            entry.PropertyChanged -= OnImageEntryPropertyChanged;
        _subscribedImageEntries.Clear();
    }

    private void RebuildImageCache()
    {
        _imageCache.Clear();
        var images = Images;
        if (images == null) return;
        foreach (var entry in images)
        {
            if (entry.Bitmap != null)
                _imageCache[entry.Key] = entry.Bitmap;
        }
    }

    private void SyncLegacyImageStoreFromCache()
    {
        _legacyImageStore.Clear();
        foreach (var kvp in _imageCache)
            _legacyImageStore[kvp.Key] = kvp.Value;
    }

    private void OnImagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            UnsubscribeAllImageEntries();
            if (Images != null)
                foreach (var entry in Images)
                    SubscribeImageEntry(entry);
            RebuildImageCache();
            SyncLegacyImageStoreFromCache();
            RefreshAllImageElements();
            return;
        }

        if (e.OldItems != null)
            foreach (ImageEntry entry in e.OldItems)
            {
                entry.PropertyChanged -= OnImageEntryPropertyChanged;
                _subscribedImageEntries.Remove(entry);
                _legacyImageStore.Remove(entry.Key);
            }
        if (e.NewItems != null)
            foreach (ImageEntry entry in e.NewItems)
            {
                SubscribeImageEntry(entry);
                if (entry.Bitmap != null)
                    _legacyImageStore[entry.Key] = entry.Bitmap;
            }

        RebuildImageCache();
        RefreshAllImageElements();
    }

    private void OnImageEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ImageEntry entry) return;

        if (e.PropertyName == nameof(ImageEntry.Bitmap))
        {
            if (entry.Bitmap != null)
                _imageCache[entry.Key] = entry.Bitmap;
            else
                _imageCache.Remove(entry.Key);

            RefreshImageElementsForKey(entry.Key);
        }
        else if (e.PropertyName == nameof(ImageEntry.Key))
        {
            RebuildImageCache();
            SyncLegacyImageStoreFromCache();
            RefreshAllImageElements();
        }
    }

    private Bitmap? ResolveImageByKey(string key)
    {
        if (_imageCache.TryGetValue(key, out var cached)) return cached;
        if (_legacyImageStore.TryGetValue(key, out var legacy)) return legacy;
        return null;
    }

    private void RefreshAllImageElements()
    {
        bool changed = false;
        using (SuppressSync())
        {
            for (int i = 0; i < Document.Items.Count; i++)
                changed |= RefreshItemImages(Document.Items[i]);
        }
        if (changed) { _fullLayoutRequired = true; InvalidateMeasure(); }
    }

    private void RefreshImageElementsForKey(string key)
    {
        bool changed = false;
        using (SuppressSync())
        {
            for (int i = 0; i < Document.Items.Count; i++)
                changed |= RefreshItemImages(Document.Items[i], key);
        }
        if (changed) { _fullLayoutRequired = true; InvalidateMeasure(); }
    }

    private bool RefreshItemImages(NoteItem item, string? filterKey = null)
    {
        bool changed = false;
        for (int j = 0; j < item.Elements.Count; j++)
        {
            var el = item.Elements[j];
            if (el.UnresolvedImageKey != null && (filterKey == null || el.UnresolvedImageKey == filterKey))
            {
                var bitmap = ResolveImageByKey(el.UnresolvedImageKey);
                if (bitmap != null)
                {
                    var imgEl = ContentElement.CreateImage(bitmap);
                    imgEl.ImageKey = el.UnresolvedImageKey;
                    imgEl.ImageAltText = el.ImageAltText;
                    item.Elements[j] = imgEl;
                    changed = true;
                }
            }
            else if (el.Type == ContentElementType.Image && el.ImageKey != null
                     && (filterKey == null || el.ImageKey == filterKey))
            {
                var bitmap = ResolveImageByKey(el.ImageKey);
                if (bitmap != null && !ReferenceEquals(el.Image, bitmap))
                {
                    el.Image = bitmap;
                    el.ImageWidth = bitmap.PixelSize.Width;
                    el.ImageHeight = bitmap.PixelSize.Height;
                    changed = true;
                }
            }
        }
        return changed;
    }

    // ---- IsDirty ----

    private void SetDirtyState(bool dirty)
    {
        if (SetAndRaise(IsDirtyProperty, ref _isDirty, dirty))
            DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateDirtyState()
    {
        SetDirtyState(_changeGeneration != _cleanGeneration);
    }

    public void MarkClean()
    {
        _cleanGeneration = _changeGeneration;
        SetDirtyState(false);
    }

    // ---- Text parsing ----

    public NoteItem ParseTextToItem(string text)
    {
        var item = new NoteItem();
        int lastEnd = 0;

        foreach (Match match in ImagePattern.Matches(text))
        {
            if (match.Index > lastEnd)
                item.Elements.Add(ContentElement.CreateText(text[lastEnd..match.Index]));

            string altText = match.Groups[1].Value;
            string key = match.Groups[2].Value;

            var bitmap = ResolveImageByKey(key);
            if (bitmap != null)
            {
                var imgEl = ContentElement.CreateImage(bitmap);
                imgEl.ImageKey = key;
                imgEl.ImageAltText = altText;
                item.Elements.Add(imgEl);
            }
            else
            {
                var placeholder = ContentElement.CreateText(match.Value);
                placeholder.UnresolvedImageKey = key;
                placeholder.ImageAltText = altText;
                item.Elements.Add(placeholder);
            }

            lastEnd = match.Index + match.Length;
        }

        if (lastEnd < text.Length)
            item.Elements.Add(ContentElement.CreateText(text[lastEnd..]));

        return item;
    }

    internal string SerializeItemToText(NoteItem item)
    {
        var sb = new StringBuilder();
        foreach (var el in item.Elements)
        {
            if (el.Type == ContentElementType.Image)
            {
                string key = el.ImageKey ?? GetOrCreateImageKey(el.Image);
                string alt = el.ImageAltText ?? "image";
                sb.Append($"![{alt}]({key})");
            }
            else if (el.UnresolvedImageKey != null)
            {
                string alt = el.ImageAltText ?? "image";
                sb.Append($"![{alt}]({el.UnresolvedImageKey})");
            }
            else
            {
                sb.Append(el.Text);
            }
        }
        return sb.ToString();
    }

    private string GetOrCreateImageKey(Bitmap? bitmap)
    {
        if (bitmap == null) return "unknown";

        foreach (var kvp in _imageCache)
        {
            if (ReferenceEquals(kvp.Value, bitmap)) return kvp.Key;
        }

        foreach (var kvp in _legacyImageStore)
        {
            if (ReferenceEquals(kvp.Value, bitmap)) return kvp.Key;
        }

        string key = $"img_{Guid.NewGuid():N}";
        _imageCache[key] = bitmap;
        return key;
    }

    // ---- Public API ----

    public string GetText() => Document.GetAllText();

    public void SetText(string text)
    {
        _undoStack.Clear();
        _redoStack.Clear();

        using (SuppressSync())
        {
            Document.Items.Clear();
            foreach (var line in text.Split('\n'))
                Document.Items.Add(new NoteItem(line.TrimEnd('\r')));
            if (Document.Items.Count == 0)
                Document.Items.Add(new NoteItem());
            Caret = CursorPosition.Start;
            SelectionAnchor = CursorPosition.Start;
        }
        _fullLayoutRequired = true;
        InvalidateMeasure();
        NotifyDocumentChanged(ChangeKind.StructureChanged);
    }

    public void AddItem(string text)
    {
        Document.Items.Add(new NoteItem(text));
        _fullLayoutRequired = true;
        InvalidateMeasure();
        NotifyDocumentChanged(ChangeKind.StructureChanged);
    }
}
