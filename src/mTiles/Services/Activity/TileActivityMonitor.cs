using Avalonia.Threading;
using mTiles.Models;
using Terminal.Avalonia;

namespace mTiles.Services.Activity;

/// <summary>
/// The one object a tile holds in order to know what it is doing: several sources, one answer.
/// </summary>
/// <remarks>
/// <para>The same bargain <c>OutputActivityLight</c> made and the reason it was worth generalising
/// rather than adding fields to the tile: this is a whole mechanism — subscriptions, arbitration, an
/// expiry timer, teardown — with its own reasons to change, and the tile only owns one and re-exports
/// what it says. What is new is that the answer now comes from more than one instrument, so the
/// choosing is somewhere it can be argued: <see cref="ActivityPolicy"/>, pure and beside a table
/// test.</para>
/// <para><b>The timer runs only while there is something to expire.</b> Working and Blocked can decay
/// and a pending fall has to come due, so those keep it running; Idle and Unknown cannot become
/// anything without a source speaking first, and a per-tile timer ticking through an idle evening is a
/// cost every tile would pay for a question nobody is asking. That rule came from the class this
/// replaces and still holds.</para>
/// </remarks>
public sealed class TileActivityMonitor : IDisposable
{
    /// <summary>How often a state that can decay is re-asked. Half of
    /// <see cref="ActivityPolicy.IdleConfirmation"/>: a timer whose period equals the window it is
    /// checking drops every other due date, which is the arithmetic <c>AiUsageService</c> got wrong
    /// once already.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);

    private readonly List<IActivitySource> _sources = [];
    private readonly Dictionary<IActivitySource, ActivityReading> _readings = [];

    private DispatcherTimer? _timer;
    private TerminalControl? _terminal;
    private DateTime? _pendingSince;
    private TileActivity _activity = TileActivity.Unknown;
    private string? _detail;

    /// <summary>What the tile is doing, as everything watching it agrees.</summary>
    public TileActivity Activity
    {
        get => _activity;
        private set
        {
            if (_activity == value) return;
            _activity = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A sentence about the current state, or null. Shown in a tooltip, never as a label.
    /// </summary>
    public string? Detail
    {
        get => _detail;
        private set
        {
            if (_detail == value) return;
            _detail = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when <see cref="Activity"/> or <see cref="Detail"/> moves, on the UI thread.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Takes on a source. Sources are added before the terminal arrives and never removed.
    /// </summary>
    /// <remarks>A tile's set of instruments is fixed by what it is — a shell has one, an agent has
    /// three — and is decided once, in <c>TerminalTileViewModel.ConfigureActivity</c>. Removal would be
    /// a state this has no use for and a stale reading nobody would clear.</remarks>
    public void Add(IActivitySource source)
    {
        _sources.Add(source);
        source.Reported += OnReported;
        if (_terminal is { } terminal) source.Attach(terminal);
    }

    /// <summary>Points every source at a terminal, letting go of whatever was watched before.</summary>
    public void Attach(TerminalControl terminal)
    {
        if (ReferenceEquals(_terminal, terminal)) return;
        _terminal = terminal;
        foreach (var source in _sources) source.Attach(terminal);
    }

    private void OnReported(object? sender, ActivityReading reading)
    {
        if (sender is not IActivitySource source) return;
        _readings[source] = reading;
        Evaluate(DateTime.UtcNow);
    }

    private void Evaluate(DateTime now)
    {
        var readings = _readings.Values.ToArray();
        var decision = ActivityPolicy.Decide(readings, now, Activity, _pendingSince);
        _pendingSince = decision.PendingSince;

        // The state first, then the sentence for the state that was actually settled on. The other
        // order publishes a detail belonging to a state the tile has not reached, which is a tooltip
        // describing something that is not on screen.
        Activity = decision.State;
        Detail = ActivityPolicy.DetailFor(readings, now, decision.State);

        if (NeedsTicking) StartTimer();
        else StopTimer();
    }

    /// <summary>Whether anything can change without a source speaking again.</summary>
    private bool NeedsTicking =>
        _pendingSince is not null || Activity is TileActivity.Working or TileActivity.Blocked;

    private void StartTimer()
    {
        // Started, not restarted. A running timer's countdown is put back to the beginning by Start,
        // and this runs on every reading — so under a steady stream the tick that decides the state is
        // still warranted was pushed away for as long as the stream lasted. The freshness windows in
        // ActivityPolicy are what smooth the signal; the timer only has to come round.
        if (_timer is { IsEnabled: true }) return;
        _timer ??= new DispatcherTimer { Interval = TickInterval };
        if (!_timerWired)
        {
            _timer.Tick += OnTick;
            _timerWired = true;
        }
        _timer.Start();
    }

    private bool _timerWired;

    private void StopTimer() => _timer?.Stop();

    private void OnTick(object? sender, EventArgs e) => Evaluate(DateTime.UtcNow);

    /// <summary>Stops watching and puts every light out. Safe to call more than once.</summary>
    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.Reported -= OnReported;
            source.Dispose();
        }
        _sources.Clear();
        _readings.Clear();

        if (_timer is { } timer)
        {
            timer.Stop();
            if (_timerWired) timer.Tick -= OnTick;
        }
        _timer = null;
        _timerWired = false;
        _terminal = null;
        _pendingSince = null;

        Activity = TileActivity.Unknown;
        Detail = null;
    }
}
