namespace mTiles.Services;

/// <summary>
/// Runs at most one piece of work per workspace directory at a time, and hands that work the
/// <em>load generation</em> it began under so it can tell whether the workspace it was deciding about
/// is still the one that is loaded.
/// </summary>
/// <remarks>
/// <para>Extracted from <see cref="AgentFileSyncCoordinator"/>, whose callers are all fire-and-forget —
/// a workspace opening, every tile-tree change, the workspace panel's toggle, the global switch — so
/// without serialisation two of them read the same unanswered config, put up two dialogs and race to
/// save the answer. Concurrency has its own reason to change and nothing to do with what the work
/// decides, which is why it is a class rather than four more fields.</para>
/// <para>The generation is the other half: a decision that spans a dialog must not act on an engine
/// table for a workspace that has been unloaded meanwhile, so <see cref="Invalidate"/> moves the number
/// and <see cref="IsCurrent"/> is how the work asks whether it still speaks for the workspace.</para>
/// </remarks>
public sealed class WorkspaceWorkGate
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _generations = new(StringComparer.OrdinalIgnoreCase);
    private bool _closed;

    /// <summary>One workspace's turnstile together with the number of callers holding or waiting on it.
    /// The count is what lets the entry be forgotten — so the table does not grow by one per directory
    /// ever seen — without a caller that has resolved the entry but not yet acquired it finding it
    /// replaced by a second semaphore, which is two pieces of work running side by side.</summary>
    private sealed class Entry
    {
        public readonly SemaphoreSlim Turn = new(1, 1);
        public int Callers;
    }

    /// <summary>True while this workspace's generation is still the one <paramref name="generation"/>
    /// was taken under, and the gate has not been closed.</summary>
    public bool IsCurrent(string workspaceDir, long generation)
    {
        lock (_gate) return !_closed && GenerationOf(workspaceDir) == generation;
    }

    /// <summary>Moves this workspace's generation on, so work already in flight for it stops speaking
    /// for it. Called when a workspace is unloaded.</summary>
    public void Invalidate(string workspaceDir)
    {
        lock (_gate) _generations[workspaceDir] = GenerationOf(workspaceDir) + 1;
    }

    /// <summary>Refuses every later <see cref="RunAsync"/> and makes <see cref="IsCurrent"/> answer no.
    /// Work already running is left to finish; what it must not do afterwards is what
    /// <see cref="IsCurrent"/> is for.</summary>
    public void Close()
    {
        lock (_gate) _closed = true;
    }

    /// <summary>Queues <paramref name="work"/> behind anything already running for this workspace, and
    /// hands it the generation current when it was queued — not when it starts. Work that waits behind a
    /// held turnstile and only begins after an <see cref="Invalidate"/> must carry the generation that
    /// invalidate retired, or its <see cref="IsCurrent"/> checks would pass and it would act on a
    /// workspace nobody has open any more.</summary>
    public async Task RunAsync(string workspaceDir, Func<long, Task> work)
    {
        Entry entry;
        long generation;
        lock (_gate)
        {
            if (_closed) return;
            if (!_entries.TryGetValue(workspaceDir, out entry!))
                _entries[workspaceDir] = entry = new Entry();
            entry.Callers++;
            generation = GenerationOf(workspaceDir);
        }

        try
        {
            await entry.Turn.WaitAsync();
            try
            {
                lock (_gate)
                {
                    if (_closed) return;
                }
                await work(generation);
            }
            finally
            {
                entry.Turn.Release();
            }
        }
        finally
        {
            lock (_gate)
            {
                if (--entry.Callers == 0)
                    _entries.Remove(workspaceDir);
            }
        }
    }

    /// <summary>Read under <see cref="_gate"/>.</summary>
    private long GenerationOf(string workspaceDir) =>
        _generations.TryGetValue(workspaceDir, out var generation) ? generation : 0;
}
