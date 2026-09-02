using System.Diagnostics;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// One asker for the whole application: what every account says is left, refreshed on a window.
/// </summary>
/// <remarks>
/// <para><b>One set of calls however many tiles are looking.</b> Two usage tiles in two workspaces ask
/// the same three services the same question, and asking twice is two calls against somebody's rate
/// limit for one answer — so the reports live here and the tiles read them.</para>
/// <para><b>Nothing here polls a service nobody is looking at.</b> The timer runs only while at least
/// one tile is attached (<see cref="Attach"/>), which is the rule <c>LocalProviderDiscovery</c> follows
/// for the same reason: a dashboard closed on Friday must not spend the weekend authenticating against
/// three services.</para>
/// <para>The failures are the answers — <see cref="IUsageSource.ReadAsync"/> never throws, and the one
/// that somehow does is caught here, because a refresh is fired and forgotten from a timer and an
/// escape from that ends the process.</para>
/// </remarks>
public sealed class AiUsageService : IDisposable
{
    /// <summary>How long an answer is treated as current.</summary>
    /// <remarks>Three minutes: a five-hour window moves by one point in that time, so the figures are
    /// never meaningfully behind — and it is still slow enough to be a courtesy to somebody else's
    /// service, since the timer only runs while a tile is on screen to read the answer.</remarks>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(3);

    /// <summary>How often the timer looks, which is <b>half</b> the interval and not the interval.</summary>
    /// <remarks><b>A timer that ticks exactly as often as the guard allows drops every other tick.</b>
    /// The period runs from one firing to the next, while <c>_lastRefresh</c> is stamped when the work
    /// <em>finishes</em> — so at the following tick the elapsed time is the interval less however long
    /// the round took, the guard says "still current", and the refresh is skipped until the tick after
    /// that. Measured against the intent, a dashboard documented as five minutes old was ten. Halving
    /// the period costs one cheap comparison on the skipped ticks and makes the staleness the interval
    /// again.</remarks>
    private static TimeSpan TimerPeriod => TimeSpan.FromTicks(RefreshInterval.Ticks / 2);

    /// <summary>How long the whole round may take before what has not answered is given up on.</summary>
    /// <remarks>Longer than any single source's own timeout would be worth waiting for, and shorter
    /// than the interval, so a round can never still be running when the next tick arrives — which is
    /// the state in which the manual button had nothing to do but wait.</remarks>
    public static readonly TimeSpan RoundTimeout = TimeSpan.FromSeconds(45);

    /// <summary>How many days a money card shows.</summary>
    public const int HistoryDays = 7;

    private readonly SettingsService _settings;
    private readonly UsageHistory _history;
    private readonly Func<AppSettings, IReadOnlyList<IUsageSource>> _sources;
    private readonly Lock _gate = new();

    private IReadOnlyList<AiUsageReport> _reports = [];
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private Task? _inFlight;
    private System.Threading.Timer? _timer;
    private int _watchers;
    private bool _disposed;

    public AiUsageService(SettingsService settings, UsageHistory? history = null,
        Func<AppSettings, IReadOnlyList<IUsageSource>>? sources = null)
    {
        _settings = settings;
        _history = history ?? new UsageHistory();
        _sources = sources ?? UsageSources.From;
    }

    /// <summary>Raised when the reports have been replaced. Never on the UI thread.</summary>
    public event Action? Changed;

    /// <summary>What every account last said, newest set first drawn.</summary>
    public IReadOnlyList<AiUsageReport> Reports
    {
        get { lock (_gate) return _reports; }
    }

    /// <summary>Whether a refresh is in flight, which is what a tile's working light follows.</summary>
    public bool IsRefreshing
    {
        get { lock (_gate) return _inFlight is not null; }
    }

    /// <summary>The instant the reports were last replaced, or null before the first refresh.</summary>
    public DateTimeOffset? LastRefresh
    {
        get { lock (_gate) return _lastRefresh == DateTimeOffset.MinValue ? null : _lastRefresh; }
    }

