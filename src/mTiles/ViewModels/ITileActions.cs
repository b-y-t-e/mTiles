namespace mTiles.ViewModels;

/// <summary>
/// The action ids this application itself knows about.
/// </summary>
/// <remarks>
/// Only the ones something outside the tile has to be able to name — the header has a Restart button and
/// Ctrl+Shift+R is bound to it, so "restart" has to mean the same thing to the tile that offers it and to
/// the header that draws it. Every other id is private to the kind that offers it, which is the point:
/// the set is open, and a kind added later needs no line here.
/// </remarks>
public static class TileActionIds
{
    public const string Restart = "restart";
}

/// <summary>
/// One thing a tile can be asked to do, by a name that is stable enough to travel.
/// </summary>
/// <param name="Id">What identifies it on the wire and in a header binding. Never shown.</param>
/// <param name="Label">What a person reads.</param>
/// <param name="Icon">An icon name, mapped to a drawing on the view side. A string because the phone
/// needs one on the wire anyway, so a string is what this value actually is.</param>
/// <param name="IsEnabled">Whether it can be done in this tile, in this state, right now.</param>
/// <param name="IsDestructive">Whether doing it can lose the user work. <b>A destructive action is
/// never offered to a phone</b> — not "with a confirmation": confirming on a phone something you cannot
/// see is theatre, and this codebase already holds that an unwired confirmation answers no.</param>
public sealed record TileAction(
    string Id,
    string Label,
    string Icon,
    bool IsEnabled = true,
    bool IsDestructive = false);

/// <summary>Whether an action was carried out, and why not when it was not.</summary>
/// <remarks>A refusal is worth a sentence because the phone is usually the only screen the user is
/// looking at.</remarks>
public sealed record TileActionResult(bool Done, string? Message)
{
    public static TileActionResult Ok { get; } = new(true, null);

    public static TileActionResult Refused(string why) => new(false, why);
}

/// <summary>
/// Tile content that offers named actions — the tile header's buttons, and what a phone may press.
/// </summary>
/// <remarks>
/// <para><see cref="Actions"/> alone could have sat on <see cref="ITile"/> returning an empty list; an
/// empty list is not a lie. But <see cref="InvokeAsync"/> travels with it, and on a tile with no actions
/// that is a method which can only fail. Kept as a capability for that reason. If all six kinds end up
/// implementing it, promoting it into <see cref="ITile"/> is a one-line change; the reverse is not.</para>
/// <para>The list is a snapshot, assembled on the UI thread: a socket thread must never walk a view
/// model tree to build one.</para>
/// </remarks>
public interface ITileActions : ITile
{
    IReadOnlyList<TileAction> Actions { get; }

    /// <summary>Does the action with that id, or says why it did not.</summary>
    /// <remarks>An id this tile does not offer gets a refusal rather than an exception: the caller may
    /// be a network peer working from a snapshot that has since changed.</remarks>
    Task<TileActionResult> InvokeAsync(string id);
}
