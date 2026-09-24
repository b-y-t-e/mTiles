namespace mTiles.ViewModels;

/// <summary>
/// What the main window is called — which is what the taskbar, Alt+Tab, the title bar and the
/// machine's process list all show.
/// </summary>
/// <remarks>
/// <para>The application's own name was the whole of it, and beside the application's own icon that
/// is the one thing the line already said: the icon is a lowercase <c>m</c>, so a taskbar with
/// labels turned on reads "m mTiles" and the fact worth having — <em>which workspace is open</em> —
/// was on screen nowhere at all. The workspace goes first, because the name of the window is read
/// left to right and truncated from the right: a taskbar button 120px wide shows the beginning of
/// this string and nothing else, and the beginning is the half that differs between two windows.</para>
/// <para>Shortening the application's name instead — leaning on the icon's <c>m</c> so the label
/// need only say "tiles" — was considered and rejected. The pairing reads as one word only where
/// the icon sits immediately left of the text on the same baseline, which is one of the eight
/// places this string surfaces in: it is drawn <em>above</em> the text in Alt+Tab and in the GNOME
/// app grid, and stands alone with no icon at all in a GNOME headerbar, in a notification and in
/// <c>ps</c>. Everywhere else the user would be left with a generic word and nothing to search for.
/// The icon is also 16px in a title bar, where the letter is a coloured blob.</para>
/// <para>The name is the panel's own (<see cref="WorkspaceDisplayName"/>), never the stored one, so
/// the title and the row the user clicked cannot disagree about what the same workspace is called.</para>
/// </remarks>
public static class WindowTitle
{
    /// <summary>The application's name, spelled once for every surface that shows it.</summary>
    public const string AppName = "mTiles";

    /// <summary>A plain hyphen with a space either side. It was an em dash, the typographer's choice, but
    /// on a taskbar it is a long bar between two short words, and the owner asked for the short one.</summary>
    private const string Separator = " - ";

    /// <param name="workspaceName">What the open workspace is called, or null/blank when none is.</param>
    /// <param name="version">The build's own version, or null/blank to leave it off.</param>
    /// <remarks>
    /// <para>The version rides on the application's name rather than on the workspace's, and therefore
    /// at the end: the title is truncated from the right, so the part that differs between two windows
    /// stays and the part that is the same in every one of them is the first to go. It is here rather
    /// than in a dialog because it is what a bug report is asked for, and a taskbar button already
    /// carries it.</para>
    /// <para>The window's own <c>Title</c> is bound to this, so setting it in
    /// <c>MainWindow</c>'s constructor is a value the binding overwrites the moment the data context
    /// arrives — which is how the version came to be spelled there and shown nowhere.</para>
    /// </remarks>
    public static string For(string? workspaceName, string? version = null)
    {
        var app = string.IsNullOrWhiteSpace(version) ? AppName : AppName + " " + version.Trim();

        return string.IsNullOrWhiteSpace(workspaceName)
            ? app
            : workspaceName.Trim() + Separator + app;
    }
}
