using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace mTiles.Views;

/// <summary>
/// A tile's view that says where the keyboard goes when its tile is activated.
/// </summary>
/// <remarks>
/// <para>Asked by <see cref="LeafTileView"/> every time the tile is focused on purpose — activated from
/// the keyboard, switched to, built, maximized — and every time a press lands on something in it that
/// cannot take the keyboard itself (the background, the header, a label). One answer per kind, so the
/// keyboard is always somewhere predictable: the prompt of a conversation, the filter of a list, the
/// editor of a note.</para>
/// <para>Answered by the view rather than guessed by walking the tree: the first focusable element in a
/// tile is whatever happens to be drawn first — a tab button, a picker — which is how an Agent tile
/// used to take the keyboard on its conversation chooser instead of its composer.</para>
/// <para>An answer that cannot take focus right now (hidden, disabled, or <c>null</c>) is not a failure:
/// the tile gives the keyboard to its card itself, never to some other control in the view, so focus
/// never stays behind in the tile that was active before. Only a view that does not implement this
/// interface at all is searched for its first focusable element.</para>
/// </remarks>
public interface IFocusTargetView
{
    /// <summary>What should hold the keyboard while this tile is the active one.</summary>
    InputElement? PreferredFocusTarget { get; }
}

/// <summary>Small helpers shared by the views answering <see cref="IFocusTargetView"/>.</summary>
internal static class FocusTargets
{
    /// <summary>Whether <paramref name="element"/> can take the keyboard at this moment.</summary>
    public static bool CanTake(InputElement? element) =>
        element is { Focusable: true, IsEffectivelyVisible: true, IsEffectivelyEnabled: true }
        && Avalonia.Controls.TopLevel.GetTopLevel(element) is not null;

    /// <summary>The selected row of a list if it has one on screen, otherwise the list itself.</summary>
    /// <remarks>A focused list whose selected row is not the focused element starts its arrow keys from
    /// the top rather than from the selection, so the row is the better target when there is one.</remarks>
    public static InputElement ListTarget(ListBox list) =>
        list.SelectedItem is { } selected && list.ContainerFromItem(selected) is InputElement row
            && CanTake(row)
            ? row
            : list;

    /// <summary>Where the keyboard goes in a tile showing <paramref name="view"/>.</summary>
    /// <remarks>A view that answers <see cref="IFocusTargetView"/> gets its target or, when that cannot
    /// take the keyboard right now, <paramref name="fallback"/> — never some other control of its own. Only
    /// a view that does not answer is searched for its first focusable element.</remarks>
    public static InputElement Resolve(Control? view, InputElement fallback)
    {
        if (view is IFocusTargetView answering)
            return CanTake(answering.PreferredFocusTarget) ? answering.PreferredFocusTarget! : fallback;

        return view?.GetSelfAndVisualDescendants().OfType<InputElement>().FirstOrDefault(CanTake) ?? fallback;
    }

    /// <summary>Whether a press on <paramref name="source"/> is one the control under it answers by taking
    /// the keyboard, looking no further up than <paramref name="tile"/>.</summary>
    /// <remarks>Buttons count wherever they are, focusable or not: a header button opening a menu must not
    /// have the menu's focus pulled back into the tile. A press whose visual tree never reaches the tile is
    /// in a popup the tile opened — a picker's list, a flyout — which routes its events through its owner
    /// but has a root of its own; the keyboard belongs to that popup, so it is left alone.</remarks>
    public static bool PressTakesKeyboard(object? source, Visual tile)
    {
        for (var visual = source as Visual; visual != null; visual = visual.GetVisualParent())
        {
            if (visual == tile) return false;
            if (visual is Button) return true;
            if (visual is InputElement { Focusable: true, IsEffectivelyEnabled: true }) return true;
        }
        return source is Visual;
    }
}
