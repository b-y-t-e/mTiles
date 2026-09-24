using mTiles.AgentSessions.Events;

namespace mTiles.Services.Agents.Sessions.Codex;

/// <summary>
/// The sub-agents' threads one codex conversation spawned: which are working, the collab call that spawned
/// each, the turn each is running and what it last said.
/// </summary>
/// <remarks>
/// Pure bookkeeping, apart from the protocol it is read from: every method answers the event to emit, or
/// null when there is nothing new to say, and the session stamps the turn on it. See
/// <see cref="CodexAppServerSession"/> for the measurements behind the rules.
/// </remarks>
internal sealed class CodexSubAgentThreads
{
    private const string UnnamedTitle = "Sub-agent";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, SubAgentThread> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _spawnCalls = new(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="thread"/> is one this conversation spawned.</summary>
    public bool Knows(string thread)
    {
        lock (_gate) return _children.ContainsKey(thread);
    }

    /// <summary>Notes a thread as ours without saying it is working — codex's <c>thread/started</c>.</summary>
    public void Register(string thread, string? title)
    {
        lock (_gate) RegisterLocked(thread, title);
    }

    /// <summary>The sub-agent is working.</summary>
    /// <param name="again">Say it even when it is already working — the call that spawned it has just been
    /// named. Otherwise one start is said once: codex announces the same one as the item starts, as it completes
    /// and as the child's turn starts, and each would be a row in the conversation's store.</param>
    public SubAgentStarted? Start(string thread, string? title, bool again = false)
    {
        lock (_gate)
        {
            var child = RegisterLocked(thread, title);
            if (child.Working && !again) return null;
            child.Working = true;
            child.LastMessage = null;
            return new SubAgentStarted(thread, child.Title, _spawnCalls.GetValueOrDefault(thread), Background: true);
        }
    }

    /// <summary>The first collab call to name a thread is the one that spawned it; a start is said again for
    /// one already working, so its row can take it over.</summary>
    public SubAgentStarted? NoteSpawnCall(string thread, string callId)
    {
        bool working;
        lock (_gate)
        {
            if (!_spawnCalls.TryAdd(thread, callId)) return null;
            working = _children.TryGetValue(thread, out var known) && known.Working;
        }

        return working ? Start(thread, null, again: true) : null;
    }

    /// <summary>The sub-agent stopped; null when it was not known to be working.</summary>
    public SubAgentEnded? End(string thread, SubAgentOutcome outcome, string? error = null)
    {
        lock (_gate)
        {
            if (!_children.TryGetValue(thread, out var child) || !child.Working) return null;
            child.Working = false;
            child.CodexTurnId = null;
            return new SubAgentEnded(thread, outcome, error ?? child.LastMessage);
        }
    }

    /// <summary>The codex turn a child is running, which is what Stop interrupts.</summary>
    public void NoteTurn(string thread, string? codexTurnId)
    {
        lock (_gate)
            if (_children.TryGetValue(thread, out var child))
                child.CodexTurnId = codexTurnId ?? child.CodexTurnId;
    }

    /// <summary>What a child last said, kept as its result.</summary>
    public void NoteMessage(string thread, string? text)
    {
        lock (_gate)
            if (_children.TryGetValue(thread, out var child))
                child.LastMessage = text ?? child.LastMessage;
    }

    /// <summary>The turns of every child still working, to interrupt.</summary>
    public IReadOnlyList<(string Thread, string Turn)> WorkingTurns()
    {
        lock (_gate)
            return [.. _children.Where(c => c.Value is { Working: true, CodexTurnId: not null })
                .Select(c => (c.Key, c.Value.CodexTurnId!))];
    }

    /// <summary>A readable name out of codex's nickname or agent path.</summary>
    public static string TitleOf(string? nickname, string? path) =>
        nickname is { Length: > 0 }
            ? nickname
            : path?.TrimEnd('/').Split('/')[^1] is { Length: > 0 } leaf ? leaf : UnnamedTitle;

    private SubAgentThread RegisterLocked(string thread, string? title)
    {
        if (_children.TryGetValue(thread, out var known))
        {
            if (title is not null && known.Title == UnnamedTitle) known.Title = title;
            return known;
        }

        var child = new SubAgentThread(title ?? UnnamedTitle);
        _children[thread] = child;
        return child;
    }

    private sealed class SubAgentThread(string title)
    {
        public string Title { get; set; } = title;
        public bool Working { get; set; }
        public string? CodexTurnId { get; set; }
        public string? LastMessage { get; set; }
    }
}
