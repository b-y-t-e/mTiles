using System.Collections.Concurrent;

namespace mTiles.Services.Agents.Sessions;

/// <summary>
/// Which stored conversation each open Agent tile is showing, so that two tiles cannot be pointed at one.
/// </summary>
/// <remarks>
/// <para><b>It could not happen until a conversation could be chosen.</b> A conversation was the tile's own
/// id, and those are unique by construction; picking one from a list is the first gesture that can aim two
/// tiles at the same events. Two hosts of one conversation each number their events from what the store held
/// when they were built, so they write the same sequence numbers into the same rows and the store's
/// <c>INSERT OR REPLACE</c> keeps whichever arrived last — one of the two conversations is then lost, and
/// nothing on either tile said so.</para>
/// <para><b>The same shape as <see cref="mTiles.Services.Agents.CapturedSessions"/> and for the same
/// reasons</b>: process-wide, because no single tile can answer "does any other tile hold this"; unordered,
/// because the tiles asking are on the UI thread while hosts close on the thread pool; and nothing is
/// persisted, because a hold describes this run of the application and the layout already records what
/// outlives it.</para>
/// <para><b>Taking and asking are one step.</b> Asking whether a conversation is free and holding it
/// afterwards leaves a gap that a workspace restoring several tiles at once passes straight through.</para>
/// </remarks>
public static class OpenConversations
{
    private static readonly ConcurrentDictionary<string, string> Held = new(StringComparer.Ordinal);

    /// <summary>Takes <paramref name="conversationId"/> for <paramref name="holder"/> unless another tile has
    /// it, and says whether it now holds it. A tile re-taking its own gets true.</summary>
    public static bool TryHold(string conversationId, string holder) =>
        conversationId.Length > 0 && holder.Length > 0 && Held.GetOrAdd(conversationId, holder) == holder;

    /// <summary>Whether some tile other than <paramref name="holder"/> is showing this conversation.</summary>
    public static bool IsHeldByAnother(string conversationId, string holder) =>
        Held.TryGetValue(conversationId, out var held) && held != holder;

    /// <summary>Gives up every conversation <paramref name="holder"/> held, apart from
    /// <paramref name="except"/>.</summary>
    /// <remarks>
    /// <para>Called when the tile closes and when it is pointed at another conversation. Without the second, a
    /// tile that had looked at a conversation would go on refusing it to every other tile for the rest of the
    /// session.</para>
    /// <para><paramref name="except"/> is for the one case where a tile leaves a conversation and is not done
    /// with it: deleting one moves the tile off it first and destroys it afterwards, and released in between it
    /// would be offered to another tile in the window where it is still in the store — which would then have its
    /// events and checkpoints deleted under a live host.</para>
    /// <para><b>The holder must not move.</b> Everything here is keyed by the id the tile had when it took the
    /// hold, so a tile that changed its own id would release under the new one and leak the old entry — leaving
    /// a conversation nothing can open for the rest of the session. No kind does that today: "New session",
    /// which is what rotates a tile's id, is offered only on a Terminal agent tile.</para>
    /// </remarks>
    public static void ReleaseAllOf(string holder, string? except = null)
    {
        foreach (var (conversationId, held) in Held)
            if (held == holder && conversationId != except)
                Held.TryRemove(new KeyValuePair<string, string>(conversationId, held));
    }

    /// <summary>Gives up one conversation, if this holder still has it.</summary>
    public static void Release(string conversationId, string holder) =>
        Held.TryRemove(new KeyValuePair<string, string>(conversationId, holder));
}
