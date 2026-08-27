using Avalonia.Threading;
using Terminal.Avalonia;

namespace mTiles.Services;

/// <summary>
/// A terminal's "working" light: on while its child is writing, off once it stops.
/// </summary>
/// <remarks>
/// <para>Output, not the process being alive: a shell sitting at its prompt is alive and doing nothing,
/// which is exactly the state this is meant to tell apart from a build running.</para>
/// <para>Its own type rather than a handful of fields on the tile, because it is a whole mechanism —
/// subscription, smoothing window, expiry timer, teardown — with its own reasons to change (the
/// threshold, the signal it listens to, a second source of activity). The tile only owns one and
/// re-exports what it says.</para>
/// <para>The timer runs only while the light is on. Output is what turns it on, so there is nothing for
/// a timer to expire until some has arrived — and a per-tile timer ticking through an idle evening is a
/// cost every tile would pay for a question nobody is asking.</para>
/// </remarks>
public sealed class OutputActivityLight : IDisposable
{
    private readonly ActivityWindow _window = new();
    private TerminalControl? _terminal;
    private DispatcherTimer? _expiryTimer;
    private bool _isOn;

    /// <summary>Whether output has been seen recently enough to still count as working.</summary>
    public bool IsOn
    {
        get => _isOn;
        private set
        {
            if (_isOn == value) return;
            _isOn = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when <see cref="IsOn"/> turns over, on the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Starts watching a terminal, letting go of whatever was watched before.</summary>
    public void Attach(TerminalControl terminal)
    {
        if (ReferenceEquals(_terminal, terminal)) return;
        Dispose();

        _terminal = terminal;
        terminal.RawOutputReceived += OnRawOutputReceived;
        _expiryTimer = new DispatcherTimer { Interval = ActivityWindow.ExpiryCheckInterval };
        _expiryTimer.Tick += (_, _) => Expire();
    }

    // Raised on the UI thread by the control, which is what lets this drive a timer and an event that
    // a view is bound to without a dispatch of its own.
    private void OnRawOutputReceived(object? sender, ReadOnlyMemory<byte> chunk)
    {
        _window.Stamp(DateTime.UtcNow);
        IsOn = true;

        // Started, not restarted. `Start` on a running DispatcherTimer puts its countdown back to the
        // beginning, and this runs once per chunk of output — so under a steady stream the tick that
        // decides the light is still warranted was pushed away for as long as the stream lasted, and
        // the interval stopped meaning anything. The window in ActivityWindow is what smooths the
        // signal; the timer only has to come round.
        if (_expiryTimer is { IsEnabled: false } timer) timer.Start();
    }

    private void Expire()
    {
        if (_window.IsActive(DateTime.UtcNow)) return;
        IsOn = false;
        _expiryTimer?.Stop();
    }

    /// <summary>Stops watching and puts the light out. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_terminal != null)
            _terminal.RawOutputReceived -= OnRawOutputReceived;
        _terminal = null;

        _expiryTimer?.Stop();
        _expiryTimer = null;
        IsOn = false;
    }
}