    /// <summary>The daily snapshots kept for one account, oldest first.</summary>
    /// <remarks><b>Nothing on screen reads these.</b> The usage tile shows what is <em>left</em> on a
    /// metered key and not what each of the last seven days cost — a row of bars answering a question
    /// nobody was asking. The recording stays because it is the only per-day history that exists at
    /// all (OpenRouter's <c>api/v1/activity</c> answers 403 for an ordinary key), it costs a few
    /// kilobytes, and a row of bars that starts empty is worth having already filled in if it ever
    /// comes back.</remarks>
    public IReadOnlyList<UsageDay> HistoryOf(string sourceId, DateTimeOffset today) =>
        _history.Days(sourceId, today, HistoryDays);

    /// <summary>The first day anything was recorded for one account, or null.</summary>
    public DateOnly? CollectingSince(string sourceId) => _history.CollectingSince(sourceId);

    /// <summary>
    /// Says a tile is looking, and starts the timer if it is the first.
    /// </summary>
    /// <remarks>Disposing the handle is what says it has stopped looking; the last one out stops the
    /// timer. A handle disposed twice counts once, because a tile is disposed by whatever closes it and
    /// nothing guarantees that happens exactly once.</remarks>
    public IDisposable Attach()
    {
        lock (_gate)
        {
            if (++_watchers == 1 && !_disposed)
                _timer = new System.Threading.Timer(_ => Poll(), null, TimerPeriod, TimerPeriod);
        }

        return new Watcher(this);
    }

    /// <summary>
    /// Refreshes unless the last answer is still current.
    /// </summary>
    /// <param name="force">True for the manual button, which must ask again however fresh the figures
    /// are — a user pressing refresh has a reason this cannot see.</param>
    public Task RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        TaskCompletionSource completion;

        lock (_gate)
        {
            // A forced refresh *queues behind* the run in flight rather than joining it. Joining is
            // what made the button look broken: a round that began before whatever the user just
            // changed — a sign-in they finished logging into, a key they pasted — answers a different
            // question, and handing them its result reads on screen as a press that did nothing. Not
            // started alongside it either, because two rounds writing _reports race and the loser is
            // whichever finishes last rather than whichever asked last.
            if (_inFlight is { } running) return force ? Queued(running, ct) : running;
            if (!force && DateTimeOffset.Now - _lastRefresh < RefreshInterval) return Task.CompletedTask;

            // The handle is published *before* the work starts, because the work can finish before it
            // starts: every source answering from a cache makes RunAsync synchronous to its last line,
            // so a handle assigned from its return value is one assigned after the run has already
            // cleared it — and the service then reports a refresh in flight for the rest of the session.
            completion = new TaskCompletionSource();
            _inFlight = completion.Task;
        }

