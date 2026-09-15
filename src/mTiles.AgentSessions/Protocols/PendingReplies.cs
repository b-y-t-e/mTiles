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

    /// <summary>Registers a request and waits for its answer.</summary>
    public async Task<T> WaitAsync(string requestId, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _open[requestId] = completion;
        try
        {
            return await completion.Task.WaitAsync(ct);
        }
        finally
        {
            _open.TryRemove(requestId, out _);
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
}
