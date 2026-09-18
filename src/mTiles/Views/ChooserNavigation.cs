using Avalonia;

namespace mTiles.Views;

/// <summary>Which card of a wrapped row of cards an arrow key moves to.</summary>
/// <remarks>
/// <para>Pure, and asked with the cards' own rectangles, because the chooser is a <c>WrapPanel</c>: how
/// many cards a row holds is whatever the tile's width allows, so "down" cannot be an index plus a
/// constant. Up and Down go to the nearest row above or below and, in it, to the card whose centre is
/// closest horizontally — the way a grid of icons is walked everywhere else. Left and Right follow the
/// reading order, across the end of a row into the next.</para>
/// <para>At an edge the answer is the card already held rather than a wrap to the far side: a keyboard
/// user who presses Up once too often should not land on the last card.</para>
/// </remarks>
public static class ChooserNavigation
{
    public enum Direction { Left, Right, Up, Down }

    /// <summary>The index to move to from <paramref name="current"/>, or -1 when there are no cards.
    /// </summary>
    public static int Move(IReadOnlyList<Rect> cards, int current, Direction direction)
    {
        if (cards.Count == 0) return -1;
        if (current < 0 || current >= cards.Count) return 0;

        switch (direction)
        {
            case Direction.Left: return Math.Max(0, current - 1);
            case Direction.Right: return Math.Min(cards.Count - 1, current + 1);
        }

        var from = cards[current];
        var below = direction == Direction.Down;

        // The nearest row in that direction: the smallest vertical step to a card whose top is past
        // this one's. Half a card's height is the tolerance, so a row of cards of slightly different
        // heights is still one row.
        var tolerance = from.Height / 2;
        double? rowTop = null;
        foreach (var card in cards)
        {
            var step = below ? card.Y - from.Y : from.Y - card.Y;
            if (step <= tolerance) continue;
            if (rowTop is null || (below ? card.Y < rowTop : card.Y > rowTop)) rowTop = card.Y;
        }
        if (rowTop is null) return current;

        var centre = from.Center.X;
        var best = current;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < cards.Count; i++)
        {
            if (Math.Abs(cards[i].Y - rowTop.Value) > tolerance) continue;
            var distance = Math.Abs(cards[i].Center.X - centre);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }
}
