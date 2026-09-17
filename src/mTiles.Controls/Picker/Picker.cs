using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace mTiles.Controls;

/// <summary>
/// A button that says what is chosen, and opens a list to change it.
/// </summary>
/// <remarks>
/// <para><b>Why not a ComboBox, and why not an AutoCompleteBox.</b> Both were tried against a provider
/// catalogue of nearly four hundred models and each is wrong in the opposite direction. A ComboBox does
/// not filter — its typing is jump-to-first-letter — so a long list stays a long list. An AutoCompleteBox
/// filters well and draws no affordance at all: a field with a catalogue behind it looks exactly like an
/// empty text box, and opening it with a value already in it narrows the list to the one row the user
/// already has. The way out is the one t3code takes: the trigger is a <i>button</i> that only ever
/// displays, and the typing happens in a search field inside the list. Two jobs, two controls, no
/// argument between them.</para>
/// <para><b>One control for every menu on a strip.</b> The same thing draws a three-row permission menu
/// with a sentence under each mode, a grouped effort menu with <c>Default</c> beside a row, and a model
/// list with a provider rail down its left and a search box at the top. What differs is which of
/// <see cref="PickerOption"/>'s optional parts the caller fills in and whether it passes
/// <see cref="Categories"/> — never a second control, because three lists is three sets of padding,
/// highlight and keyboard handling that drift apart.</para>
/// <para><b>The rows are not a ListBox.</b> A row that cannot be picked has to stay visible, dimmed, with
/// the reason under it — and a disabled <c>ListBoxItem</c> leaves the hit test, which takes the tooltip
/// carrying that reason with it. So the list is an <c>ItemsControl</c>, the click is read off the pointer,
/// and the arrows are handled here.</para>
/// </remarks>
[TemplatePart(PartTrigger, typeof(ToggleButton))]
[TemplatePart(PartPopup, typeof(Popup))]
[TemplatePart(PartSearch, typeof(TextBox))]
[TemplatePart(PartRows, typeof(ItemsControl))]
[TemplatePart(PartScroll, typeof(ScrollViewer))]
public class Picker : TemplatedControl
{
    private const string PartTrigger = "PART_Trigger";
    private const string PartPopup = "PART_Popup";
    private const string PartSearch = "PART_Search";
    private const string PartRows = "PART_Rows";
    private const string PartScroll = "PART_Scroll";
    private const string PartRail = "PART_Rail";

    private ToggleButton? _trigger;
    private Popup? _popup;
    private TextBox? _search;
    private ItemsControl? _rows;
    private ItemsControl? _rail;
    private ScrollViewer? _scroll;
    private PickerRow? _highlighted;

    public static readonly StyledProperty<IEnumerable?> OptionsProperty =
        AvaloniaProperty.Register<Picker, IEnumerable?>(nameof(Options));

    public static readonly StyledProperty<IEnumerable?> CategoriesProperty =
        AvaloniaProperty.Register<Picker, IEnumerable?>(nameof(Categories));

