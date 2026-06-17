using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using MTerminal.Models;

namespace MTerminal.Views;

public partial class DiffView : UserControl
{
    public static readonly StyledProperty<string> DiffTextProperty =
        AvaloniaProperty.Register<DiffView, string>(nameof(DiffText), "");

    public static readonly StyledProperty<string> OldContentProperty =
        AvaloniaProperty.Register<DiffView, string>(nameof(OldContent), "");

    public static readonly StyledProperty<string> NewContentProperty =
        AvaloniaProperty.Register<DiffView, string>(nameof(NewContent), "");

    public static readonly StyledProperty<string> EditorFontFamilyProperty =
        AvaloniaProperty.Register<DiffView, string>(nameof(EditorFontFamily), AppDefaults.FontFamily);

    public static readonly StyledProperty<double> EditorFontSizeProperty =
        AvaloniaProperty.Register<DiffView, double>(nameof(EditorFontSize), 13);

    public static readonly StyledProperty<bool> SplitModeProperty =
        AvaloniaProperty.Register<DiffView, bool>(nameof(SplitMode), false);

    public static readonly StyledProperty<bool> SkipEmptyLinesProperty =
        AvaloniaProperty.Register<DiffView, bool>(nameof(SkipEmptyLines), true);

    public string DiffText { get => GetValue(DiffTextProperty); set => SetValue(DiffTextProperty, value); }
    public string OldContent { get => GetValue(OldContentProperty); set => SetValue(OldContentProperty, value); }
    public string NewContent { get => GetValue(NewContentProperty); set => SetValue(NewContentProperty, value); }
    public string EditorFontFamily { get => GetValue(EditorFontFamilyProperty); set => SetValue(EditorFontFamilyProperty, value); }
    public double EditorFontSize { get => GetValue(EditorFontSizeProperty); set => SetValue(EditorFontSizeProperty, value); }
    public bool SplitMode { get => GetValue(SplitModeProperty); set => SetValue(SplitModeProperty, value); }
    public bool SkipEmptyLines { get => GetValue(SkipEmptyLinesProperty); set => SetValue(SkipEmptyLinesProperty, value); }

    private TextEditor? _unifiedEditor;
    private TextEditor? _leftEditor;
    private TextEditor? _rightEditor;
    private Grid? _splitGrid;
    private bool _syncingScroll;

