using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Services;

namespace mTiles.ViewModels;

/// <summary>
/// How long the agent has been at it, as the waiting row beside the waiting row's spinner writes it — one clock for
/// every conversation that waits on one (the Goal tile's run, the Agent tile's turn).
/// </summary>
/// <remarks>
/// <para><b>A <see cref="Stopwatch"/> and not two <see cref="DateTime"/>s</b>: the wall clock moves —
/// daylight saving, an NTP correction, a laptop waking up — and a label that answers "-1:00" or jumps an
/// hour is worse than no label.</para>
/// <para><b>Empty while stopped</b>, which is also when the row showing it is hidden — one property saying
/// one thing, rather than a stale "4:07" kept alive underneath an invisible control.</para>
/// <para><b>The timer is built on first start and kept</b>: a conversation waits many times, and a new timer
/// per wait is a subscription per wait to get wrong. <see cref="DispatcherPriority.Background"/>
/// deliberately — this is a label, and a second's lateness in it costs nothing, while a timer at input
/// priority competes once a second with the transcript that is being appended to.</para>
/// </remarks>
public sealed partial class ElapsedClock : ObservableObject, IDisposable
{
    private readonly Stopwatch _clock = new();
    private DispatcherTimer? _timer;

    /// <summary>The elapsed time as the UI writes it (<see cref="ElapsedDisplay"/>), or empty while stopped.</summary>
    [ObservableProperty] private string _text = "";

    public bool IsRunning => _clock.IsRunning;

    /// <summary>Starts from zero, whether or not it was already running.</summary>
    public void Start()
    {
        _clock.Restart();
        Text = ElapsedDisplay.Format(TimeSpan.Zero);

        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => Text = ElapsedDisplay.Format(_clock.Elapsed));
        _timer.Start();
    }

    public void Stop()
    {
        _clock.Stop();
        _timer?.Stop();
        Text = "";
    }

    public void Dispose() => Stop();
}
