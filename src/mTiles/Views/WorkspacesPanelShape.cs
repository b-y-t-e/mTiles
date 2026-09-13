using Avalonia;

namespace mTiles.Views;

/// <summary>The three shapes the list of workspaces takes, chosen by the room its tile is given.</summary>
internal enum WorkspacesPanelShape
{
    /// <summary>A column of rows, each with its name and the branch under it.</summary>
    List,

    /// <summary>A column of initials — the list squeezed narrower than a name.</summary>
    Strip,

    /// <summary>A row of tabs — the list laid along the top or the bottom of the window.</summary>
    Tabs
}

/// <summary>Which shape the list takes in a given size.</summary>
/// <remarks>
/// <para><b>By its size, not by where it was dropped</b>, which is the rule the strip of initials already
/// followed before the list could move: a list dragged to the top is short because that is the size the
/// layout gives it, and a list the user drags tall again beside the layout is a list again without
/// anybody having to tell it so. Nothing is stored, so nothing can disagree with what is on screen.</para>
/// <para><b>Height outranks width.</b> A tile too short for rows cannot be a column of anything, however
/// narrow, so tabs are the answer before the strip is asked about. The threshold sits well above the
/// strip of tabs a drop gives the list and well below the height of the shortest list of rows worth
/// having, so a list resized by hand does not flicker between the two near either.</para>
/// <para>Pure, and argued in a table test, like every other rule here that is an opinion about a layout.</para>
/// </remarks>
internal static class WorkspacesPanelShapes
{
    /// <summary>Below this height the list is a row of tabs.</summary>
    public const double TabsBelowHeight = 120;

    /// <summary>Below this width a list of rows is a strip of initials.</summary>
    public const double StripBelowWidth = 80;

    public static WorkspacesPanelShape For(Size size)
    {
        // A size of nothing is a control not laid out yet, which is not a short one: it stays a list
        // rather than springing into tabs for one frame.
        if (size.Height > 0 && size.Height < TabsBelowHeight) return WorkspacesPanelShape.Tabs;
        if (size.Width > 0 && size.Width < StripBelowWidth) return WorkspacesPanelShape.Strip;
        return WorkspacesPanelShape.List;
    }
}
