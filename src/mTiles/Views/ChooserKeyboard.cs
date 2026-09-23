using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace mTiles.Views;

/// <summary>A filter box driving a row of chooser cards: typing narrows them, the arrows move the
/// highlighted card, Enter picks it and Escape clears the filter and then leaves.</summary>
/// <remarks>Kept out of <see cref="LeafTileView"/>, which decides only which chooser is on screen; how
/// the keyboard walks it is this class's one concern. The movement itself is
/// <see cref="ChooserNavigation"/>, pure.</remarks>
public sealed class ChooserKeyboard
{
    private readonly TextBox _filter;
    private readonly Func<IEnumerable<Button>> _cards;
    private readonly Button? _backCard;
    private readonly Action _leave;
    private int _currentCard = -1;

    /// <param name="filter">The box typed into.</param>
    /// <param name="cards">The cards of whichever chooser is on screen, Back included.</param>
    /// <param name="backCard">The way out of a step: never filtered out and never highlighted first.</param>
    /// <param name="leave">What Escape on an empty filter does.</param>
    public ChooserKeyboard(TextBox filter, Func<IEnumerable<Button>> cards, Button? backCard, Action leave)
    {
        _filter = filter;
        _cards = cards;
        _backCard = backCard;
        _leave = leave;

        // Tunnel: a TextBox takes the arrows for its caret and marks them handled before a bubbling
        // handler would see them.
        filter.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        filter.TextChanged += (_, _) => Apply();
    }

    /// <summary>Starts the filter afresh.</summary>
    public void Clear() => _filter.Text = "";

    /// <summary>Hides the cards the filter rules out and puts the highlight on the first one left.</summary>
    /// <remarks>Back is never filtered out: it is the way out of the step, not one of its answers. Nor is
    /// it where the highlight starts, or Enter on a step would go back rather than pick.</remarks>
    public void Apply()
    {
        foreach (var card in _cards())
            card.IsVisible = card == _backCard || ModelSearch.Matches(_filter.Text, card.Tag as string);

        var visible = VisibleCards();
        Highlight(visible, visible.FindIndex(b => b != _backCard));
    }

    private List<Button> VisibleCards() => _cards().Where(b => b.IsVisible).ToList();

    private void Highlight(List<Button> visible, int index)
    {
        foreach (var card in _cards()) card.Classes.Set("current", false);
        _currentCard = index;
        if (index < 0 || index >= visible.Count) return;
        visible[index].Classes.Set("current", true);
        visible[index].BringIntoView();
    }

    /// <remarks>Left and Right move between cards only while the filter is empty - with text in it they
    /// are the caret's, which is what anyone correcting a typo expects.</remarks>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;
        var filterEmpty = string.IsNullOrEmpty(_filter.Text);

        if (DirectionOf(e.Key, filterEmpty) is { } direction)
            Move(direction);
        else if (e.Key == Key.Enter)
            PickCurrent();
        else if (e.Key == Key.Escape)
        {
            if (filterEmpty) _leave();
            else Clear();
        }
        else return;

        e.Handled = true;
    }

    private static ChooserNavigation.Direction? DirectionOf(Key key, bool filterEmpty) => key switch
    {
        Key.Left when filterEmpty => ChooserNavigation.Direction.Left,
        Key.Right when filterEmpty => ChooserNavigation.Direction.Right,
        Key.Up => ChooserNavigation.Direction.Up,
        Key.Down => ChooserNavigation.Direction.Down,
        _ => null,
    };

    private void Move(ChooserNavigation.Direction direction)
    {
        var visible = VisibleCards();
        var rects = visible
            .Select(b => new Rect(b.TranslatePoint(default, _filter) ?? default, b.Bounds.Size))
            .ToList();
        Highlight(visible, ChooserNavigation.Move(rects, _currentCard, direction));
    }

    private void PickCurrent()
    {
        var visible = VisibleCards();
        if (_currentCard < 0 || _currentCard >= visible.Count) return;
        var card = visible[_currentCard];
        if (card.Command is { } command && command.CanExecute(card.CommandParameter))
            command.Execute(card.CommandParameter);
    }
}
