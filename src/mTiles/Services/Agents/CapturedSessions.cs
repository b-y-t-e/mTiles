using System.Collections.Concurrent;

namespace mTiles.Services.Agents;

/// <summary>
/// Which sessions the open tiles already hold, for the one strategy where the agent names its own.
/// </summary>
/// <remarks>
/// <para>Only <see cref="SessionStrategy.CapturedAfterStart"/> needs this, and it needs it because the
/// capture is a guess: codex leaves a rollout file behind and the tile works out which one is its own.
/// The file's recorded <c>cwd</c> settles that between workspaces; inside one workspace two codex tiles
/// started moments apart leave two indistinguishable files, and without a record of what is already
/// spoken for both tiles would write down the same id and one conversation would be lost at the next
/// restart.</para>
/// <para>Process-wide and unordered on purpose: the question is "does any open tile hold this", which no
/// single tile can answer, and the tiles asking are on the UI thread and on capture threads at once.
/// Nothing is persisted — a claim describes this session of the application, and a layout already
/// records the ids that outlive it.</para>
/// </remarks>
public static class CapturedSessions
{
    private static readonly ConcurrentDictionary<string, string> Held = new(StringComparer.Ordinal);

    /// <summary>Records that <paramref name="holder"/> holds <paramref name="sessionId"/>.</summary>
    /// <remarks>Keyed by the session and valued by the holder, so a tile re-claiming its own id after a
    /// restart replaces its own entry rather than adding a second one.</remarks>
    public static void Claim(string sessionId, string holder)
    {
        if (sessionId.Length == 0) return;
        Held[sessionId] = holder;
    }

    /// <summary>Gives up every session <paramref name="holder"/> held.</summary>
    /// <remarks>Called when a tile is closed and when it takes a new identity: an id nobody holds has to
    /// become available again, or a workspace reopened twice in one run of the application would refuse
    /// to capture anything.</remarks>
    public static void ReleaseAllOf(string holder)
    {
        foreach (var (sessionId, held) in Held)
            if (held == holder)
                Held.TryRemove(new KeyValuePair<string, string>(sessionId, held));
    }

    /// <summary>Takes <paramref name="sessionId"/> for <paramref name="holder"/> unless another holder
    /// already has it, and says whether it now holds it.</summary>
    /// <remarks>One step on purpose. Asking whether an id is free and claiming it afterwards leaves a
    /// gap two captures can pass through together — two codex tiles restored from one layout run on the
    /// thread pool at the same moment — and both would then write down the same session, which loses one
    /// of the two conversations at the next launch. <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,TValue)"/>
    /// settles it in one operation, and a holder re-taking its own id gets true rather than a second
    /// entry.</remarks>
    public static bool TryClaim(string sessionId, string holder) =>
        sessionId.Length > 0 && Held.GetOrAdd(sessionId, holder) == holder;
}