    public DiffView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RebuildView();
    }

    private void RebuildView()
    {
        if (SplitMode)
            ShowSplitView();
        else
            ShowUnifiedView();
    }

    private void ShowUnifiedView()
    {
        if (_unifiedEditor == null)
        {
            _unifiedEditor = CreateEditor();
            _unifiedEditor.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackgroundRenderer());
        }
        _unifiedEditor.Text = ApplyFilters(DiffText);
        Content = _unifiedEditor;
    }

    private string ApplyFilters(string text)
    {
        if (!SkipEmptyLines) return text;
        var sb = new System.Text.StringBuilder();
        var pastFirst = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            var ch = line[0];
            if (ch is ' ' or '+' or '-' && line.Length <= 1)
                continue;
            if (line.StartsWith("@@"))
            {
                if (pastFirst) sb.AppendLine();
                pastFirst = true;
            }
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private void ShowSplitView()
    {
        if (_splitGrid == null)
        {
            _leftEditor = CreateEditor();
            _leftEditor.TextArea.TextView.BackgroundRenderers.Add(new SideDiffBackgroundRenderer(isOld: true));
            _rightEditor = CreateEditor();
            _rightEditor.TextArea.TextView.BackgroundRenderers.Add(new SideDiffBackgroundRenderer(isOld: false));

            SetupScrollSync(_leftEditor, _rightEditor);

            var splitter = new GridSplitter
            {
                Width = 2,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext
            };
            splitter.Bind(GridSplitter.BackgroundProperty, splitter.GetResourceObservable("BorderSubtle"));

            _splitGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            Grid.SetColumn(_leftEditor, 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(_rightEditor, 2);
            _splitGrid.Children.Add(_leftEditor);
            _splitGrid.Children.Add(splitter);
            _splitGrid.Children.Add(_rightEditor);
        }

        UpdateSplitContent();
        Content = _splitGrid;
    }

    private void SetupScrollSync(TextEditor left, TextEditor right)
    {
        left.TextArea.TextView.ScrollOffsetChanged += (_, _) =>
        {
            if (_syncingScroll) return;
            _syncingScroll = true;
            var offset = left.TextArea.TextView.ScrollOffset;
            right.ScrollToVerticalOffset(offset.Y);
            right.ScrollToHorizontalOffset(offset.X);
            _syncingScroll = false;
        };
        right.TextArea.TextView.ScrollOffsetChanged += (_, _) =>
        {
            if (_syncingScroll) return;
            _syncingScroll = true;
            var offset = right.TextArea.TextView.ScrollOffset;
            left.ScrollToVerticalOffset(offset.Y);
            left.ScrollToHorizontalOffset(offset.X);
            _syncingScroll = false;
        };
    }

    private void UpdateSplitContent()
    {
        if (_leftEditor == null || _rightEditor == null) return;

        var oldText = OldContent ?? "";
        var newText = NewContent ?? "";

        var diff = SideBySideDiffBuilder.Diff(new Differ(), oldText, newText);

        var leftLines = new System.Text.StringBuilder();
        var rightLines = new System.Text.StringBuilder();

        foreach (var line in diff.OldText.Lines)
            leftLines.AppendLine(line.Text ?? "");
        foreach (var line in diff.NewText.Lines)
            rightLines.AppendLine(line.Text ?? "");

        _leftEditor.Text = leftLines.ToString();
        _rightEditor.Text = rightLines.ToString();

        if (_leftEditor.TextArea.TextView.BackgroundRenderers
                .OfType<SideDiffBackgroundRenderer>().FirstOrDefault() is { } leftRenderer)
            leftRenderer.SetLines(diff.OldText.Lines);

        if (_rightEditor.TextArea.TextView.BackgroundRenderers
                .OfType<SideDiffBackgroundRenderer>().FirstOrDefault() is { } rightRenderer)
            rightRenderer.SetLines(diff.NewText.Lines);

        _leftEditor.TextArea.TextView.Redraw();
        _rightEditor.TextArea.TextView.Redraw();
    }

    private TextEditor CreateEditor()
    {
        var editor = new TextEditor
        {
            IsReadOnly = true,
            FontFamily = new FontFamily(EditorFontFamily),
            FontSize = EditorFontSize,
            ShowLineNumbers = false,
            WordWrap = false,
            Padding = new Thickness(8, 4),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        editor.Bind(TextEditor.BackgroundProperty, editor.GetResourceObservable("BgBase"));
        editor.Bind(TextEditor.ForegroundProperty, editor.GetResourceObservable("TextPrimary"));

        return editor;
    }

    private void UpdateEditorFont(TextEditor editor)
    {
        editor.FontFamily = new FontFamily(EditorFontFamily);
        editor.FontSize = EditorFontSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SplitModeProperty)
        {
            RebuildView();
        }
        else if (change.Property == DiffTextProperty || change.Property == SkipEmptyLinesProperty)
        {
            if (!SplitMode && _unifiedEditor != null)
                _unifiedEditor.Text = ApplyFilters(DiffText);
        }
        else if (change.Property == OldContentProperty || change.Property == NewContentProperty)
        {
            if (SplitMode)
                UpdateSplitContent();
        }
        else if (change.Property == EditorFontFamilyProperty || change.Property == EditorFontSizeProperty)
        {
            if (_unifiedEditor != null) UpdateEditorFont(_unifiedEditor);
            if (_leftEditor != null) UpdateEditorFont(_leftEditor);
            if (_rightEditor != null) UpdateEditorFont(_rightEditor);
        }
    }
}
