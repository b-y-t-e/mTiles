namespace mTiles.Views;

/// <summary>
/// Which way round an account's line is laid out.
/// </summary>
/// <remarks>
/// <para><b>The tile has two shapes because it is put in two kinds of hole.</b> Across the top of a
/// workspace it is a row of readings and reads as an instrument; in a column beside a terminal there is
/// not room for one window's label, bar and figure side by side, let alone four of them — and what a
/// narrow tile did instead was clip the figure mid-character (<c>13% · 2</c>), which is the one part of
/// the row the design promises to keep. Stacked, every window gets the tile's whole width and the
/// picture comes back with it.</para>
/// <para><b>It is a threshold and not a breakpoint anybody chose.</b> What has to fit is a window's own
/// three parts — the label, a bar short enough still to be worth drawing, and the widest figure the row
/// can carry (<c>13% · 2h 26m</c> at the terminal's own size) — times however many windows the busiest
/// account has, plus what is left on a metered key. An account with two windows therefore stays
/// horizontal in a column where one with four cannot.</para>
/// <para><b>All it answers is which way round.</b> Where each item then goes is
/// <see cref="UsageWindowsPanel"/>'s - a window with a bar takes a line, one answered in money wraps -
/// and what is left on the key is the last item of that same flow rather than something docked to the
/// end of it, which is why nothing here says where it goes any more.</para>
/// <para>Pure, and in <c>Views/</c> for the reason <see cref="TileIcons"/> is: it is an opinion about
/// the drawing, so it is argued in a table test rather than in a code-behind nothing can reach.</para>
/// </remarks>
public static class UsageLayout
{
    /// <summary>
    /// Whether the cards have to stack, given the room they actually have.
    /// </summary>
    /// <param name="contentWidth">The width the cards are drawn in - the tile less its own margins.
    /// Zero or less is a control that has not been measured yet, which stays horizontal: a tile that
    /// starts stacked and springs sideways on its first layout pass is worse than one that never
    /// moves.</param>
    /// <param name="itemsPerAccount">How many things the busiest account puts on its line - its
    /// windows, and what is left on the key where there is such a figure. Nothing to lay out is
    /// nothing to run out of room for.</param>
    /// <param name="hasBarWindows">Whether anything on the tile draws a bar. A row that does needs half
    /// as much again as one answered in money, which is a label and a figure and nothing else - and a
    /// machine whose only account is a metered key would otherwise stack at a width its rows fit in.</param>
    public static bool IsVerticalFor(double contentWidth, int itemsPerAccount, bool hasBarWindows)
    {
        if (contentWidth <= 0 || itemsPerAccount <= 0) return false;

        return contentWidth < itemsPerAccount * (hasBarWindows ? WindowMinWidth : MeteredWindowMinWidth);
    }

    /// <summary>What one window needs before it is worth drawing on a shared line.</summary>
    /// <remarks>The label (<c>7d</c>), a bar of a handful of cells, and the widest figure the row can
    /// carry - <c>13% &#183; 2h 26m</c>, twelve characters of the terminal's own 10px face - plus the gap
    /// to the next window. Below this the bar goes first, which the bar handles by itself, and then the
    /// figure starts being cut, which nothing handles.</remarks>
    private const double WindowMinWidth = 130;

    /// <summary>What a row answered in money needs, which is <c>30d: $18.47</c> and the gap after it -
    /// no bar, so nothing that gives way before the figure does.</summary>
    private const double MeteredWindowMinWidth = 90;
}
