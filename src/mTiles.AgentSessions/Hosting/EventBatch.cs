using mTiles.AgentSessions.Events;

namespace mTiles.AgentSessions.Hosting;

/// <summary>
/// Shrinks a batch of events before it is stored, without changing what replaying it produces. Pure.
/// </summary>
/// <remarks>
/// A model streams its reply a few characters at a time, and stored as they arrive a long answer is
/// thousands of rows. Consecutive deltas to the same message — or the same thought, or the same tool's
/// output — are the same fold as one delta holding their text, so they are merged and keep the last
/// sequence number. Anything in between breaks the run, because order across different things is part of
/// the record.
/// </remarks>
public static class EventBatch
{
    public static IReadOnlyList<AgentEvent> Coalesce(IReadOnlyList<AgentEvent> batch)
    {
        var result = new List<AgentEvent>(batch.Count);
        foreach (var e in batch)
        {
            if (result.Count > 0 && Merge(result[^1], e) is { } merged)
                result[^1] = merged;
            else
                result.Add(e);
        }

        return result;
    }

    private static AgentEvent? Merge(AgentEvent previous, AgentEvent next) => (previous, next) switch
    {
        (AssistantTextDelta a, AssistantTextDelta b) when a.MessageId == b.MessageId && a.TurnId == b.TurnId =>
            b with { Delta = a.Delta + b.Delta },
        (ReasoningDelta a, ReasoningDelta b) when a.MessageId == b.MessageId && a.TurnId == b.TurnId =>
            b with { Delta = a.Delta + b.Delta },
        (ToolUpdated { Title: null, Detail: null } a, ToolUpdated { Title: null, Detail: null } b)
            when a.ToolCallId == b.ToolCallId && a.OutputDelta is not null && b.OutputDelta is not null =>
            b with { OutputDelta = a.OutputDelta + b.OutputDelta },
        _ => null,
    };
}