    public static readonly StyledProperty<PickerCategory?> SelectedCategoryProperty =
        AvaloniaProperty.Register<Picker, PickerCategory?>(nameof(SelectedCategory),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    // One-way by default: the control only reads it, and a pick is reported through SelectionRequested so
    // that a choice needing confirmation first (bypass) can never reach the view model by a binding.
    public static readonly StyledProperty<string?> SelectedIdProperty =
        AvaloniaProperty.Register<Picker, string?>(nameof(SelectedId));

    public static readonly StyledProperty<object?> IconProperty =
        AvaloniaProperty.Register<Picker, object?>(nameof(Icon));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<Picker, string?>(nameof(Text));

    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<Picker, string?>(nameof(Placeholder));

    public static readonly StyledProperty<bool> IsSearchEnabledProperty =
        AvaloniaProperty.Register<Picker, bool>(nameof(IsSearchEnabled));

    public static readonly StyledProperty<string?> SearchPlaceholderProperty =
        AvaloniaProperty.Register<Picker, string?>(nameof(SearchPlaceholder), "Search…");

    public static readonly StyledProperty<string?> EmptyTextProperty =
        AvaloniaProperty.Register<Picker, string?>(nameof(EmptyText), "Nothing matches.");

    public static readonly StyledProperty<bool> IsDropDownOpenProperty =
        AvaloniaProperty.Register<Picker, bool>(nameof(IsDropDownOpen));

    public static readonly StyledProperty<double> DropDownWidthProperty =
        AvaloniaProperty.Register<Picker, double>(nameof(DropDownWidth), double.NaN);

    public static readonly StyledProperty<double> MaxDropDownHeightProperty =
        AvaloniaProperty.Register<Picker, double>(nameof(MaxDropDownHeight), 360);

    public static readonly StyledProperty<double> DropDownMinWidthProperty =
        AvaloniaProperty.Register<Picker, double>(nameof(DropDownMinWidth), 200);

    public static readonly StyledProperty<PlacementMode> PlacementProperty =
        AvaloniaProperty.Register<Picker, PlacementMode>(nameof(Placement),
            PlacementMode.BottomEdgeAlignedLeft);

    public static readonly StyledProperty<bool> ShowChevronProperty =
        AvaloniaProperty.Register<Picker, bool>(nameof(ShowChevron), true);

    public static readonly StyledProperty<Func<object, PickerOption?>?> OptionSelectorProperty =
        AvaloniaProperty.Register<Picker, Func<object, PickerOption?>?>(nameof(OptionSelector));

    public static readonly StyledProperty<Func<string?, PickerOption, bool>?> FilterProperty =
        AvaloniaProperty.Register<Picker, Func<string?, PickerOption, bool>?>(nameof(Filter));

    public static readonly StyledProperty<bool> ClearsSearchOnOpenProperty =
        AvaloniaProperty.Register<Picker, bool>(nameof(ClearsSearchOnOpen), true);

    public static readonly StyledProperty<string?> TypedEntryFormatProperty =
        AvaloniaProperty.Register<Picker, string?>(nameof(TypedEntryFormat));

    public static readonly StyledProperty<bool> ClosesOnSelectionProperty =
        AvaloniaProperty.Register<Picker, bool>(nameof(ClosesOnSelection), true);

    /// <summary>Raised when a row the user can pick was picked. The picker changes nothing by itself.</summary>
    /// <remarks>An event and not only a bound <see cref="SelectedId"/>, because several of these choices
    /// are asked about before they take effect — switching an agent to bypass asks a question first — and
    /// a two-way binding that has already written the new value has nothing left to refuse.</remarks>
    public static readonly RoutedEvent<PickerSelectionEventArgs> SelectionRequestedEvent =
        RoutedEvent.Register<Picker, PickerSelectionEventArgs>(
            nameof(SelectionRequested), RoutingStrategies.Bubble);

    /// <summary>The rows to offer, in the order they are drawn. <see cref="PickerOption"/> items.</summary>
    public IEnumerable? Options
    {
        get => GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    /// <summary>The rail down the left, or null for no rail. <see cref="PickerCategory"/> items.</summary>
    public IEnumerable? Categories
    {
        get => GetValue(CategoriesProperty);
        set => SetValue(CategoriesProperty, value);
    }

    public PickerCategory? SelectedCategory
    {
        get => GetValue(SelectedCategoryProperty);
        set => SetValue(SelectedCategoryProperty, value);
    }

    /// <summary>Which row is the current one. Marks it in the list; does not move the trigger's text.</summary>
    public string? SelectedId
    {
        get => GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    /// <summary>Drawn in front of the trigger's text.</summary>
    public object? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>What the trigger says. The caller's, never inferred from the selection: several of these
    /// read as a summary of more than one choice (<c>High · 1M</c>) and one of them has to say
    /// <c>Choose model</c> when nothing is chosen at all.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>What the trigger says when <see cref="Text"/> is empty.</summary>
    public string? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>Whether the list carries a search field. Off for a menu of four.</summary>
    public bool IsSearchEnabled
    {
        get => GetValue(IsSearchEnabledProperty);
        set => SetValue(IsSearchEnabledProperty, value);
    }

    public string? SearchPlaceholder
    {
        get => GetValue(SearchPlaceholderProperty);
        set => SetValue(SearchPlaceholderProperty, value);
    }

    /// <summary>What stands where the rows would be when the search matched nothing.</summary>
    public string? EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public bool IsDropDownOpen
    {
        get => GetValue(IsDropDownOpenProperty);
        set => SetValue(IsDropDownOpenProperty, value);
    }

    public double DropDownWidth
    {
        get => GetValue(DropDownWidthProperty);
        set => SetValue(DropDownWidthProperty, value);
    }

    public double MaxDropDownHeight
    {
        get => GetValue(MaxDropDownHeightProperty);
        set => SetValue(MaxDropDownHeightProperty, value);
    }

    public double DropDownMinWidth
    {
        get => GetValue(DropDownMinWidthProperty);
        set => SetValue(DropDownMinWidthProperty, value);
    }

    /// <summary>Where the list opens relative to the trigger.</summary>
    public PlacementMode Placement
    {
        get => GetValue(PlacementProperty);
        set => SetValue(PlacementProperty, value);
    }

    /// <summary>Whether the trigger draws its chevron. On by default, because the chevron is what says a
    /// list exists at all; off for a trigger that is already obviously a menu.</summary>
    public bool ShowChevron
    {
        get => GetValue(ShowChevronProperty);
        set => SetValue(ShowChevronProperty, value);
    }

    /// <summary>Turns whatever is in <see cref="Options"/> into rows.</summary>
    /// <remarks><b>This is what keeps the control out of its callers' type systems.</b> Without it a host
    /// has to project its own list into <see cref="PickerOption"/> before binding, which means a second
    /// collection kept in step with the first. With it, <see cref="Options"/> can be the host's own
    /// models and this is the one line that says how one of them reads. An item that is already a
    /// <see cref="PickerOption"/> needs no selector; an item the selector answers null for is left out.</remarks>
    public Func<object, PickerOption?>? OptionSelector
    {
        get => GetValue(OptionSelectorProperty);
        set => SetValue(OptionSelectorProperty, value);
    }

    /// <summary>What the search field means. <see cref="PickerSearch"/>'s rule when nothing is given.</summary>
    /// <remarks>A property because matching is an opinion about the list being matched: a catalogue of
    /// model ids wants punctuation forgiven, a list of people's names does not.</remarks>
    public Func<string?, PickerOption, bool>? Filter
    {
        get => GetValue(FilterProperty);
        set => SetValue(FilterProperty, value);
    }

    /// <summary>Whether opening the list empties the search field. On by default - see the remarks on
    /// <see cref="OnDropDownOpenChanged"/> - and off for a picker somebody reopens to refine a search.</summary>
    public bool ClearsSearchOnOpen
    {
        get => GetValue(ClearsSearchOnOpenProperty);
        set => SetValue(ClearsSearchOnOpenProperty, value);
    }

    /// <summary>How the row offering the search text itself reads (<c>Use "{0}"</c>), or null for no such row.</summary>
    /// <remarks>For a list that is a suggestion rather than the whole answer: a model a provider serves and
    /// does not list, or an agent that reports no catalogue at all, would otherwise be a picker with nothing
    /// in it and no way to name the one model the user already knows. The row is left out when an option
    /// already carries exactly that id, so the same model is never offered twice.</remarks>
    public string? TypedEntryFormat
    {
        get => GetValue(TypedEntryFormatProperty);
        set => SetValue(TypedEntryFormatProperty, value);
    }

    /// <summary>Whether picking closes the list. Off for a list of switches somebody sets several of.</summary>
    public bool ClosesOnSelection
    {
        get => GetValue(ClosesOnSelectionProperty);
        set => SetValue(ClosesOnSelectionProperty, value);
    }

    public event EventHandler<PickerSelectionEventArgs>? SelectionRequested
    {
        add => AddHandler(SelectionRequestedEvent, value);
        remove => RemoveHandler(SelectionRequestedEvent, value);
    }

    /// <summary>What the list draws: <see cref="PickerRow"/> and <see cref="PickerHeading"/>, in order.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    /// <summary>The rail, as the template's own items.</summary>
    public ObservableCollection<PickerCategory> Rail { get; } = [];

    /// <summary>Whether anything at all is being offered, so the template can say so instead of drawing nothing.</summary>
    /// <remarks>A property rather than a binding to the collection's count: a search that matches nothing
    /// has to say so, and an empty list that simply draws nothing reads as a list that failed to load.</remarks>
    public static readonly DirectProperty<Picker, bool> HasRowsProperty =
        AvaloniaProperty.RegisterDirect<Picker, bool>(nameof(HasRows), picker => picker.HasRows);

    private bool _hasRows;

    public bool HasRows
    {
        get => _hasRows;
        private set => SetAndRaise(HasRowsProperty, ref _hasRows, value);
    }

    /// <summary>What the trigger actually draws: <see cref="Text"/>, or <see cref="Placeholder"/> while it is empty.</summary>
    public static readonly DirectProperty<Picker, string?> TriggerTextProperty =
        AvaloniaProperty.RegisterDirect<Picker, string?>(nameof(TriggerText), picker => picker.TriggerText);

    private string? _triggerText;

    public string? TriggerText
    {
        get => _triggerText;
        private set => SetAndRaise(TriggerTextProperty, ref _triggerText, value);
    }

    /// <summary>The list the picker is subscribed to, held so the subscription can end with the control's
    /// time on screen rather than with the view model's lifetime.</summary>
    private INotifyCollectionChanged? _watched;

    /// <summary>What has been typed into the search field.</summary>
    /// <remarks>Held here rather than read off the box, so <see cref="Rebuild"/> is the same whether the
    /// list was reopened, the rail was clicked or a key was pressed.</remarks>
    protected string SearchText { get; private set; } = string.Empty;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (_search is not null) _search.TextChanged -= SearchChanged;
        if (_rows is not null) _rows.RemoveHandler(PointerPressedEvent, RowPressed);
        if (_rail is not null) _rail.RemoveHandler(PointerPressedEvent, RailPressed);

        _trigger = e.NameScope.Find<ToggleButton>(PartTrigger);
        _popup = e.NameScope.Find<Popup>(PartPopup);
        _search = e.NameScope.Find<TextBox>(PartSearch);
        _rows = e.NameScope.Find<ItemsControl>(PartRows);
        _rail = e.NameScope.Find<ItemsControl>(PartRail);
        _scroll = e.NameScope.Find<ScrollViewer>(PartScroll);

        if (_search is not null) _search.TextChanged += SearchChanged;
        // Tunnel: the row's own content may be a control that handles the press first.
        _rows?.AddHandler(PointerPressedEvent, RowPressed, RoutingStrategies.Tunnel);
        _rail?.AddHandler(PointerPressedEvent, RailPressed, RoutingStrategies.Tunnel);
        DrawRail();
        Rebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == OptionsProperty)
        {
            if (this.IsAttachedToVisualTree()) Watch(change.GetNewValue<IEnumerable?>());
            Rebuild();
        }
        else if (change.Property == TextProperty || change.Property == PlaceholderProperty)
        {
            TriggerText = string.IsNullOrEmpty(Text) ? Placeholder : Text;
        }
        else if (change.Property == CategoriesProperty)
        {
            DrawRail();
            Rebuild();
        }
        // OptionSelector and Filter are the two a host sets from code-behind, after the markup has already
        // set Options — XAML has nowhere to put a function. Left out here, a picker whose items are the
        // host's own models drew an empty list until something else happened to rebuild it.
        else if (change.Property == SelectedCategoryProperty || change.Property == SelectedIdProperty
                 || change.Property == OptionSelectorProperty || change.Property == FilterProperty
                 || change.Property == TypedEntryFormatProperty)
        {
            Rebuild();
        }
        else if (change.Property == IsDropDownOpenProperty)
        {
            OnDropDownOpenChanged(change.GetNewValue<bool>());
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Watch(Options);
        // The list may have moved while nothing was listening to it.
        Rebuild();
    }

    /// <summary>Lets go of the view model's list, which outlives this control whenever a tile's view is rebuilt.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Watch(null);
    }

    /// <summary>Follows a list that changes while it is open — a catalogue arriving from a provider.</summary>
    private void Watch(IEnumerable? current)
    {
        if (_watched is not null) _watched.CollectionChanged -= OptionsChanged;
        _watched = current as INotifyCollectionChanged;
        if (_watched is not null) _watched.CollectionChanged += OptionsChanged;
    }

    private void OptionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void SearchChanged(object? sender, TextChangedEventArgs e)
    {
        SearchText = _search?.Text ?? string.Empty;
        Rebuild();
    }

    private void OnDropDownOpenChanged(bool open)
    {
        if (open)
        {
            // Empty, by default and every time. The list filters on this text, so reopening it with the
            // last search still in it answers "what are my options" with whatever was typed a week ago.
            if (ClearsSearchOnOpen)
            {
                SearchText = string.Empty;
                if (_search is not null) _search.Text = string.Empty;
            }

            Rebuild();
            Highlight(Rows.OfType<PickerRow>().FirstOrDefault(row => row.IsSelected)
                      ?? Rows.OfType<PickerRow>().FirstOrDefault(row => row.CanBePicked));
            // Posted: the popup's tree is being realised in this same pass, and focus cannot be given to a
            // control that has not been laid out yet.
            Dispatcher.UIThread.Post(() => _search?.Focus(), DispatcherPriority.Loaded);
        }
        else
        {
            Highlight(null);
            if (_trigger is not null) _trigger.IsChecked = false;
        }
    }

    private void DrawRail()
    {
        Rail.Clear();
        if (Categories is null) return;
        foreach (var category in Categories.OfType<PickerCategory>()) Rail.Add(category);
        SelectedCategory ??= Rail.FirstOrDefault();
    }

    /// <summary>Rebuilds the drawn list from the options, the search and the rail's selection.</summary>
    /// <remarks>Whole, rather than by difference: the lists here are tens of rows and the alternative is a
    /// reconciliation that has to get headings right as groups empty out under a filter.</remarks>
    private void Rebuild()
    {
        var highlightedId = _highlighted?.Option.Id;
        _highlighted = null;
        Rows.Clear();
        if (Options is not null)
        {
            var read = OptionSelector;
            var matches = Filter ?? PickerSearch.Matches;
            string? heading = null;
            foreach (var item in Options)
            {
                // Already a row, or the host's own model read through its one line. Null is "not a row",
                // which is how a caller filters without building a second collection.
                if ((item as PickerOption ?? (read is null ? null : read(item))) is not { } option) continue;

                if (SelectedCategory is { } category
                    && option.CategoryId is not null
                    && option.CategoryId != category.Id) continue;
                if (!matches(SearchText, option)) continue;

                // A heading is drawn when the group changes, so the caller's order is the whole of the
                // grouping and a group emptied by the filter takes its heading with it.
                if (option.Group != heading)
                {
                    heading = option.Group;
                    if (!string.IsNullOrEmpty(heading)) Rows.Add(new PickerHeading(heading));
                }

                Rows.Add(new PickerRow(option) { IsSelected = option.Id == SelectedId });
            }
        }

        AddTypedEntry();
        HasRows = Rows.Count > 0;
        if (IsDropDownOpen) KeepHighlight(highlightedId);
    }

    /// <summary>Offers the search text itself as a row, when <see cref="TypedEntryFormat"/> asks for one.</summary>
    private void AddTypedEntry()
    {
        var typed = SearchText.Trim();
        if (TypedEntryFormat is not { } format || typed.Length == 0) return;
        if (Rows.OfType<PickerRow>().Any(row => row.Option.Id == typed)) return;

        Rows.Add(new PickerRow(new PickerOption
        {
            Id = typed,
            Title = string.Format(format, typed),
            IsAction = true,
        }));
    }

    /// <summary>Puts the highlight back on a row that is actually in the rebuilt list.</summary>
    /// <remarks>The rows are new objects after every rebuild, so a highlight held across one points at a row
    /// nobody can see - and Enter would pick it. The same option keeps it if the search left it in; otherwise
    /// it moves to the first row that can be picked.</remarks>
    private void KeepHighlight(string? highlightedId)
    {
        var pickable = Rows.OfType<PickerRow>().Where(row => row.CanBePicked).ToList();
        Highlight(pickable.FirstOrDefault(row => row.Option.Id == highlightedId) ?? pickable.FirstOrDefault());
    }

    private void RowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Find(e.Source as Visual) is not { } row) return;
        e.Handled = true;
        Pick(row);
    }

    private void RailPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Find<PickerCategory>(e.Source as Visual) is not { } category) return;
        e.Handled = true;
        SelectedCategory = category;
        // The search survives the rail: somebody typing a name and then narrowing to one provider is
        // asking two halves of one question, and emptying the field here would throw the first half away.
    }

    /// <summary>What a press landed in, by walking up from whatever was actually hit.</summary>
    private static T? Find<T>(Visual? from) where T : class
    {
        for (var visual = from; visual is not null; visual = visual.GetVisualParent())
            if (visual is Control control && control.DataContext is T found) return found;
        return null;
    }

    private static PickerRow? Find(Visual? from) => Find<PickerRow>(from);

    private void Pick(PickerRow row)
    {
        // A refused row still closes nothing and says nothing new: its reason is already under it, which is
        // the whole reason it is drawn rather than left out.
        if (!row.CanBePicked) return;

        if (ClosesOnSelection) IsDropDownOpen = false;
        RaiseEvent(new PickerSelectionEventArgs(SelectionRequestedEvent, row.Option));
    }

    /// <summary>Takes the list's keys on their way down, before whatever has the focus sees them.</summary>
    /// <remarks>Tunnel, not <c>OnKeyDown</c>: with search off the focus stays on the trigger, a
    /// <see cref="ToggleButton"/> that reads Enter as a click and handles it — so a bubbling handler here never
    /// heard it, and Enter closed the list without picking the highlighted row.</remarks>
    public Picker() => AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsDropDownOpen) return;

        switch (e.Key)
        {
            case Key.Escape:
                IsDropDownOpen = false;
                e.Handled = true;
                return;
            case Key.Enter:
                if (_highlighted is { } picked) Pick(picked);
                e.Handled = true;
                return;
            case Key.Down:
                Step(1);
                e.Handled = true;
                return;
            case Key.Up:
                Step(-1);
                e.Handled = true;
                return;
        }
    }

    /// <summary>Moves the highlight by one pickable row, skipping headings and refusals.</summary>
    /// <remarks>It does not wrap. A list that jumps from its end back to its start looks, at the moment it
    /// happens, exactly like a list that scrolled — and this one has headings in it, so there is nothing at
    /// the top to recognise as the top.</remarks>
    private void Step(int by)
    {
        var pickable = Rows.OfType<PickerRow>().Where(row => row.CanBePicked).ToList();
        if (pickable.Count == 0) return;

        var at = _highlighted is null ? -1 : pickable.IndexOf(_highlighted);
        var next = Math.Clamp(at + by, 0, pickable.Count - 1);
        if (at < 0) next = by > 0 ? 0 : pickable.Count - 1;
        Highlight(pickable[next]);
    }

    private void Highlight(PickerRow? row)
    {
        if (_highlighted is not null) _highlighted.IsHighlighted = false;
        _highlighted = row;
        if (row is null) return;

        row.IsHighlighted = true;
        BringIntoView(row);
    }

    /// <summary>Scrolls the highlighted row into view, if the template has a scroller and a container yet.</summary>
    private void BringIntoView(PickerRow row)
    {
        if (_rows is null || _scroll is null) return;
        var container = _rows.GetRealizedContainers()
            .FirstOrDefault(control => ReferenceEquals(control.DataContext, row));
        container?.BringIntoView();
    }
}

/// <summary>A row the user picked.</summary>
public sealed class PickerSelectionEventArgs(RoutedEvent routedEvent, PickerOption option)
    : RoutedEventArgs(routedEvent)
{
    public PickerOption Option { get; } = option;
}
