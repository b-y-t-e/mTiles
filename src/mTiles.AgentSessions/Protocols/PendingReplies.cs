using System.Collections.Concurrent;

namespace mTiles.AgentSessions.Protocols;

/// <summary>
/// Questions an agent is blocked on, each waiting for the user's answer.
/// </summary>
/// <remarks>
/// Every protocol here asks the same way underneath — a request that is not answered until the user
/// says something — so every session keeps its open requests in one of these. <see cref="AbandonAll"/>
/// is the rule that matters: when a turn or a session ends, whatever is still waiting gets the answer
/// that stops it, so no agent sits on a request whose button has gone from the screen.
/// </remarks>
/// <typeparam name="T">The answer.</typeparam>
public sealed class PendingReplies<T>
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<T>> _open = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _heldBySubAgents = new(StringComparer.Ordinal);

    /// <summary>Registers a request and waits for its answer.</summary>
    /// <param name="bySubAgent">Whether a sub-agent asked rather than the agent — such a request outlives the
    /// turn, see <see cref="AbandonTurn"/>.</param>
    public async Task<T> WaitAsync(string requestId, CancellationToken ct, bool bySubAgent = false)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (bySubAgent) _heldBySubAgents[requestId] = 0;
        _open[requestId] = completion;
        try
        {
            return await completion.Task.WaitAsync(ct);
        }
        finally
        {
            _open.TryRemove(requestId, out _);
            _heldBySubAgents.TryRemove(requestId, out _);
        }
    }

    /// <summary>Answers a request. False when nothing is waiting under that id any more.</summary>
    public bool Resolve(string requestId, T answer) =>
        _open.TryRemove(requestId, out var completion) && completion.TrySetResult(answer);

    /// <summary>Answers every open request with <paramref name="answer"/>.</summary>
    public void AbandonAll(T answer)
    {
        foreach (var id in _open.Keys) Resolve(id, answer);
    }

    /// <summary>Answers every open request the agent itself made with <paramref name="answer"/>.</summary>
    /// <remarks>For the end of a turn: the turn's own requests go, a sub-agent's — which a background one is
    /// still waiting on — stay, and the reducer keeps them on screen by the same rule.</remarks>
    public void AbandonTurn(T answer)
    {
        foreach (var id in _open.Keys)
            if (!_heldBySubAgents.ContainsKey(id)) Resolve(id, answer);
    }
}