        // Started outside the lock, and that is the point: a run is synchronous up to its first real
        // await, so starting it under _gate held the lock through a source's own work and blocked every
        // reader of Reports and IsRefreshing — the workspace's working light among them.
        _ = RunAsync(completion, ct);
        return completion.Task;
    }

    /// <summary>A forced refresh that waits for the round already running and then asks again.</summary>
    /// <remarks>The wait swallows the earlier round's outcome, because it is not this caller's: what
    /// they asked for is a fresh answer, and a round that failed is still a round that has finished.
    /// </remarks>
    private async Task Queued(Task running, CancellationToken ct)
    {
        try { await running; }
        catch (Exception ex) { Trace.TraceWarning("The refresh being waited for failed: {0}", ex.Message); }

        await RefreshAsync(force: true, ct);
    }

    private async Task RunAsync(TaskCompletionSource completion, CancellationToken ct)
    {
        // A deadline for the whole round, because the answers are published together: one account on a
        // hanging socket held every card at its previous figures for as long as that instance's own
        // timeout allowed - and, while it held them, every press of Refresh joined the stuck round
        // instead of starting one. What a source that overruns costs is its own card, not the tile.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(RoundTimeout);

        try
        {
            var sources = _sources(_settings.Settings);
            var answers = await Task.WhenAll(sources.Select(source => Ask(source, ct, deadline.Token)));
            var reports = answers.OfType<AiUsageReport>().ToArray();

            foreach (var report in reports)
            {
                Remember(report);
                Explain(report);
            }

            lock (_gate)
            {
                _reports = reports;
                _lastRefresh = DateTimeOffset.Now;
            }
        }
        catch (Exception ex)
        {
            // Nobody awaits this task, so an escape from here is an unobserved exception and a tile
            // still showing the previous figures with nothing saying the refresh failed. Enumerating
            // the sources can throw, and a cancelled source throws past Ask, which excludes it —
            // caught here so the last line still runs and the tile rebuilds from what it has.
            Trace.TraceWarning("A usage refresh failed: {0}", ex.Message);
        }
        finally
        {
            lock (_gate) _inFlight = null;
            completion.TrySetResult();
        }

        // After the handle is settled and outside the lock, so a subscriber rebuilding its cards cannot
        // deadlock against a reader of Reports. Wrapped because this runs on a task nobody awaits: an
        // escape from here is an unobserved exception and nothing on screen saying what failed.
        try { Changed?.Invoke(); }
        catch (Exception ex) { Trace.TraceWarning("A usage listener failed: {0}", ex.Message); }
    }

    /// <summary>
    /// Puts the reason an account could not be asked somewhere a person can find it.
    /// </summary>
    /// <remarks><b>The tile draws no card for such an account</b> (see
    /// <c>UsageTileViewModel.Rebuild</c>), so without this the sentence every reader takes trouble to
    /// write — "Nobody is signed in here", "named no limit window this build recognises", "codex's
    /// recent sessions report no limits" — was constructed and thrown away, and the account simply
    /// vanished. The layers underneath log their own failures, but only the ones that are a failed call:
    /// nothing down there knows that eight rollouts in a row carried no reading. Once per round rather
    /// than per read, because that is how often the answer can change.</remarks>
    private void Explain(AiUsageReport report)
    {
        if (report.Problem is { Length: > 0 } problem)
            Trace.TraceWarning("The usage of '{0}' could not be read: {1}", report.SourceName, problem);
    }

    /// <summary>What today cost, where the account answers in money.</summary>
    /// <remarks>The shortest window carrying an amount, because that is the one whose figure is a day's
    /// spending rather than a week's — and a source that reports no money at all contributes no bars,
    /// which is the asymmetry the card states on screen.</remarks>
    private void Remember(AiUsageReport report)
    {
        var today = report.Windows
            .Where(window => window.UsedAmount is not null && window.Length > TimeSpan.Zero)
            .OrderBy(window => window.Length)
            .FirstOrDefault();

        if (today is { UsedAmount: { } amount } && today.Length <= TimeSpan.FromDays(1))
            _history.Record(report.SourceId, report.MeasuredAt, amount);
    }

    /// <summary>One account's answer, and never an exception.</summary>
    /// <remarks>The contract says a source does not throw; this is what keeps a source that breaks it
    /// from costing the other cards their refresh.</remarks>
    /// <param name="ct">The caller's own cancellation — a tile closing, the application shutting down.
    /// Cancelled here means nobody wants the answer, so it travels on out.</param>
    /// <param name="deadline">The caller's cancellation <em>and</em> this round's time limit. A source
    /// cut off by the limit costs its own card and lets the rest of the round be published.</param>
    private static async Task<AiUsageReport?> Ask(IUsageSource source, CancellationToken ct,
        CancellationToken deadline)
    {
        try { return await source.ReadAsync(deadline); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Trace.TraceWarning("Asking an account for its usage failed: {0}", ex.Message);
            return null;
        }
    }

    /// <summary>The timer's own call, which cannot be awaited and must not throw.</summary>
    private void Poll()
    {
        try { _ = RefreshAsync(); }
        catch (Exception ex) { Trace.TraceWarning("A usage refresh could not be started: {0}", ex.Message); }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_watchers == 0 || --_watchers > 0) return;

            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>One tile's claim on the timer.</summary>
    private sealed class Watcher(AiUsageService service) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;

            _released = true;
            service.Release();
        }
    }
}
