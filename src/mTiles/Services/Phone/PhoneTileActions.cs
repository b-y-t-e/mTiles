using System.Text.Json;
using mTiles.ViewModels;

namespace mTiles.Services.Phone;

/// <summary>
/// What of a tile's actions a paired phone is allowed to see and press.
/// </summary>
/// <remarks>
/// <para><b>This is the single point at which the open action set could quietly become the thing it
/// replaced.</b> The keys a phone can press are a closed enum decided at compile time; actions are not —
/// a kind registered later brings whatever it likes, and Git already has Discard changes and Undo last
/// commit. So the filter lives here, in mTiles, and never in the page: <b>a destructive action is not
/// offered at all</b>. Not "with a confirmation" — confirming on a phone something you cannot see is
/// theatre, and this codebase already holds that an unwired confirmation answers no.</para>
/// <para>It is also stricter than the keys are in one respect: an action is gated on being enabled for
/// this tile in this state, whereas Enter can always be pressed.</para>
/// <para>Pure, and separate from the manager, so the rule is readable in a test without a UI thread, a
/// socket or a tile.</para>
/// </remarks>
internal static class PhoneTileActions
{
    /// <summary>The actions of that tile a phone may be shown.</summary>
    public static IReadOnlyList<TileAction> ForPhone(IReadOnlyList<TileAction> actions) =>
        [.. actions.Where(a => !a.IsDestructive)];

    /// <summary>Whether a phone may press this one, given what the tile offers right now.</summary>
    /// <remarks>Asked again at the moment of the press rather than trusting the snapshot the phone acted
    /// on: its copy is as old as the last state it was told about, and a tile moves through its own
    /// phases without anybody pressing anything.</remarks>
    public static bool IsAllowed(IReadOnlyList<TileAction> actions, string id) =>
        ForPhone(actions).Any(a => a.Id == id && a.IsEnabled);

    /// <summary>The message a phone is sent whenever the active tile or its state changes.</summary>
    public static string Describe(string tileName, IReadOnlyList<TileAction> actions) =>
        JsonSerializer.Serialize(new
        {
            type = "actions",
            tile = tileName,
            actions = ForPhone(actions)
                .Select(a => new { id = a.Id, label = a.Label, icon = a.Icon, enabled = a.IsEnabled })
                .ToArray(),
        });
}
