namespace mTiles.ViewModels;

/// <summary>
/// Tile content that needs to know when the user has submitted a line in it — Enter pressed in its
/// terminal.
/// </summary>
/// <remarks>
/// <para>Separate from <see cref="IActiveStateTile"/>, which says which tile is being looked at: that is
/// not the same as a tile having been told to do something, and the terminal agent tile needs the second.
/// A <c>/clear</c> or a <c>/resume</c> moves the CLI into another conversation only because it was typed
/// into that tile, while a <c>claude</c> in some other terminal writes into the same store at any
/// moment at all.</para>
/// <para>Told by the view, which is the one place a keystroke is seen: the terminal control reports no
/// input of its own. A line that arrives as text with its Enter already on it — dictation's auto-Enter —
/// never reaches the view as a keystroke, so the tile counts that one itself.</para>
/// </remarks>
public interface IInputSubmissionTile : ITile
{
    /// <summary>The user pressed Enter in this tile. Called on the UI thread.</summary>
    void OnInputSubmitted();
}
