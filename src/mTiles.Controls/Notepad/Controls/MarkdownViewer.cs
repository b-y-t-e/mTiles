using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Input.Platform;
using global::Avalonia.Interactivity;
using global::Avalonia.Media;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Threading;
using Notepad.Avalonia.Model;

namespace Notepad.Avalonia.Controls;

/// <summary>
/// Read-only markdown renderer with a built-in vertical scrollbar, free text
/// selection (mouse drag, Shift+click, double/triple click, Ctrl+A) and copy.
/// </summary>
/// <remarks>
/// Like <see cref="NoteEditor"/> the viewer scrolls its own content and
/// virtualizes rendering, so give it a finite height and do NOT wrap it in an
/// external <c>ScrollViewer</c>.
/// </remarks>
public class MarkdownViewer : Control
{
    // ---- Properties ----

    public static readonly StyledProperty<string?> MarkdownTextProperty =
        AvaloniaProperty.Register<MarkdownViewer, string?>(nameof(MarkdownText));

    public static readonly StyledProperty<IEnumerable<ImageEntry>?> ImagesProperty =
        AvaloniaProperty.Register<MarkdownViewer, IEnumerable<ImageEntry>?>(nameof(Images));

    public static readonly StyledProperty<Func<string, Bitmap?>?> ImageResolverProperty =
        AvaloniaProperty.Register<MarkdownViewer, Func<string, Bitmap?>?>(nameof(ImageResolver));

    public static readonly StyledProperty<EditorTheme> ColorThemeProperty =
        AvaloniaProperty.Register<MarkdownViewer, EditorTheme>(nameof(ColorTheme), EditorTheme.Light);

    public static readonly StyledProperty<FontFamily> DefaultFontProperty =
        AvaloniaProperty.Register<MarkdownViewer, FontFamily>(nameof(DefaultFont), FontFamily.Default);

    public static readonly StyledProperty<double> DefaultFontSizeProperty =
        AvaloniaProperty.Register<MarkdownViewer, double>(nameof(DefaultFontSize), 14.0);

    public static readonly StyledProperty<FontFamily> CodeFontProperty =
        AvaloniaProperty.Register<MarkdownViewer, FontFamily>(nameof(CodeFont),
            new FontFamily("Consolas,Menlo,DejaVu Sans Mono,monospace"));

    public static readonly StyledProperty<IBrush> BackgroundBrushProperty =
        AvaloniaProperty.Register<MarkdownViewer, IBrush>(nameof(BackgroundBrush), Brushes.White);

    public static readonly StyledProperty<IBrush> ForegroundProperty =
        AvaloniaProperty.Register<MarkdownViewer, IBrush>(nameof(Foreground), Brushes.Black);

    public static readonly StyledProperty<IBrush> MutedBrushProperty =
        AvaloniaProperty.Register<MarkdownViewer, IBrush>(nameof(MutedBrush),
            new SolidColorBrush(Color.FromRgb(110, 118, 129)));

    public static readonly StyledProperty<IBrush> LinkBrushProperty =
        AvaloniaProperty.Register<MarkdownViewer, IBrush>(nameof(LinkBrush),
            new SolidColorBrush(Color.FromRgb(9, 105, 218)));

    /// <summary>The size of a code block's text, as a share of <see cref="DefaultFontSize"/>.</summary>
    /// <remarks>0.92 is what this control has always used and stays the default, so no document renders
    /// differently for this existing. A host whose whole surface is already set in the code face — a
    /// transcript of an agent's work, where the prose and the patch are the same kind of thing — sets
    /// it to 1 and gets one size on the screen instead of two that differ by eight per cent, which is
    /// too little to read as a decision and enough to see.</remarks>
    public static readonly StyledProperty<double> CodeFontScaleProperty =
        AvaloniaProperty.Register<MarkdownViewer, double>(nameof(CodeFontScale), 0.92);

