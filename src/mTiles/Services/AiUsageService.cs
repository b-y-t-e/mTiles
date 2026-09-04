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

    /// <summary>How long a good reading may go on standing in for an account that has stopped
    /// answering.</summary>
    /// <remarks><b>Masking is for a bad round, not for a broken account.</b> A logged-out or permanently
    /// unreachable account would otherwise show figures from the moment it last worked for the life of
    /// the process, with its own <c>Problem</c> never reaching the tile — <c>UsageTileViewModel.Rebuild</c>
    /// keeps only <c>Answered</c> reports — and no visual sign that anything is wrong, since
    /// <c>UsageDisplay.Age</c> stamps a reading only once it is older than the shortest window it
    /// describes (five hours for Claude, seven days for a weekly-only account). Five refresh intervals:
    /// long enough that a rate limit, a dropped socket or a token being renewed elsewhere passes
    /// underneath it, short enough that a card cannot be hours wrong without saying so.</remarks>
    public static readonly TimeSpan MaskLimit = TimeSpan.FromTicks(RefreshInterval.Ticks * 5);

    /// <summary>One source's own cache entry: the last answer worth showing, when that answer was
    /// taken, and when the source was last actually asked.</summary>
    /// <remarks><see cref="LastGood"/> is only ever replaced by another <c>Answered</c> report — never by
    /// a failure and never by nothing — which is what keeps a card from going blank the moment its
    /// account has one bad round; <see cref="LastGoodAt"/> is what stops that courtesy becoming a lie,
    /// through <see cref="MaskLimit"/>.</remarks>
    private sealed record Entry(AiUsageReport? LastGood, DateTimeOffset LastGoodAt,
        DateTimeOffset LastAttemptAt);

    private readonly SettingsService _settings;
    private readonly UsageHistory _history;
    private readonly Func<AppSettings, IReadOnlyList<IUsageSource>> _sources;
    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    private IReadOnlyList<AiUsageReport> _reports = [];
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _nextRoundDueAt = DateTimeOffset.MinValue;
    private Task? _inFlight;
    private System.Threading.Timer? _timer;
    private int _watchers;
    private bool _disposed;

    public AiUsageService(SettingsService settings, UsageHistory? history = null,
        Func<AppSettings, IReadOnlyList<IUsageSource>>? sources = null,
        Func<DateTimeOffset>? now = null)
    {
        _settings = settings;
        _history = history ?? new UsageHistory();
        _sources = sources ?? UsageSources.From;
        _now = now ?? (() => DateTimeOffset.Now);
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
    /// Refreshes whatever account is overdue, unless nothing is.
    /// </summary>
    /// <param name="force">True for the manual button. It starts a round however recently the last one
    /// ran — the user has just changed something this cannot see — but it never breaks a single
    /// account's own 3-minute window, which is <see cref="RefreshInterval"/> applied <em>per source</em>
    /// rather than once for the whole round (<c>AskIfDue</c>). Pressing it twice in a row therefore
    /// asks nobody the second time. The timer, having nothing new to react to, is turned away here
    /// instead, so a tick on which every account is still fresh costs one comparison.</param>
    public Task RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        TaskCompletionSource completion;
        var now = _now();

        lock (_gate)
        {
            // A forced refresh *queues behind* the run in flight rather than joining it. Joining is
            // what made the button look broken: a round that began before whatever the user just
            // changed — a sign-in they finished logging into, a key they pasted — answers a different
            // question, and handing them its result reads on screen as a press that did nothing. Not
            // started alongside it either, because two rounds writing _reports race and the loser is
            // whichever finishes last rather than whichever asked last.
            if (_inFlight is { } running) return force ? Queued(running, ct) : running;
            // The round-level early-out, and it is deliberately a *remembered instant* rather than a
            // walk of today's sources: working out who exists reads this machine's own files —
            // .claude.json runs to megabytes — and doing that under _gate would block every reader of
            // Reports and IsRefreshing, the workspace's working light among them, on disk I/O every
            // tick. So the timer is turned away by one comparison, and an account configured since the
            // last round waits at most one tick to be noticed. A forced round is never turned away
            // here: the user has just changed something this cannot see, and what keeps their press
            // from costing anybody a request is the per-source window in AskIfDue, not this.
            if (!force && now < _nextRoundDueAt) return Task.CompletedTask;

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
        _ = RunAsync(completion, now, ct);
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

    private async Task RunAsync(TaskCompletionSource completion, DateTimeOffset now, CancellationToken ct)
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
            var outcomes = await Task.WhenAll(sources.Select(source => AskIfDue(source, now, ct, deadline.Token)));
            var reports = outcomes.Select(outcome => outcome.Report).OfType<AiUsageReport>().ToArray();

            lock (_gate)
            {
                PruneCache(sources);
                _nextRoundDueAt = EarliestDueAt(now);
                _reports = reports;

                // Only when at least one source was actually asked this round: bumping it for a round
                // that asked nobody (every source still within its own window) would tell the tile's
                // "refreshed just now" line that the figures are fresh when they are, at best, the same
                // ones it already had.
                if (outcomes.Any(outcome => outcome.WasAsked))
                    _lastRefresh = now;
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

    /// <summary>One source's outcome for a round: what to draw for it, and whether it was actually
    /// asked — the two <see cref="RunAsync"/> needs and neither can tell you the other.</summary>
    private readonly record struct Outcome(AiUsageReport? Report, bool WasAsked);

    /// <summary>
    /// Asks one source, unless its own 3-minute window is not yet up — and never lets a bad answer
    /// erase a good one.
    /// </summary>
    /// <remarks><b>The per-source throttle this whole class exists to keep.</b> A source within its
    /// window is not asked at all, however the round was started — the manual button included — and
    /// answers with whatever <see cref="Entry.LastGood"/> it is holding, which may be nothing at all for
    /// a source that has never yet answered. A source that <em>is</em> asked and comes back with
    /// nothing usable (null, or a report carrying a <c>Problem</c>) falls back to that same
    /// <see cref="Entry.LastGood"/> rather than replacing it — a card with numbers on it never turns
    /// into an empty one or a bare sentence because one round went badly.</remarks>
    private async Task<Outcome> AskIfDue(IUsageSource source, DateTimeOffset now, CancellationToken ct,
        CancellationToken deadline)
    {
        Entry? entry;
        lock (_gate) _cache.TryGetValue(source.Id, out entry);

        if (entry is not null && now - entry.LastAttemptAt < RefreshInterval)
        {
            Trace.TraceInformation(
                "Usage of '{0}' is still within its 3-minute window; not asked again.", source.Id);
            return new Outcome(StillWorthShowing(entry, now), WasAsked: false);
        }

        var fresh = await Ask(source, ct, deadline);
        var updated = Keep(entry, fresh, now);

        lock (_gate) _cache[source.Id] = updated;

        if (fresh is not null)
        {
            Remember(fresh);
            Explain(fresh);
        }

        if (fresh is not { Answered: true })
            TraceMasking(source.Id, entry, updated);

        return new Outcome(updated.LastGood ?? fresh, WasAsked: true);
    }

    /// <summary>What this round leaves in the cache for one source.</summary>
    /// <remarks>A good answer replaces everything and restamps the age. Anything else keeps the last
    /// good reading only while <see cref="MaskLimit"/> allows, and once it does not the entry is
    /// emptied — which is what lets the account's own <c>Problem</c> reach the caller, and the card
    /// disappear, rather than stale figures standing there for good.</remarks>
    private static Entry Keep(Entry? entry, AiUsageReport? fresh, DateTimeOffset now)
    {
        if (fresh is { Answered: true }) return new Entry(fresh, now, now);

        var masked = entry is null ? null : StillWorthShowing(entry, now);

        return new Entry(masked, masked is null ? now : entry!.LastGoodAt, now);
    }

    /// <summary>The entry's last good reading while it is young enough to stand in for a fresh one, and
    /// null once it is not.</summary>
    private static AiUsageReport? StillWorthShowing(Entry entry, DateTimeOffset now) =>
        entry.LastGood is not null && now - entry.LastGoodAt < MaskLimit ? entry.LastGood : null;

    /// <summary>Says in the log which of the two things just happened to a failing account: its figures
    /// are being held over, or they have been given up on and its own sentence is what the round
    /// carries now.</summary>
    private static void TraceMasking(string sourceId, Entry? before, Entry after)
    {
        if (after.LastGood is { } masked)
            Trace.TraceInformation(
                "Usage of '{0}' could not be refreshed; showing the reading from {1} instead.",
                sourceId, masked.MeasuredAt);
        else if (before?.LastGood is { } expired)
            Trace.TraceWarning(
                "Usage of '{0}' has not answered since {1}, which is longer than {2}; its last reading " +
                "is no longer shown.", sourceId, expired.MeasuredAt, MaskLimit);
    }

    /// <summary>Drops the cache entries for accounts that no longer exist in settings — a removed
    /// sign-in, a deleted provider instance — so the cache does not grow for the life of the
    /// process.</summary>
    /// <remarks>Called with <c>_gate</c> held, by the one place that already holds it.</remarks>
    private void PruneCache(IReadOnlyList<IUsageSource> sources)
    {
        var live = new HashSet<string>(sources.Select(source => source.Id), StringComparer.Ordinal);

        foreach (var staleId in _cache.Keys.Where(id => !live.Contains(id)).ToArray())
            _cache.Remove(staleId);
    }

    /// <summary>The soonest any known account falls out of its own window — what the next round is
    /// allowed to start at.</summary>
    /// <remarks>Called with <c>_gate</c> held. An empty cache means nothing has been asked yet, so the
    /// next round starts at once rather than never.</remarks>
    private DateTimeOffset EarliestDueAt(DateTimeOffset now) =>
        _cache.Count == 0
            ? now
            : _cache.Values.Min(entry => entry.LastAttemptAt) + RefreshInterval;

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
