using Avalonia;

namespace mTiles.ViewModels;

/// <summary>
/// Tile content that sits inset from its card and paints that inset itself.
/// </summary>
/// <remarks>
/// <para>Not about theming — every tile is styled, globally, through <c>DynamicResource</c> and
/// <c>ThemeBridge</c>, and there is no capability in that. This is about the one tile that is text
/// against an edge and wants a gap: a terminal. Every other tile's content is its own chrome — bars,
/// lists, a composer — and drawing that inside an inset leaves a square-cornered rectangle floating in a
/// rounded card, with a sliver of card colour round the bottom corners where the two shapes disagree.
/// Those tiles run to the card's edge and take its corners from the clip.</para>
/// <para><see cref="ContentBackground"/> is a literal hex rather than a resource key because it is the
/// terminal's own ANSI background (<c>TerminalTheme.Background</c>), which the UI palette does not
/// derive: the gap has to be the colour of the thing in it or it reads as a frame.</para>
/// </remarks>
public interface ICustomBackgroundTile : ITile
{
    /// <summary>How far the content sits inside the card. No inset at the top either way — the header
    /// is already there.</summary>
    Thickness ContentInset { get; }

    /// <summary>What that inset is painted in, as <c>#rrggbb</c>.</summary>
    string ContentBackground { get; }
}