    public static readonly StyledProperty<IBrush> CodeBackgroundProperty =
        AvaloniaProperty.Register<MarkdownViewer, IBrush>(nameof(CodeBackground),
            new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)));

    /// <summary>
    /// Colours a <c>diff</c> code block line by line: what was added, what was removed, and the
    /// format's own lines.
    /// </summary>
    /// <remarks>
    /// <para>Off unless it is asked for. A code block is rendered exactly as it was before this
    /// existed, and a host that wants a patch to read as one says so — which is also what keeps this
    /// from being a syntax highlighter by the back door: the rule is the one character at the head of
    /// the line that the diff format itself puts there, and nothing here tokenizes anything.</para>
    /// <para>The language is the fence's own word. Only <c>diff</c> and <c>patch</c> are read, so a
    /// block fenced as anything else — or as nothing — is left alone whatever this is set to.</para>
    /// </remarks>
    public static readonly StyledProperty<bool> HighlightDiffProperty =
        AvaloniaProperty.Register<MarkdownViewer, bool>(nameof(HighlightDiff));

    /// <summary>The ground behind an added line, the whole width of the block.</summary>
    /// <remarks>A ground rather than coloured text, and both rather than either: on a block of twenty
    /// added lines, green text is a paragraph of green and the eye reads the colour instead of the
    /// code. The band is what says which lines, and it runs the width of the block because a
    /// background that stops where the text stops leaves the block a ragged right edge.</remarks>
    public static readonly StyledProperty<IBrush?> DiffAddedBackgroundProperty =
        AvaloniaProperty.Register<MarkdownViewer, IBrush?>(nameof(DiffAddedBackground),
            new SolidColorBrush(Color.FromArgb(28, 115, 201, 145)));

    /// <inheritdoc cref="DiffAddedBackgroundProperty"/>
    public static readonly StyledProperty<IBrush?> DiffRemovedBackgroundProperty =
        AvaloniaProperty.Register<MarkdownViewer, IBrush?>(nameof(DiffRemovedBackground),
            new SolidColorBrush(Color.FromArgb(28, 192, 104, 120)));

    /// <summary>The <c>+</c> at the head of an added line. Null leaves it the block's own colour.</summary>
    public static readonly StyledProperty<IBrush?> DiffAddedForegroundProperty =
        AvaloniaProperty.Register<MarkdownViewer, IBrush?>(nameof(DiffAddedForeground));

    /// <inheritdoc cref="DiffAddedForegroundProperty"/>
    public static readonly StyledProperty<IBrush?> DiffRemovedForegroundProperty =
        AvaloniaProperty.Register<MarkdownViewer, IBrush?>(nameof(DiffRemovedForeground));

    /// <summary>The lines that are the format rather than the file: <c>@@</c>, <c>diff --git</c>.</summary>
    public static readonly StyledProperty<IBrush?> DiffMetaForegroundProperty =
        AvaloniaProperty.Register<MarkdownViewer, IBrush?>(nameof(DiffMetaForeground));

    public static readonly StyledProperty<IBrush> RuleBrushProperty =
        AvaloniaProperty.Register<MarkdownViewer, IBrush>(nameof(RuleBrush),
            new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)));

    public static readonly StyledProperty<IBrush> QuoteBarBrushProperty =
        AvaloniaProperty.Register<MarkdownViewer, IBrush>(nameof(QuoteBarBrush),
            new SolidColorBrush(Color.FromRgb(208, 215, 222)));

    public static readonly StyledProperty<IBrush> SelectionBrushProperty =
        AvaloniaProperty.Register<MarkdownViewer, IBrush>(nameof(SelectionBrush),
            new SolidColorBrush(Color.FromArgb(80, 30, 144, 255)));

    public static readonly StyledProperty<IBrush> InactiveSelectionBrushProperty =
        AvaloniaProperty.Register<MarkdownViewer, IBrush>(nameof(InactiveSelectionBrush),
            new SolidColorBrush(Color.FromArgb(60, 130, 130, 130)));

    public static readonly StyledProperty<bool> ClearSelectionOnLostFocusProperty =
        AvaloniaProperty.Register<MarkdownViewer, bool>(nameof(ClearSelectionOnLostFocus), true);

    public static readonly StyledProperty<IBrush> ScrollTrackBrushProperty =
        AvaloniaProperty.Register<MarkdownViewer, IBrush>(nameof(ScrollTrackBrush),
            new SolidColorBrush(Color.FromArgb(24, 0, 0, 0)));

    public static readonly StyledProperty<IBrush> ScrollThumbBrushProperty =
        AvaloniaProperty.Register<MarkdownViewer, IBrush>(nameof(ScrollThumbBrush),
            new SolidColorBrush(Color.FromArgb(96, 0, 0, 0)));

    public static readonly StyledProperty<Thickness> ViewerPaddingProperty =
        AvaloniaProperty.Register<MarkdownViewer, Thickness>(nameof(ViewerPadding), new Thickness(12));

    public static readonly StyledProperty<double> ParagraphSpacingProperty =
        AvaloniaProperty.Register<MarkdownViewer, double>(nameof(ParagraphSpacing), 10.0);

    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<MarkdownViewer, double>(nameof(LineSpacing), 3.0);

    public static readonly StyledProperty<double> MaxImageHeightProperty =
        AvaloniaProperty.Register<MarkdownViewer, double>(nameof(MaxImageHeight), 320.0);

    public static readonly StyledProperty<bool> SoftLineBreaksProperty =
        AvaloniaProperty.Register<MarkdownViewer, bool>(nameof(SoftLineBreaks), true);

    public static readonly StyledProperty<bool> IsSelectionEnabledProperty =
        AvaloniaProperty.Register<MarkdownViewer, bool>(nameof(IsSelectionEnabled), true);

    public static readonly StyledProperty<bool> OpenLinksInBrowserProperty =
        AvaloniaProperty.Register<MarkdownViewer, bool>(nameof(OpenLinksInBrowser), true);

    /// <summary>Markdown source rendered by the viewer.</summary>
    public string? MarkdownText
    {
        get => GetValue(MarkdownTextProperty);
        set => SetValue(MarkdownTextProperty, value);
    }

    /// <summary>Images referenced by <c>![alt](key)</c>, keyed by <see cref="ImageEntry.Key"/>.</summary>
    public IEnumerable<ImageEntry>? Images
    {
        get => GetValue(ImagesProperty);
        set => SetValue(ImagesProperty, value);
    }

    /// <summary>Fallback used when a key is not present in <see cref="Images"/>.</summary>
    public Func<string, Bitmap?>? ImageResolver
    {
        get => GetValue(ImageResolverProperty);
        set => SetValue(ImageResolverProperty, value);
    }

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

    public FontFamily CodeFont
    {
        get => GetValue(CodeFontProperty);
        set => SetValue(CodeFontProperty, value);
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

    public IBrush MutedBrush
    {
        get => GetValue(MutedBrushProperty);
        set => SetValue(MutedBrushProperty, value);
    }

    public IBrush LinkBrush
    {
        get => GetValue(LinkBrushProperty);
        set => SetValue(LinkBrushProperty, value);
    }

    /// <inheritdoc cref="CodeFontScaleProperty"/>
    public double CodeFontScale
    {
        get => GetValue(CodeFontScaleProperty);
        set => SetValue(CodeFontScaleProperty, value);
    }

    public IBrush CodeBackground
    {
        get => GetValue(CodeBackgroundProperty);
        set => SetValue(CodeBackgroundProperty, value);
    }

    /// <inheritdoc cref="HighlightDiffProperty"/>
    public bool HighlightDiff
    {
        get => GetValue(HighlightDiffProperty);
        set => SetValue(HighlightDiffProperty, value);
    }

    /// <inheritdoc cref="DiffAddedBackgroundProperty"/>
    public IBrush? DiffAddedBackground
    {
        get => GetValue(DiffAddedBackgroundProperty);
        set => SetValue(DiffAddedBackgroundProperty, value);
    }

    /// <inheritdoc cref="DiffRemovedBackgroundProperty"/>
    public IBrush? DiffRemovedBackground
    {
        get => GetValue(DiffRemovedBackgroundProperty);
        set => SetValue(DiffRemovedBackgroundProperty, value);
    }

    /// <inheritdoc cref="DiffAddedForegroundProperty"/>
    public IBrush? DiffAddedForeground
    {
        get => GetValue(DiffAddedForegroundProperty);
        set => SetValue(DiffAddedForegroundProperty, value);
    }

    /// <inheritdoc cref="DiffRemovedForegroundProperty"/>
    public IBrush? DiffRemovedForeground
    {
        get => GetValue(DiffRemovedForegroundProperty);
        set => SetValue(DiffRemovedForegroundProperty, value);
    }

    /// <inheritdoc cref="DiffMetaForegroundProperty"/>
    public IBrush? DiffMetaForeground
    {
        get => GetValue(DiffMetaForegroundProperty);
        set => SetValue(DiffMetaForegroundProperty, value);
    }

    public IBrush RuleBrush
    {
        get => GetValue(RuleBrushProperty);
        set => SetValue(RuleBrushProperty, value);
    }

    public IBrush QuoteBarBrush
    {
        get => GetValue(QuoteBarBrushProperty);
        set => SetValue(QuoteBarBrushProperty, value);
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
    /// Drops the selection when the control loses focus, matching Avalonia's
    /// <c>TextBox</c> and <c>SelectableTextBlock</c>. Set to false to keep the
    /// selection, which is then drawn with <see cref="InactiveSelectionBrush"/>.
    /// </summary>
    public bool ClearSelectionOnLostFocus
    {
        get => GetValue(ClearSelectionOnLostFocusProperty);
        set => SetValue(ClearSelectionOnLostFocusProperty, value);
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

    public Thickness ViewerPadding
    {
        get => GetValue(ViewerPaddingProperty);
        set => SetValue(ViewerPaddingProperty, value);
    }

    public double ParagraphSpacing
    {
        get => GetValue(ParagraphSpacingProperty);
        set => SetValue(ParagraphSpacingProperty, value);
    }

    public double LineSpacing
    {
        get => GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    public double MaxImageHeight
    {
        get => GetValue(MaxImageHeightProperty);
        set => SetValue(MaxImageHeightProperty, value);
    }

    /// <summary>When true a single newline inside a paragraph renders as a line break.</summary>
    public bool SoftLineBreaks
    {
        get => GetValue(SoftLineBreaksProperty);
        set => SetValue(SoftLineBreaksProperty, value);
    }

    public bool IsSelectionEnabled
    {
        get => GetValue(IsSelectionEnabledProperty);
        set => SetValue(IsSelectionEnabledProperty, value);
    }

    /// <summary>Opens http/https/mailto links in the system browser when a click is unhandled.</summary>
    public bool OpenLinksInBrowser
    {
        get => GetValue(OpenLinksInBrowserProperty);
        set => SetValue(OpenLinksInBrowserProperty, value);
    }

    /// <summary>Raised when the user clicks a link. Set <see cref="LinkClickedEventArgs.Handled"/> to suppress the default action.</summary>
    public event EventHandler<LinkClickedEventArgs>? LinkClicked;

    /// <summary>Raised whenever the selection range changes.</summary>
    public event EventHandler? SelectionChanged;

    // ---- Layout model ----

    private sealed class VisualRun
    {
        public string Text = string.Empty;
        public int Start;
        public Typeface Typeface;
        public double FontSize;
        public IBrush Brush = Brushes.Black;
        public bool Underline;
        public bool Strike;
        public bool IsCode;
        public string? LinkUrl;
        public Bitmap? Image;
        public double X;
        public double Width;
        public double Height;
        public double Baseline;

        public int End => Start + Text.Length;
    }

    private sealed class VisualLine
    {
        public double Y;
        public double Height;
        public double Baseline;
        public int Start;
        public int End;
        public readonly List<VisualRun> Runs = new();
        public double Left;
        public double Right;

        /// <summary>A ground the whole width of the block, behind everything on this line.</summary>
        public IBrush? Band;
    }

    private sealed class VisualBlock
    {
        public MarkdownBlock Source = null!;
        public double Y;
        public double Height;
        public int Start;
        public int End;
        public readonly List<VisualLine> Lines = new();
        public double ContentLeft;
        public double ContentRight;
        public Rect? CheckBox;
        public bool CodeBox;
        public bool HeadingRule;
        public readonly List<(Rect Rect, bool Header)> Cells = new();
    }

    /// <summary>Width-independent run produced from the parsed inlines.</summary>
    private sealed class SourceRun
    {
        public string Text = string.Empty;
        public int Start;
        public bool Bold;
        public bool Italic;
        public bool Strike;
        public bool Code;
        public bool LineBreak;
        public bool Muted;
        public string? LinkUrl;
        public Bitmap? Image;
    }

    private sealed class SourceBlock
    {
        public MarkdownBlock Source = null!;
        public int Start;
        public int End;
        public readonly List<SourceRun> Marker = new();
        public readonly List<SourceRun> Runs = new();
        public List<List<List<SourceRun>>>? TableCells;
        public List<bool>? TableRowIsHeader;
    }

    // ---- State ----

    private const double ScrollBarWidth = 12;
    private const double MinThumbHeight = 30;
    private const double QuoteIndent = 16;
    private const double ListIndent = 22;
    private const double CodePadding = 8;
    private const double CellPaddingX = 8;
    private const double CellPaddingY = 4;
    private const int MaxCacheEntries = 4096;

    private readonly List<SourceBlock> _sourceBlocks = new();
    private readonly List<VisualBlock> _blocks = new();
    private string _plainText = string.Empty;

    private readonly Dictionary<string, Bitmap> _imageCache = new();
    private readonly List<ImageEntry> _subscribedImages = new();
    private readonly Dictionary<(string, Typeface, double), double> _measureCache = new();
    private readonly Dictionary<(string, Typeface, double, IBrush), FormattedText> _fmtCache = new();

    private bool _contentDirty = true;
    private bool _layoutDirty = true;
    private double _desiredWidth;
    private double _desiredHeight;
    private double _layoutWidth = -1;

    private double _offset;
    private double _viewportHeight;
    private bool _internalScroll;
    private bool _draggingThumb;
    private double _thumbDragStartY;
    private double _thumbDragStartOffset;
    private bool _scrollBarWasVisible;

    private int _selectionAnchor;
    private int _selectionStart;
    private int _selectionEnd;
    private bool _mouseSelecting;
    private readonly FocusSelectionBehavior _focusBehavior;
    private Point _pressPoint;
    private string? _pressedLink;
    private int _selectMode; // 0 = char, 1 = word, 2 = line
    private int _wordAnchorStart;
    private int _wordAnchorEnd;
    private DispatcherTimer? _autoScrollTimer;
    private double _autoScrollDelta;

    public MarkdownViewer()
    {
        // Created first: the property system can raise OnPropertyChanged while the
        // rest of the constructor runs (BuildContextMenu below does exactly that).
        _focusBehavior = new FocusSelectionBehavior(this,
            () => ClearSelectionOnLostFocus && HasSelection,
            ClearSelection);

        Focusable = true;
        IsTabStop = true;
        ClipToBounds = true;
        ApplyTheme(ColorTheme);
        BuildContextMenu();
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

    // ---- Public API ----

    /// <summary>Plain text of the rendered document; selection offsets index into it.</summary>
    public string PlainText
    {
        get
        {
            EnsureContent();
            return _plainText;
        }
    }

    /// <summary>Currently selected text, or an empty string.</summary>
    public string SelectedText
    {
        get
        {
            EnsureContent();
            if (_selectionEnd <= _selectionStart) return string.Empty;
            int start = Math.Clamp(_selectionStart, 0, _plainText.Length);
            int end = Math.Clamp(_selectionEnd, 0, _plainText.Length);
            return end > start ? _plainText[start..end] : string.Empty;
        }
    }

    public bool HasSelection => _selectionEnd > _selectionStart;

    public void SelectAll()
    {
        EnsureContent();
        SetSelection(0, _plainText.Length);
        _selectionAnchor = 0;
    }

    /// <summary>Drops the selection and the anchor a Shift+click would extend from.</summary>
    public void ClearSelection()
    {
        _selectionAnchor = 0;
        SetSelection(0, 0);
    }

    /// <summary>Copies the selection (or the whole document when nothing is selected) to the clipboard.</summary>
    public void CopySelection()
    {
        var text = HasSelection ? SelectedText : PlainText;
        if (string.IsNullOrEmpty(text)) return;
        if (TopLevel.GetTopLevel(this) is { Clipboard: { } clipboard })
            clipboard.SetTextAsync(text);
    }

    /// <summary>Scrolls the view so that <paramref name="offset"/> in <see cref="PlainText"/> is visible.</summary>
    public void ScrollToOffset(int offset)
    {
        EnsureLayout();
        foreach (var block in _blocks)
        {
            if (offset > block.End) continue;
            if (block.Y < _offset) SetOffset(block.Y);
            else if (block.Y + block.Height > _offset + _viewportHeight)
                SetOffset(block.Y + block.Height - _viewportHeight);
            return;
        }
    }

    private void SetSelection(int start, int end)
    {
        int len = _plainText.Length;
        start = Math.Clamp(start, 0, len);
        end = Math.Clamp(end, 0, len);
        if (start > end) (start, end) = (end, start);
        if (start == _selectionStart && end == _selectionEnd) return;
        _selectionStart = start;
        _selectionEnd = end;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void BuildContextMenu()
    {
        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) => CopySelection();
        var selectAll = new MenuItem { Header = "Select All" };
        selectAll.Click += (_, _) => SelectAll();
        ContextMenu = new ContextMenu { ItemsSource = new object[] { copy, selectAll } };
    }

    // ---- Property changes ----

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ColorThemeProperty)
        {
            ApplyTheme(change.GetNewValue<EditorTheme>());
            return;
        }

        if (change.Property == ContextMenuProperty)
        {
            _focusBehavior?.HandleContextMenuChanged(change.NewValue as ContextMenu);
            return;
        }

        if (change.Property == ImagesProperty)
        {
            UnsubscribeImages(change.OldValue as IEnumerable<ImageEntry>);
            SubscribeImages(change.NewValue as IEnumerable<ImageEntry>);
            RebuildImageCache();
            InvalidateContent();
            return;
        }

        if (change.Property == MarkdownTextProperty
            || change.Property == SoftLineBreaksProperty
            || change.Property == ImageResolverProperty)
        {
            InvalidateContent();
            return;
        }

        if (change.Property == DefaultFontProperty
            || change.Property == DefaultFontSizeProperty
            || change.Property == CodeFontProperty
            || change.Property == CodeFontScaleProperty
            || change.Property == ViewerPaddingProperty
            || change.Property == ParagraphSpacingProperty
            || change.Property == LineSpacingProperty
            || change.Property == MaxImageHeightProperty)
        {
            InvalidateLayout();
            return;
        }

        if (change.Property == ForegroundProperty
            || change.Property == MutedBrushProperty
            || change.Property == LinkBrushProperty
            || change.Property == BackgroundBrushProperty
            || change.Property == CodeBackgroundProperty
            || change.Property == HighlightDiffProperty
            || change.Property == DiffAddedBackgroundProperty
            || change.Property == DiffRemovedBackgroundProperty
            || change.Property == DiffAddedForegroundProperty
            || change.Property == DiffRemovedForegroundProperty
            || change.Property == DiffMetaForegroundProperty
            || change.Property == RuleBrushProperty
            || change.Property == QuoteBarBrushProperty)
        {
            _fmtCache.Clear();
            InvalidateLayout();
            return;
        }

        if (change.Property == SelectionBrushProperty
            || change.Property == InactiveSelectionBrushProperty)
        {
            InvalidateVisual();
        }
    }

    private void InvalidateContent()
    {
        _contentDirty = true;
        _layoutDirty = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void InvalidateLayout()
    {
        _layoutDirty = true;
        _measureCache.Clear();
        _fmtCache.Clear();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void ApplyTheme(EditorTheme theme)
    {
        if (theme == EditorTheme.None) return;

        if (theme == EditorTheme.Light)
        {
            BackgroundBrush = Brushes.White;
            Foreground = Brushes.Black;
            MutedBrush = new SolidColorBrush(Color.FromRgb(110, 118, 129));
            LinkBrush = new SolidColorBrush(Color.FromRgb(9, 105, 218));
            CodeBackground = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0));
            RuleBrush = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0));
            QuoteBarBrush = new SolidColorBrush(Color.FromRgb(208, 215, 222));
            SelectionBrush = new SolidColorBrush(Color.FromArgb(80, 30, 144, 255));
            InactiveSelectionBrush = new SolidColorBrush(Color.FromArgb(60, 130, 130, 130));
            ScrollTrackBrush = new SolidColorBrush(Color.FromArgb(24, 0, 0, 0));
            ScrollThumbBrush = new SolidColorBrush(Color.FromArgb(96, 0, 0, 0));
        }
        else
        {
            BackgroundBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220));
            MutedBrush = new SolidColorBrush(Color.FromRgb(150, 155, 160));
            LinkBrush = new SolidColorBrush(Color.FromRgb(88, 166, 255));
            CodeBackground = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            RuleBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
            QuoteBarBrush = new SolidColorBrush(Color.FromRgb(80, 86, 94));
            SelectionBrush = new SolidColorBrush(Color.FromArgb(80, 60, 140, 230));
            InactiveSelectionBrush = new SolidColorBrush(Color.FromArgb(70, 150, 150, 150));
            ScrollTrackBrush = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
            ScrollThumbBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
        }
    }

    // ---- Images ----

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _focusBehavior.Detach();
        StopAutoScroll();
        UnsubscribeImages(Images);
        base.OnDetachedFromVisualTree(e);
    }

    private void SubscribeImages(IEnumerable<ImageEntry>? images)
    {
        if (images == null) return;
        if (images is INotifyCollectionChanged ncc)
            ncc.CollectionChanged += OnImagesCollectionChanged;
        foreach (var entry in images)
        {
            entry.PropertyChanged += OnImageEntryChanged;
            _subscribedImages.Add(entry);
        }
    }

    private void UnsubscribeImages(IEnumerable<ImageEntry>? images)
    {
        if (images is INotifyCollectionChanged ncc)
            ncc.CollectionChanged -= OnImagesCollectionChanged;
        foreach (var entry in _subscribedImages)
            entry.PropertyChanged -= OnImageEntryChanged;
        _subscribedImages.Clear();
    }

    private void OnImagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UnsubscribeImages(null);
        if (Images is { } images)
            foreach (var entry in images)
            {
                entry.PropertyChanged += OnImageEntryChanged;
                _subscribedImages.Add(entry);
            }
        RebuildImageCache();
        InvalidateContent();
    }

    private void OnImageEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        RebuildImageCache();
        InvalidateContent();
    }

    private void RebuildImageCache()
    {
        _imageCache.Clear();
        if (Images == null) return;
        foreach (var entry in Images)
            if (entry.Bitmap != null)
                _imageCache[entry.Key] = entry.Bitmap;
    }

    private Bitmap? ResolveImage(string key)
    {
        if (_imageCache.TryGetValue(key, out var bitmap)) return bitmap;
        return ImageResolver?.Invoke(key);
    }

    // ---- Content build (width independent) ----

    private void EnsureContent()
    {
        if (!_contentDirty) return;
        BuildContent();
    }

    private void BuildContent()
    {
        _contentDirty = false;
        _layoutDirty = true;
        _sourceBlocks.Clear();

        var blocks = MarkdownParser.Parse(MarkdownText, SoftLineBreaks);
        var text = new StringBuilder();

        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var sb = new SourceBlock { Source = block, Start = text.Length };

            switch (block.Type)
            {
                case MarkdownBlockType.ThematicBreak:
                    break;

                case MarkdownBlockType.CodeBlock:
                    AppendRun(sb.Runs, text, block.Code, code: true);
                    break;

                case MarkdownBlockType.Table:
                    BuildTableRuns(sb, text, block.Table!);
                    break;

                default:
                    if (block.IsListItem && block.Marker != null)
                        AppendRun(sb.Marker, text, block.Marker + " ", muted: !block.Ordered);
                    AppendInlines(sb.Runs, text, block.Inlines);
                    break;
            }

            sb.End = text.Length;
            _sourceBlocks.Add(sb);

            if (i < blocks.Count - 1)
            {
                bool tight = block.ListDepth > 0 && blocks[i + 1].ListDepth > 0;
                text.Append(tight ? "\n" : "\n\n");
            }
        }

        _plainText = text.ToString();
        _selectionStart = Math.Min(_selectionStart, _plainText.Length);
        _selectionEnd = Math.Min(_selectionEnd, _plainText.Length);
    }

    private void BuildTableRuns(SourceBlock sb, StringBuilder text, MarkdownTable table)
    {
        sb.TableCells = new List<List<List<SourceRun>>>();
        sb.TableRowIsHeader = new List<bool>();

        for (int r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var cells = new List<List<SourceRun>>();
            for (int c = 0; c < row.Cells.Count; c++)
            {
                if (c > 0) text.Append('\t');
                var runs = new List<SourceRun>();
                AppendInlines(runs, text, row.Cells[c].Inlines);
                cells.Add(runs);
            }
            if (r < table.Rows.Count - 1) text.Append('\n');
            sb.TableCells.Add(cells);
            sb.TableRowIsHeader.Add(row.IsHeader);
        }
    }

    private void AppendInlines(List<SourceRun> runs, StringBuilder text, List<MarkdownInline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline.LineBreak)
            {
                runs.Add(new SourceRun { LineBreak = true, Start = text.Length });
                continue;
            }

            if (inline.IsImage)
            {
                var bitmap = ResolveImage(inline.ImageKey!);
                string alt = string.IsNullOrEmpty(inline.ImageAlt)
                    ? (bitmap != null ? "￼" : inline.ImageKey!)
                    : inline.ImageAlt!;
                var run = new SourceRun
                {
                    Text = alt,
                    Start = text.Length,
                    Image = bitmap,
                    Italic = bitmap == null,
                    Muted = bitmap == null,
                    LinkUrl = inline.LinkUrl
                };
                text.Append(alt);
                runs.Add(run);
                continue;
            }

            if (inline.Text.Length == 0) continue;

            runs.Add(new SourceRun
            {
                Text = inline.Text,
                Start = text.Length,
                Bold = inline.Bold,
                Italic = inline.Italic,
                Strike = inline.Strikethrough,
                Code = inline.Code,
                LinkUrl = inline.LinkUrl
            });
            text.Append(inline.Text);
        }
    }

    private static void AppendRun(List<SourceRun> runs, StringBuilder text, string value,
        bool code = false, bool muted = false)
    {
        if (value.Length == 0) return;
        runs.Add(new SourceRun { Text = value, Start = text.Length, Code = code, Muted = muted });
        text.Append(value);
    }

    // ---- Layout ----

    private double AvailableWidth =>
        Math.Max((_desiredWidth > 0 ? _desiredWidth : 400)
            - (ScrollBarVisible ? ScrollBarWidth : 0), 80);

    private void EnsureLayout()
    {
        EnsureContent();
        if (_layoutDirty || Math.Abs(_layoutWidth - AvailableWidth) > 0.5)
            BuildLayout();
    }

    private void BuildLayout()
    {
        _layoutDirty = false;
        _layoutWidth = AvailableWidth;
        _blocks.Clear();

        var padding = ViewerPadding;
        double right = Math.Max(_layoutWidth - padding.Right, padding.Left + 40);
        double y = padding.Top;

        for (int i = 0; i < _sourceBlocks.Count; i++)
        {
            var source = _sourceBlocks[i];
            var block = LayoutBlock(source, padding.Left, right, y);
            _blocks.Add(block);
            y = block.Y + block.Height;

            if (i < _sourceBlocks.Count - 1)
                y += SpacingAfter(source.Source, _sourceBlocks[i + 1].Source);
        }

        _desiredHeight = y + padding.Bottom;
    }

    private double SpacingAfter(MarkdownBlock current, MarkdownBlock next)
    {
        bool tight = current.ListDepth > 0 && next.ListDepth > 0;
        double spacing = tight ? ParagraphSpacing * 0.35 : ParagraphSpacing;
        if (next.Type == MarkdownBlockType.Heading) spacing += ParagraphSpacing * 0.6;
        return spacing;
    }

    private VisualBlock LayoutBlock(SourceBlock source, double left, double right, double y)
    {
        var md = source.Source;
        var block = new VisualBlock
        {
            Source = md,
            Y = y,
            Start = source.Start,
            End = source.End
        };

        double indent = left + md.QuoteDepth * QuoteIndent + md.ListDepth * ListIndent;
        block.ContentLeft = indent;
        block.ContentRight = right;

        switch (md.Type)
        {
            case MarkdownBlockType.ThematicBreak:
                block.Height = ParagraphSpacing;
                return block;

            case MarkdownBlockType.CodeBlock:
                LayoutCodeBlock(source, block, indent, right, y);
                return block;

            case MarkdownBlockType.Table:
                LayoutTable(source, block, indent, right, y);
                return block;
        }

        double fontSize = FontSizeFor(md);
        double contentLeft = indent;

        if (md.IsTask)
        {
            double boxSize = fontSize * 0.85;
            block.CheckBox = new Rect(indent, y + (fontSize + 4 - boxSize) / 2, boxSize, boxSize);
            contentLeft = indent + boxSize + 6;
        }
        else if (source.Marker.Count > 0)
        {
            // The marker sits in the gutter to the left of the item text.
            var markerRuns = ResolveRuns(source.Marker, md, fontSize);
            double markerWidth = 0;
            foreach (var run in markerRuns) markerWidth += run.Width;
            double markerX = Math.Max(left, indent - Math.Max(markerWidth, ListIndent * 0.6));
            var markerLine = new VisualLine { Y = y, Left = markerX, Start = source.Marker[0].Start };
            double mx = markerX;
            foreach (var run in markerRuns)
            {
                run.X = mx;
                mx += run.Width;
                markerLine.Runs.Add(run);
                markerLine.Height = Math.Max(markerLine.Height, run.Height);
                markerLine.Baseline = Math.Max(markerLine.Baseline, run.Baseline);
                markerLine.End = run.End;
            }
            markerLine.Right = mx;
            block.Lines.Add(markerLine);
        }

        var lines = FlowRuns(ResolveRuns(source.Runs, md, fontSize), source.Runs,
            contentLeft, right, y, fontSize);

        // Merge the marker line into the first content line so both share a row.
        if (block.Lines.Count == 1 && lines.Count > 0)
        {
            var marker = block.Lines[0];
            var first = lines[0];
            foreach (var run in marker.Runs) first.Runs.Insert(0, run);
            first.Left = marker.Left;
            first.Start = marker.Start;
            first.Height = Math.Max(first.Height, marker.Height);
            first.Baseline = Math.Max(first.Baseline, marker.Baseline);
            block.Lines.Clear();
        }
        else if (block.Lines.Count == 1)
        {
            block.Lines[0].Height = Math.Max(block.Lines[0].Height, fontSize + 4);
        }

        block.Lines.AddRange(lines);
        RepositionLines(block.Lines, y);

        block.Height = TotalHeight(block.Lines);
        if (block.Height <= 0) block.Height = fontSize + 4;

        if (md.Type == MarkdownBlockType.Heading && md.HeadingLevel <= 2)
        {
            block.HeadingRule = true;
            block.Height += 6;
        }

        return block;
    }

    private void LayoutCodeBlock(SourceBlock source, VisualBlock block, double indent, double right, double y)
    {
        block.CodeBox = true;
        double fontSize = DefaultFontSize * CodeFontScale;
        double innerLeft = indent + CodePadding;
        double innerRight = Math.Max(right - CodePadding, innerLeft + 40);
        double top = y + CodePadding;

        var typeface = new Typeface(CodeFont);
        var lines = new List<VisualLine>();
        double lineY = top;
        bool diff = HighlightDiff && IsDiffLanguage(block.Source.CodeLanguage);

        // Code keeps its own line structure; only over-long lines are wrapped.
        var codeLines = new List<string>();
        var codeLineStarts = new List<int>();
        foreach (var source_run in source.Runs)
        {
            int lineStart = source_run.Start;
            foreach (var text in source_run.Text.Split('\n'))
            {
                codeLines.Add(text);
                codeLineStarts.Add(lineStart);
                lineStart += text.Length + 1;
            }
        }

        // Read as a whole rather than a line at a time: what a line beginning `---` is depends on the
        // lines before it, and the pair that opens the next file is only recognisable from the one
        // after it.
        var roles = diff ? DiffRolesOf(codeLines) : null;

        for (var index = 0; index < codeLines.Count; index++)
        {
            var codeLine = codeLines[index];
            var (ground, ink) = roles is null ? (null, null) : DiffColours(roles[index]);
            var runs = new List<VisualRun>
            {
                new()
                {
                    Text = codeLine,
                    Start = codeLineStarts[index],
                    Typeface = typeface,
                    FontSize = fontSize,
                    Brush = ink ?? Foreground,
                    // The block already paints one background box; per-run
                    // highlighting would double up on it.
                    IsCode = false
                }
            };
            MeasureRuns(runs);
            var wrapped = FlowVisualRuns(runs, innerLeft, innerRight, lineY, fontSize, lineSpacing: 0);
            // A wrapped line is still the same line of the patch, so the ground carries on to the
            // rest of it: a band that stopped at the wrap would say a change ended mid-sentence.
            if (ground is not null)
                foreach (var wrappedLine in wrapped) wrappedLine.Band = ground;
            lines.AddRange(wrapped);
            lineY = wrapped.Count > 0
                ? wrapped[^1].Y + wrapped[^1].Height
                : lineY + fontSize + 4;
        }

        if (lines.Count > 0)
            lines[^1].Right = Math.Max(lines[^1].Right, innerLeft);

        block.Lines.AddRange(lines);
        block.Height = (lines.Count > 0 ? lines[^1].Y + lines[^1].Height - y : fontSize + 4) + CodePadding;
    }

    /// <summary>Whether a fence's word says its content is a patch.</summary>
    /// <remarks>Two words and no more. Guessing — a block with no language whose first lines begin
    /// with <c>+</c> — would colour a list of build flags as a diff, and the author of the document
    /// has already said what it is by writing the fence.</remarks>
    internal static bool IsDiffLanguage(string? language) =>
        string.Equals(language, "diff", StringComparison.OrdinalIgnoreCase)
        || string.Equals(language, "patch", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a line of a patch is.</summary>
    internal enum DiffLineRole { Context, Added, Removed, Meta }

    /// <summary>What each line of a patch is, read in order.</summary>
    /// <remarks>In order, because the first character of a line does not settle it on its own: a line
    /// of two dashes removed from a file is written <c>---</c>, exactly like the header that names the
    /// file, and a line of two pluses added to one is written <c>+++</c>. Inside a hunk neither can be
    /// a header — git is quoting somebody's code there — and outside one neither can be content. So
    /// the hunk is tracked, which is the same rule git itself reads a patch by.</remarks>
    internal static DiffLineRole[] DiffRolesOf(IReadOnlyList<string> lines)
    {
        var roles = new DiffLineRole[lines.Count];
        var inHunk = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            // A hunk runs until a line that cannot be part of one: every line of a hunk body begins
            // with a space, a `+`, a `-` or git's `\` marker, or is empty. The `---`/`+++` pair is the
            // exception, since it is also how one patch is concatenated onto another.
            if (line.StartsWith("@@", StringComparison.Ordinal)) inHunk = true;
            else if (inHunk && (!ContinuesHunk(line) || OpensAnotherFile(lines, i))) inHunk = false;

            roles[i] = line.StartsWith("@@", StringComparison.Ordinal) ? DiffLineRole.Meta
                : inHunk ? InHunkRoleOf(line)
                : DiffRoleOf(line);
        }

        return roles;
    }

    /// <summary>Whether a line can be part of a hunk's body at all.</summary>
    private static bool ContinuesHunk(string line) =>
        line.Length == 0 || line[0] is ' ' or '+' or '-' or '\\';

    /// <summary>Whether the next file's own header starts here, though its first line reads as a
    /// removal.</summary>
    private static bool OpensAnotherFile(IReadOnlyList<string> lines, int index) =>
        lines[index].StartsWith("--- ", StringComparison.Ordinal)
        && index + 1 < lines.Count
        && lines[index + 1].StartsWith("+++ ", StringComparison.Ordinal);

    /// <summary>What a line inside a hunk is: its first character, and nothing else.</summary>
    /// <remarks>Git's <c>\ No newline at end of file</c> is the one line in a hunk belonging to
    /// neither side, so it is the format talking rather than content.</remarks>
    private static DiffLineRole InHunkRoleOf(string line) => line switch
    {
        _ when line.StartsWith("\\ ", StringComparison.Ordinal) => DiffLineRole.Meta,
        _ when line.StartsWith('+') => DiffLineRole.Added,
        _ when line.StartsWith('-') => DiffLineRole.Removed,
        _ => DiffLineRole.Context,
    };

    /// <summary>Which of the four a line outside any hunk is, read from the one character the format
    /// puts at its head.</summary>
    /// <remarks>Only where that character cannot be the format's own: <c>+++</c> and <c>---</c> are the
    /// file header, not a line added or removed, and reading them as content paints the two loudest
    /// bands in the block over the two lines that say the least. <c>Index: path</c> and the row of
    /// <c>=</c> under it are the same header in the shape <c>diff -u</c> and Subversion write it, which
    /// is what an agent's own patch of a fragment often is.</remarks>
    internal static DiffLineRole DiffRoleOf(string line)
    {
        if (line.Length == 0) return DiffLineRole.Context;

        if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)
            || line.StartsWith("@@", StringComparison.Ordinal)
            || NamesTheFile(line))
            return DiffLineRole.Meta;

        return line[0] switch
        {
            '+' => DiffLineRole.Added,
            '-' => DiffLineRole.Removed,
            _ => DiffLineRole.Context,
        };
    }

    /// <summary>Everything git writes above a hunk, and the two notices it writes instead of one.</summary>
    /// <remarks>The pair of paths and the blob index are only the usual case: a rename, a copy, a mode
    /// change and a binary file are whole patches whose header <em>is</em> the change, and read as
    /// content they come out in the colour of somebody's code — <c>rename from a</c> as a line of the
    /// file, <c>Binary files a/x and b/x differ</c> as prose. None of them can begin a line inside a
    /// hunk, where every line wears a marker, so this is asked only outside one.</remarks>
    private static bool NamesTheFile(string line)
    {
        foreach (var prefix in HeaderPrefixes)
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    private static readonly string[] HeaderPrefixes =
    [
        "diff ", "index ", "Index: ", "===",
        "old mode ", "new mode ", "new file mode ", "deleted file mode ",
        "similarity index ", "dissimilarity index ",
        "copy from ", "copy to ", "rename from ", "rename to ",
        "Binary files ", "GIT binary patch",
    ];

    /// <summary>What a line of a patch is drawn in: its ground, and its own colour where it has one.</summary>
    private (IBrush? Ground, IBrush? Ink) DiffColours(DiffLineRole role) => role switch
    {
        DiffLineRole.Added => (DiffAddedBackground, DiffAddedForeground),
        DiffLineRole.Removed => (DiffRemovedBackground, DiffRemovedForeground),
        DiffLineRole.Meta => (null, DiffMetaForeground),
        _ => (null, null),
    };

    private void LayoutTable(SourceBlock source, VisualBlock block, double indent, double right, double y)
    {
        var table = block.Source.Table!;
        var cells = source.TableCells!;
        int columns = table.ColumnCount;
        if (columns == 0) { block.Height = 0; return; }

        double fontSize = DefaultFontSize;
        double available = Math.Max(right - indent, 120);

        // Natural width per column, then scale down proportionally when too wide.
        var widths = new double[columns];
        for (int r = 0; r < cells.Count; r++)
            for (int c = 0; c < cells[r].Count && c < columns; c++)
            {
                bool header = source.TableRowIsHeader![r];
                double w = 0;
                foreach (var run in ResolveRuns(cells[r][c], block.Source, fontSize, forceBold: header))
                    w += run.Width;
                widths[c] = Math.Max(widths[c], w + CellPaddingX * 2);
            }

        double total = 0;
        foreach (double w in widths) total += w;
        if (total > available && total > 0)
        {
            double scale = available / total;
            for (int c = 0; c < columns; c++)
                widths[c] = Math.Max(widths[c] * scale, 48);
        }

        double rowY = y;
        for (int r = 0; r < cells.Count; r++)
        {
            bool header = source.TableRowIsHeader![r];
            double x = indent;
            double rowHeight = 0;
            var rowLines = new List<(VisualLine Line, int Column)>();

            for (int c = 0; c < columns; c++)
            {
                double cellWidth = widths[c];
                if (c < cells[r].Count)
                {
                    var runs = ResolveRuns(cells[r][c], block.Source, fontSize, forceBold: header);
                    var flowed = FlowVisualRuns(runs, x + CellPaddingX,
                        x + cellWidth - CellPaddingX, rowY + CellPaddingY, fontSize);
                    double h = TotalHeight(flowed);
                    rowHeight = Math.Max(rowHeight, h + CellPaddingY * 2);
                    foreach (var line in flowed) rowLines.Add((line, c));
                }
                else
                {
                    rowHeight = Math.Max(rowHeight, fontSize + 4 + CellPaddingY * 2);
                }
                x += cellWidth;
            }

            double cellX = indent;
            for (int c = 0; c < columns; c++)
            {
                block.Cells.Add((new Rect(cellX, rowY, widths[c], rowHeight), header));
                AlignCell(rowLines, c, cellX, widths[c], table, c);
                cellX += widths[c];
            }

            foreach (var (line, _) in rowLines) block.Lines.Add(line);
            rowY += rowHeight;
        }

        block.Height = rowY - y;
    }

    private static void AlignCell(List<(VisualLine Line, int Column)> rowLines, int column,
        double cellX, double cellWidth, MarkdownTable table, int columnIndex)
    {
        var alignment = columnIndex < table.Alignments.Count
            ? table.Alignments[columnIndex]
            : MarkdownColumnAlignment.Left;
        if (alignment == MarkdownColumnAlignment.Left) return;

        foreach (var (line, c) in rowLines)
        {
            if (c != column) continue;
            double contentWidth = line.Right - line.Left;
            double free = cellWidth - CellPaddingX * 2 - contentWidth;
            if (free <= 0) continue;
            double shift = alignment == MarkdownColumnAlignment.Center ? free / 2 : free;
            foreach (var run in line.Runs) run.X += shift;
            line.Left += shift;
            line.Right += shift;
        }
    }

    private static double TotalHeight(List<VisualLine> lines)
    {
        if (lines.Count == 0) return 0;
        double top = lines[0].Y;
        double bottom = lines[^1].Y + lines[^1].Height;
        return bottom - top;
    }

    private void RepositionLines(List<VisualLine> lines, double top)
    {
        double y = top;
        for (int i = 0; i < lines.Count; i++)
        {
            double delta = y - lines[i].Y;
            if (Math.Abs(delta) > 0.001)
                lines[i].Y += delta;
            y = lines[i].Y + lines[i].Height + (i < lines.Count - 1 ? LineSpacing : 0);
        }
    }

    private double FontSizeFor(MarkdownBlock block)
    {
        double baseSize = DefaultFontSize;
        if (block.Type != MarkdownBlockType.Heading) return baseSize;
        return block.HeadingLevel switch
        {
            1 => baseSize * 1.9,
            2 => baseSize * 1.55,
            3 => baseSize * 1.30,
            4 => baseSize * 1.12,
            5 => baseSize,
            _ => baseSize * 0.92
        };
    }

    private List<VisualRun> ResolveRuns(List<SourceRun> sourceRuns, MarkdownBlock block,
        double fontSize, bool forceBold = false)
    {
        bool headingBold = block.Type == MarkdownBlockType.Heading;
        var result = new List<VisualRun>(sourceRuns.Count);

        foreach (var source in sourceRuns)
        {
            if (source.LineBreak)
            {
                result.Add(new VisualRun
                {
                    Text = string.Empty,
                    Start = source.Start,
                    FontSize = fontSize,
                    Typeface = new Typeface(DefaultFont)
                });
                continue;
            }

            bool bold = source.Bold || headingBold || forceBold;
            var family = source.Code ? CodeFont : DefaultFont;
            double size = source.Code ? fontSize * 0.94 : fontSize;

            var run = new VisualRun
            {
                Text = source.Text,
                Start = source.Start,
                Typeface = new Typeface(family,
                    source.Italic ? FontStyle.Italic : FontStyle.Normal,
                    bold ? FontWeight.Bold : FontWeight.Normal),
                FontSize = size,
                IsCode = source.Code,
                Strike = source.Strike,
                LinkUrl = source.LinkUrl,
                Underline = source.LinkUrl != null,
                Image = source.Image,
                Brush = source.LinkUrl != null ? LinkBrush
                    : source.Muted || block.QuoteDepth > 0 ? MutedBrush
                    : Foreground
            };
            result.Add(run);
        }

        MeasureRuns(result);
        return result;
    }

    private void MeasureRuns(List<VisualRun> runs)
    {
        foreach (var run in runs)
        {
            if (run.Image != null)
            {
                double natural = run.Image.PixelSize.Height;
                double height = Math.Min(natural <= 0 ? MaxImageHeight : natural, MaxImageHeight);
                double scale = natural > 0 ? height / natural : 1;
                run.Height = height;
                run.Width = run.Image.PixelSize.Width * scale;
                run.Baseline = height;
                continue;
            }

            run.Width = MeasureText(run.Text, run.Typeface, run.FontSize);
            var metrics = GetFormatted(run.Text.Length > 0 ? run.Text : "X",
                run.Typeface, run.FontSize, Brushes.Transparent);
            run.Height = metrics.Height;
            run.Baseline = metrics.Baseline;
        }
    }

    private List<VisualLine> FlowRuns(List<VisualRun> runs, List<SourceRun> sources,
        double left, double right, double top, double fontSize) =>
        FlowVisualRuns(runs, left, right, top, fontSize);

    /// <summary>Greedily breaks runs into lines that fit between left and right.</summary>
    /// <param name="lineSpacing">What goes between two lines, or null for this viewer's own. Nothing
    /// inside a code block: that spacing is for prose, where a line is part of a paragraph, and a long
    /// line of code wraps into several visual lines with a gap between each — which is invisible until
    /// a line carries a ground of its own, and then it is a patch drawn as bands with unpainted stripes
    /// through it.</param>
    private List<VisualLine> FlowVisualRuns(List<VisualRun> runs, double left, double right,
        double top, double fontSize, double? lineSpacing = null)
    {
        double spacing = lineSpacing ?? LineSpacing;

        var lines = new List<VisualLine>();
        double maxWidth = Math.Max(right - left, 40);

        var current = NewLine(left, top, runs.Count > 0 ? runs[0].Start : 0);
        double penX = left;

        void CommitLine()
        {
            if (current.Runs.Count == 0)
            {
                current.Height = fontSize + 4;
                current.Baseline = fontSize;
                current.End = current.Start;
            }
            current.Right = penX;
            lines.Add(current);
        }

        void BreakLine(int nextStart)
        {
            CommitLine();
            current = NewLine(left, 0, nextStart);
            penX = left;
        }

        foreach (var run in runs)
        {
            if (run.Text.Length == 0 && run.Image == null)
            {
                // explicit line break
                BreakLine(run.Start);
                continue;
            }

            if (run.Image != null)
            {
                double imageWidth = Math.Min(run.Width, maxWidth);
                if (penX > left && penX + imageWidth > right) BreakLine(run.Start);
                if (imageWidth < run.Width)
                {
                    double scale = imageWidth / run.Width;
                    run.Width = imageWidth;
                    run.Height *= scale;
                    run.Baseline = run.Height;
                }
                run.X = penX;
                penX += run.Width;
                AddRunToLine(current, run, penX);
                continue;
            }

            foreach (var token in Tokenize(run.Text))
            {
                string tokenText = run.Text.Substring(token.Start, token.Length);
                double tokenWidth = MeasureText(tokenText, run.Typeface, run.FontSize);

                if (penX + tokenWidth > right && penX > left)
                {
                    if (tokenText.Trim().Length == 0)
                    {
                        // trailing space at a break: keep it on the current line
                        AppendToken(current, run, token, tokenText, tokenWidth, ref penX);
                        continue;
                    }
                    BreakLine(run.Start + token.Start);
                }

                if (tokenWidth > maxWidth)
                {
                    // A single token longer than the line: split it by measurement.
                    int consumed = 0;
                    while (consumed < tokenText.Length)
                    {
                        int fit = FitCount(tokenText[consumed..], run.Typeface, run.FontSize, right - penX);
                        if (fit == 0)
                        {
                            if (penX > left) { BreakLine(run.Start + token.Start + consumed); continue; }
                            fit = 1;
                        }
                        var piece = tokenText.Substring(consumed, fit);
                        double pieceWidth = MeasureText(piece, run.Typeface, run.FontSize);
                        AppendPiece(current, run, run.Start + token.Start + consumed, piece, pieceWidth, ref penX);
                        consumed += fit;
                        if (consumed < tokenText.Length) BreakLine(run.Start + token.Start + consumed);
                    }
                    continue;
                }

                AppendToken(current, run, token, tokenText, tokenWidth, ref penX);
            }
        }

        CommitLine();

        double y = top;
        for (int i = 0; i < lines.Count; i++)
        {
            lines[i].Y = y;
            y += lines[i].Height + spacing;
        }
        return lines;
    }

    private static VisualLine NewLine(double left, double y, int start) =>
        new() { Left = left, Right = left, Y = y, Start = start, End = start };

    private static void AddRunToLine(VisualLine line, VisualRun run, double penX)
    {
        line.Runs.Add(run);
        line.Height = Math.Max(line.Height, run.Height);
        line.Baseline = Math.Max(line.Baseline, run.Baseline);
        line.End = Math.Max(line.End, run.End);
        line.Right = penX;
    }

    private void AppendToken(VisualLine line, VisualRun template, (int Start, int Length) token,
        string text, double width, ref double penX) =>
        AppendPiece(line, template, template.Start + token.Start, text, width, ref penX);

    // Consecutive pieces of the same source run are merged so a line holds few runs.
    private void AppendPiece(VisualLine line, VisualRun template, int start, string text,
        double width, ref double penX)
    {
        if (line.Runs.Count > 0)
        {
            var last = line.Runs[^1];
            if (last.Image == null && ReferenceEquals(last.Typeface.FontFamily, template.Typeface.FontFamily)
                && last.Typeface == template.Typeface
                && Math.Abs(last.FontSize - template.FontSize) < 0.01
                && ReferenceEquals(last.Brush, template.Brush)
                && last.Underline == template.Underline
                && last.Strike == template.Strike
                && last.IsCode == template.IsCode
                && last.LinkUrl == template.LinkUrl
                && last.End == start)
            {
                last.Text += text;
                last.Width = MeasureText(last.Text, last.Typeface, last.FontSize);
                penX = last.X + last.Width;
                line.End = last.End;
                line.Right = penX;
                return;
            }
        }

        var run = new VisualRun
        {
            Text = text,
            Start = start,
            Typeface = template.Typeface,
            FontSize = template.FontSize,
            Brush = template.Brush,
            Underline = template.Underline,
            Strike = template.Strike,
            IsCode = template.IsCode,
            LinkUrl = template.LinkUrl,
            Height = template.Height,
            Baseline = template.Baseline,
            Width = width,
            X = penX
        };
        penX += width;
        AddRunToLine(line, run, penX);
    }

    /// <summary>Splits text into break opportunities (a word plus its trailing spaces).</summary>
    private static List<(int Start, int Length)> Tokenize(string text)
    {
        var tokens = new List<(int, int)>();
        int i = 0;
        while (i < text.Length)
        {
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            tokens.Add((start, i - start));
        }
        return tokens;
    }

    private int FitCount(string text, Typeface typeface, double fontSize, double available)
    {
        if (available <= 0 || text.Length == 0) return 0;
        int lo = 1, hi = text.Length;
        int best = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (MeasureText(text[..mid], typeface, fontSize) <= available)
            {
                best = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }
        return best;
    }

    private double MeasureText(string text, Typeface typeface, double fontSize)
    {
        if (text.Length == 0) return 0;
        var key = (text, typeface, fontSize);
        if (_measureCache.TryGetValue(key, out double cached)) return cached;
        var fmt = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, fontSize, Brushes.Transparent);
        double width = fmt.WidthIncludingTrailingWhitespace;
        if (_measureCache.Count > MaxCacheEntries) _measureCache.Clear();
        _measureCache[key] = width;
        return width;
    }

    private FormattedText GetFormatted(string text, Typeface typeface, double fontSize, IBrush brush)
    {
        var key = (text, typeface, fontSize, brush);
        if (_fmtCache.TryGetValue(key, out var cached)) return cached;
        var fmt = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, fontSize, brush);
        if (_fmtCache.Count > MaxCacheEntries) _fmtCache.Clear();
        _fmtCache[key] = fmt;
        return fmt;
    }

    // ---- Measure / arrange ----

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;
        _desiredWidth = Math.Max(width, 120);
        _internalScroll = !double.IsInfinity(availableSize.Height);
        _viewportHeight = _internalScroll ? availableSize.Height : 0;

        EnsureLayout();

        if (_offset > MaxOffset) _offset = MaxOffset;

        bool scrollBarVisible = ScrollBarVisible;
        if (scrollBarVisible != _scrollBarWasVisible)
        {
            _scrollBarWasVisible = scrollBarVisible;
            _layoutDirty = true;
            InvalidateMeasure();
        }

        return new Size(_desiredWidth, _internalScroll ? availableSize.Height : _desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _desiredWidth = Math.Max(finalSize.Width, 120);
        _internalScroll = !double.IsInfinity(finalSize.Height);
        _viewportHeight = _internalScroll ? finalSize.Height : 0;

        EnsureLayout();
        if (_offset > MaxOffset) _offset = MaxOffset;

        return new Size(_desiredWidth,
            _internalScroll ? finalSize.Height : Math.Max(finalSize.Height, _desiredHeight));
    }

    // ---- Scrolling ----

    private double MaxOffset => _internalScroll ? Math.Max(0, _desiredHeight - _viewportHeight) : 0;

    private bool ScrollBarVisible => _internalScroll && _viewportHeight > 0 && MaxOffset > 0.5;

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
        double thumb = _desiredHeight > 0
            ? Math.Max(MinThumbHeight, track * _viewportHeight / _desiredHeight)
            : track;
        thumb = Math.Min(thumb, track);
        double max = MaxOffset;
        double y = max > 0 ? _offset / max * (track - thumb) : 0;
        return (y, thumb);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!ScrollBarVisible || e.Delta.Y == 0) return;

        double before = _offset;
        SetOffset(_offset - e.Delta.Y * (DefaultFontSize + LineSpacing) * 3);
        if (_offset != before) e.Handled = true;
    }

    // ---- Rendering ----

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));

        EnsureLayout();

        using (context.PushTransform(Matrix.CreateTranslation(0, -_offset)))
            RenderContent(context);

        RenderScrollBar(context);
    }

    private void RenderContent(DrawingContext context)
    {
        double top = _offset;
        double bottom = _offset + (_viewportHeight > 0 ? _viewportHeight : _desiredHeight);

        var (first, last) = GetVisibleBlockRange(top, bottom);
        var selectionBrush = IsFocused ? SelectionBrush : InactiveSelectionBrush;

        for (int i = first; i <= last && i < _blocks.Count; i++)
        {
            var block = _blocks[i];
            RenderBlockDecorations(context, block);

            foreach (var line in block.Lines)
            {
                // A line's own ground, below everything else on it: the selection has to stay visible
                // over it, and so does the inline-code box.
                if (line.Band is { } band)
                    context.FillRectangle(band, new Rect(
                        block.ContentLeft, line.Y,
                        Math.Max(block.ContentRight - block.ContentLeft, 0), line.Height));

                // Run backgrounds (inline code) go first so the selection
                // highlight stays visible on top of them.
                foreach (var run in line.Runs)
                    RenderRunBackground(context, line, run);

                RenderSelection(context, line, selectionBrush);

                foreach (var run in line.Runs)
                    RenderRun(context, line, run);
            }
        }
    }

    private void RenderBlockDecorations(DrawingContext context, VisualBlock block)
    {
        var md = block.Source;

        for (int q = 0; q < md.QuoteDepth; q++)
        {
            double x = ViewerPadding.Left + q * QuoteIndent;
            context.FillRectangle(QuoteBarBrush, new Rect(x, block.Y, 3, Math.Max(block.Height, 4)));
        }

        if (md.Type == MarkdownBlockType.ThematicBreak)
        {
            double y = block.Y + block.Height / 2;
            context.FillRectangle(RuleBrush,
                new Rect(block.ContentLeft, y, Math.Max(block.ContentRight - block.ContentLeft, 0), 1));
            return;
        }

        if (block.CodeBox)
        {
            context.FillRectangle(CodeBackground,
                new Rect(block.ContentLeft, block.Y,
                    Math.Max(block.ContentRight - block.ContentLeft, 0), block.Height), 4);
        }

        if (block.HeadingRule)
        {
            double y = block.Y + block.Height - 3;
            context.FillRectangle(RuleBrush,
                new Rect(block.ContentLeft, y, Math.Max(block.ContentRight - block.ContentLeft, 0), 1));
        }

        if (block.CheckBox is { } box)
        {
            var pen = new Pen(RuleBrush, 1.2);
            context.DrawRectangle(md.TaskChecked ? LinkBrush : null, pen, box, 3, 3);
            if (md.TaskChecked)
            {
                var check = new StreamGeometry();
                using (var ctx = check.Open())
                {
                    ctx.BeginFigure(new Point(box.X + box.Width * 0.22, box.Y + box.Height * 0.52), false);
                    ctx.LineTo(new Point(box.X + box.Width * 0.42, box.Y + box.Height * 0.74));
                    ctx.LineTo(new Point(box.X + box.Width * 0.80, box.Y + box.Height * 0.28));
                    ctx.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(BackgroundBrush, 1.8), check);
            }
        }

        foreach (var (rect, header) in block.Cells)
        {
            if (header) context.FillRectangle(CodeBackground, rect);
            context.DrawRectangle(null, new Pen(RuleBrush, 1), rect);
        }
    }

    private void RenderSelection(DrawingContext context, VisualLine line, IBrush brush)
    {
        if (_selectionEnd <= _selectionStart) return;
        if (line.End < _selectionStart || line.Start > _selectionEnd) return;

        foreach (var run in line.Runs)
        {
            int start = Math.Max(_selectionStart, run.Start);
            int end = Math.Min(_selectionEnd, run.End);
            if (end <= start) continue;

            double x = run.X;
            double width = run.Width;

            if (run.Image == null && run.Text.Length > 0)
            {
                x += MeasureText(run.Text[..(start - run.Start)], run.Typeface, run.FontSize);
                width = MeasureText(run.Text[(start - run.Start)..(end - run.Start)],
                    run.Typeface, run.FontSize);
            }

            context.FillRectangle(brush, new Rect(x, line.Y, width, line.Height));
        }

        // Show that the line break itself is part of the selection.
        if (_selectionEnd > line.End && line.Runs.Count > 0)
            context.FillRectangle(brush, new Rect(line.Right, line.Y, DefaultFontSize * 0.4, line.Height));
    }

    private void RenderRunBackground(DrawingContext context, VisualLine line, VisualRun run)
    {
        if (!run.IsCode || run.Text.Length == 0) return;
        double y = line.Y + line.Baseline - run.Baseline;
        context.FillRectangle(CodeBackground, new Rect(run.X - 1, y, run.Width + 2, run.Height), 3);
    }

    private void RenderRun(DrawingContext context, VisualLine line, VisualRun run)
    {
        if (run.Image != null)
        {
            context.DrawImage(run.Image,
                new Rect(run.X, line.Y + line.Height - run.Height, run.Width, run.Height));
            return;
        }

        if (run.Text.Length == 0) return;

        double y = line.Y + line.Baseline - run.Baseline;

        var fmt = GetFormatted(run.Text, run.Typeface, run.FontSize, run.Brush);
        context.DrawText(fmt, new Point(run.X, y));

        if (run.Underline)
        {
            double uy = y + run.Baseline + 1.5;
            context.FillRectangle(run.Brush, new Rect(run.X, uy, run.Width, 1));
        }

        if (run.Strike)
        {
            double sy = y + run.Baseline * 0.65;
            context.FillRectangle(run.Brush, new Rect(run.X, sy, run.Width, 1));
        }
    }

    private void RenderScrollBar(DrawingContext context)
    {
        if (!ScrollBarVisible) return;

        double x = Bounds.Width - ScrollBarWidth;
        context.FillRectangle(ScrollTrackBrush, new Rect(x, 0, ScrollBarWidth, _viewportHeight));

        var (thumbY, thumbHeight) = GetThumbMetrics();
        context.FillRectangle(ScrollThumbBrush,
            new Rect(x + 2, thumbY, ScrollBarWidth - 4, thumbHeight), 4);
    }

    private (int first, int last) GetVisibleBlockRange(double top, double bottom)
    {
        if (_blocks.Count == 0) return (0, -1);

        int lo = 0, hi = _blocks.Count - 1, first = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_blocks[mid].Y + _blocks[mid].Height >= top) { first = mid; hi = mid - 1; }
            else lo = mid + 1;
        }

        lo = 0; hi = _blocks.Count - 1;
        int last = hi;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_blocks[mid].Y <= bottom) { last = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        return (Math.Max(0, first - 1), Math.Min(_blocks.Count - 1, last + 1));
    }

    // ---- Hit testing ----

    private int OffsetFromPoint(Point point)
    {
        EnsureLayout();
        if (_blocks.Count == 0) return 0;

        double y = point.Y + _offset;
        var block = BlockAt(y);
        if (block == null) return 0;

        VisualLine? best = null;
        double bestDistance = double.MaxValue;

        foreach (var line in block.Lines)
        {
            double dy = y < line.Y ? line.Y - y
                : y > line.Y + line.Height ? y - (line.Y + line.Height)
                : 0;
            double dx = point.X < line.Left ? line.Left - point.X
                : point.X > line.Right ? point.X - line.Right
                : 0;
            double distance = dy * 1000 + dx;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = line;
            }
        }

        if (best == null) return block.Start;
        return OffsetInLine(best, point.X);
    }

    private VisualBlock? BlockAt(double y)
    {
        if (_blocks.Count == 0) return null;

        int lo = 0, hi = _blocks.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var block = _blocks[mid];
            if (y < block.Y) hi = mid - 1;
            else if (y > block.Y + block.Height) lo = mid + 1;
            else return block;
        }
        return _blocks[Math.Clamp(lo, 0, _blocks.Count - 1)];
    }

    private int OffsetInLine(VisualLine line, double x)
    {
        if (line.Runs.Count == 0) return line.Start;
        if (x <= line.Runs[0].X) return line.Runs[0].Start;

        foreach (var run in line.Runs)
        {
            if (x > run.X + run.Width) continue;
            if (run.Image != null || run.Text.Length == 0)
                return x < run.X + run.Width / 2 ? run.Start : run.End;

            double local = x - run.X;
            int count = FitCount(run.Text, run.Typeface, run.FontSize, local);
            count = Math.Clamp(count, 0, run.Text.Length);

            // Snap to the nearer character boundary.
            if (count < run.Text.Length)
            {
                double before = MeasureText(run.Text[..count], run.Typeface, run.FontSize);
                double after = MeasureText(run.Text[..(count + 1)], run.Typeface, run.FontSize);
                if (local - before > after - local) count++;
            }
            return run.Start + count;
        }

        return line.End;
    }

    private VisualRun? RunAtPoint(Point point)
    {
        EnsureLayout();
        double y = point.Y + _offset;
        var block = BlockAt(y);
        if (block == null) return null;

        foreach (var line in block.Lines)
        {
            if (y < line.Y || y > line.Y + line.Height) continue;
            foreach (var run in line.Runs)
                if (point.X >= run.X && point.X <= run.X + run.Width)
                    return run;
        }
        return null;
    }

    // ---- Mouse ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);

        if (ScrollBarVisible && point.X >= Bounds.Width - ScrollBarWidth)
        {
            var (thumbY, thumbHeight) = GetThumbMetrics();
            if (point.Y >= thumbY && point.Y <= thumbY + thumbHeight)
            {
                _draggingThumb = true;
                _thumbDragStartY = point.Y;
                _thumbDragStartOffset = _offset;
                e.Pointer.Capture(this);
            }
            else
            {
                SetOffset(_offset + (point.Y < thumbY ? -_viewportHeight : _viewportHeight));
            }
            e.Handled = true;
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        Focus(NavigationMethod.Pointer);
        _pressPoint = point;
        _pressedLink = RunAtPoint(point)?.LinkUrl;

        if (!IsSelectionEnabled) return;

        int offset = OffsetFromPoint(point);

        if (e.ClickCount >= 3)
        {
            _selectMode = 2;
            var (lineStart, lineEnd) = LineRangeAt(offset);
            _wordAnchorStart = lineStart;
            _wordAnchorEnd = lineEnd;
            _selectionAnchor = lineStart;
            SetSelection(lineStart, lineEnd);
        }
        else if (e.ClickCount == 2)
        {
            _selectMode = 1;
            var (wordStart, wordEnd) = WordRangeAt(offset);
            _wordAnchorStart = wordStart;
            _wordAnchorEnd = wordEnd;
            _selectionAnchor = wordStart;
            SetSelection(wordStart, wordEnd);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _selectMode = 0;
            SetSelection(_selectionAnchor, offset);
        }
        else
        {
            _selectMode = 0;
            _selectionAnchor = offset;
            SetSelection(offset, offset);
        }

        _mouseSelecting = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);

        if (_draggingThumb)
        {
            var (_, thumbHeight) = GetThumbMetrics();
            double usable = _viewportHeight - thumbHeight;
            if (usable > 0)
                SetOffset(_thumbDragStartOffset + (point.Y - _thumbDragStartY) / usable * MaxOffset);
            e.Handled = true;
            return;
        }

        if (!_mouseSelecting)
        {
            Cursor = RunAtPoint(point)?.LinkUrl != null
                ? new Cursor(StandardCursorType.Hand)
                : new Cursor(StandardCursorType.Ibeam);
            return;
        }

        ExtendSelectionTo(point);
        UpdateAutoScroll(point);
        e.Handled = true;
    }

    private void ExtendSelectionTo(Point point)
    {
        int offset = OffsetFromPoint(point);

        if (_selectMode == 0)
        {
            SetSelection(_selectionAnchor, offset);
            return;
        }

        var (start, end) = _selectMode == 1 ? WordRangeAt(offset) : LineRangeAt(offset);
        SetSelection(Math.Min(_wordAnchorStart, start), Math.Max(_wordAnchorEnd, end));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var point = e.GetPosition(this);
        bool wasSelecting = _mouseSelecting;

        _mouseSelecting = false;
        _draggingThumb = false;
        StopAutoScroll();
        e.Pointer.Capture(null);

        if (_pressedLink is { } link
            && Math.Abs(point.X - _pressPoint.X) < 4 && Math.Abs(point.Y - _pressPoint.Y) < 4
            && RunAtPoint(point)?.LinkUrl == link)
        {
            OnLinkActivated(link);
        }
        _pressedLink = null;

        if (wasSelecting) e.Handled = true;
    }

    private void OnLinkActivated(string url)
    {
        var args = new LinkClickedEventArgs(url);
        LinkClicked?.Invoke(this, args);
        if (args.Handled || !OpenLinksInBrowser) return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https" or "mailto")) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Launching a browser is best-effort; a failure must not crash the app.
        }
    }

    private void UpdateAutoScroll(Point point)
    {
        if (!ScrollBarVisible) { StopAutoScroll(); return; }

        const double edge = 16;
        if (point.Y < edge) _autoScrollDelta = -(edge - point.Y);
        else if (point.Y > _viewportHeight - edge) _autoScrollDelta = point.Y - (_viewportHeight - edge);
        else _autoScrollDelta = 0;

        if (_autoScrollDelta == 0) { StopAutoScroll(); return; }

        if (_autoScrollTimer == null)
        {
            _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _autoScrollTimer.Tick += OnAutoScrollTick;
        }
        if (!_autoScrollTimer.IsEnabled) _autoScrollTimer.Start();
    }

    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        if (!_mouseSelecting) { StopAutoScroll(); return; }
        double before = _offset;
        SetOffset(_offset + Math.Clamp(_autoScrollDelta, -40, 40));
        if (Math.Abs(_offset - before) < 0.001) return;

        var pointer = _autoScrollDelta < 0 ? new Point(_pressPoint.X, 0)
            : new Point(_pressPoint.X, _viewportHeight);
        ExtendSelectionTo(pointer);
    }

    private void StopAutoScroll()
    {
        _autoScrollTimer?.Stop();
        _autoScrollDelta = 0;
    }

    // ---- Keyboard ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.C when ctrl:
            case Key.Insert when ctrl:
                CopySelection();
                e.Handled = true;
                break;

            case Key.A when ctrl:
                SelectAll();
                e.Handled = true;
                break;

            case Key.Escape:
                ClearSelection();
                e.Handled = true;
                break;

            case Key.Up:
                SetOffset(_offset - DefaultFontSize * 2);
                e.Handled = true;
                break;

            case Key.Down:
                SetOffset(_offset + DefaultFontSize * 2);
                e.Handled = true;
                break;

            case Key.PageUp:
                SetOffset(_offset - _viewportHeight * 0.9);
                e.Handled = true;
                break;

            case Key.PageDown:
                SetOffset(_offset + _viewportHeight * 0.9);
                e.Handled = true;
                break;

            case Key.Home when ctrl:
                SetOffset(0);
                e.Handled = true;
                break;

            case Key.End when ctrl:
                SetOffset(MaxOffset);
                e.Handled = true;
                break;
        }
    }

    // ---- Word / line ranges ----

    private (int start, int end) WordRangeAt(int offset)
    {
        EnsureContent();
        string text = _plainText;
        if (text.Length == 0) return (0, 0);

        int position = Math.Clamp(offset, 0, text.Length - 1);
        if (char.IsWhiteSpace(text[position]))
        {
            int wsStart = position;
            while (wsStart > 0 && char.IsWhiteSpace(text[wsStart - 1]) && text[wsStart - 1] != '\n') wsStart--;
            int wsEnd = position;
            while (wsEnd < text.Length && char.IsWhiteSpace(text[wsEnd]) && text[wsEnd] != '\n') wsEnd++;
            return (wsStart, wsEnd);
        }

        bool word = IsWordChar(text[position]);
        int start = position;
        while (start > 0 && IsWordChar(text[start - 1]) == word && !char.IsWhiteSpace(text[start - 1])) start--;
        int end = position;
        while (end < text.Length && IsWordChar(text[end]) == word && !char.IsWhiteSpace(text[end])) end++;
        return (start, end);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private (int start, int end) LineRangeAt(int offset)
    {
        EnsureContent();
        string text = _plainText;
        if (text.Length == 0) return (0, 0);

        int position = Math.Clamp(offset, 0, text.Length);
        int start = position;
        while (start > 0 && text[start - 1] != '\n') start--;
        int end = position;
        while (end < text.Length && text[end] != '\n') end++;
        return (start, end);
    }
}

/// <summary>Event data for <see cref="MarkdownViewer.LinkClicked"/>.</summary>
public class LinkClickedEventArgs : EventArgs
{
    public LinkClickedEventArgs(string url) => Url = url;

    public string Url { get; }

    /// <summary>Set to true to suppress the viewer's default "open in browser" behaviour.</summary>
    public bool Handled { get; set; }
}
