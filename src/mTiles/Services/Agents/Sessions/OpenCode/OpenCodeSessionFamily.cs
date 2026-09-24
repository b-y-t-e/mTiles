using System.Collections.Concurrent;

namespace mTiles.Services.Agents.Sessions.OpenCode;

/// <summary>
/// Which opencode sessions are this conversation's: its own, and every sub-agent's session opened under it.
/// </summary>
/// <remarks>
/// A <c>task</c> tool's sub-agent is a child session asking in its own name, so a request filtered to our
/// session id alone was dropped and the sub-agent waited for ever. A session is ours if its <c>parentID</c>
/// chain reaches ours — noted as sessions are announced, or asked of the server (<paramref name="parentOf"/>)
/// for one opened before the stream saw it. Each answer is remembered, a stranger's included — but never a
/// lookup that failed: a moment's timeout remembered as "not ours" would refuse every later request of that
/// sub-agent, which then waits for ever.
/// </remarks>
/// <param name="parentOf">The server's answer to "what is this session's parent", or null where it could not
/// be asked.</param>
public sealed class OpenCodeSessionFamily(Func<string, Task<SessionParent?>> parentOf)
{
    private const int MaxHops = 8;

    // Each session met, with whether it is ours: true once its chain reaches ours, false once known not to.
    private readonly ConcurrentDictionary<string, bool> _known = new(StringComparer.Ordinal);

    /// <summary>This conversation's own session, once opened.</summary>
    public string? Root { get; set; }

    /// <summary>A session announced with its parent, noted as ours when the parent is.</summary>
    public void Note(string id, string parent)
    {
        if (IsKnownOurs(parent)) _known[id] = true;
    }

    /// <summary>Whether <paramref name="session"/> is ours, walking its parents on the server where not known.
    /// </summary>
    public async Task<bool> IsOursAsync(string session) => await WhoseAsync(session) == true;

    /// <summary>Whether <paramref name="session"/> is ours — or null where the server could not be asked, which
    /// is a question to ask again rather than an answer.</summary>
    public async Task<bool?> WhoseAsync(string session)
    {
        var chain = new List<string>();
        var current = session;
        for (var hop = 0; hop < MaxHops && current is not null; hop++)
        {
            if (IsKnownOurs(current))
            {
                foreach (var child in chain) _known[child] = true;
                return true;
            }

            if (_known.TryGetValue(current, out var ours) && !ours) break;
            chain.Add(current);
            if (await parentOf(current) is not { } answer) return null;
            current = answer.ParentId;
        }

        foreach (var stranger in chain) _known.TryAdd(stranger, false);
        return false;
    }

    private bool IsKnownOurs(string session) => session == Root || _known.GetValueOrDefault(session);
}

/// <summary>What the server said a session's parent is — <paramref name="ParentId"/> null for a session with
/// none.</summary>
public sealed record SessionParent(string? ParentId);
