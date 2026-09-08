using mTiles.Models;
using Terminal.Avalonia;

namespace mTiles.Services.Activity;

/// <summary>
/// The floor: a tile is working while its child is writing.
/// </summary>
/// <remarks>
/// <para>Output, not the process being alive: a shell sitting at its prompt is alive and doing nothing,
/// which is exactly the state this is meant to tell apart from a build running. It is the only source
/// every tile gets, because it is the only one that needs to know nothing about what is running.</para>
/// <para><b>It never answers <see cref="TileActivity.Idle"/>.</b> Silence is not evidence of rest — a
/// tool waiting on a network call is silent too — so the absence of output is left to expire through
/// <see cref="ActivityPolicy.FreshnessOf"/> instead of being asserted here. That is what lets a fresh
/// answer from any higher source overrule it without a fight.</para>
/// <para>This was <c>OutputActivityLight</c>, which owned the light itself. What it kept — the
/// subscription, the smoothing window, the teardown — is here; what it decided is now
/// <see cref="ActivityPolicy"/>'s, so that the one tile-wide answer has one author.</para>
/// </remarks>
public sealed class OutputActivitySource : IActivitySource
{
    /// <summary>How often a still-running child is worth re-reporting.</summary>
    /// <remarks>Output arrives in bursts many times a second and every chunk means the same thing. The
    /// reading is only worth repeating often enough that it never expires while the child is writing,
    /// which is what this is measured against: comfortably inside
    /// <see cref="ActivityWindow.DefaultWindow"/>.</remarks>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

    private TerminalControl? _terminal;
    private DateTime _lastReport = DateTime.MinValue;

    /// <inheritdoc />
    public ActivityAuthority Authority => ActivityAuthority.Output;

    /// <inheritdoc />
    public event EventHandler<ActivityReading>? Reported;

    /// <inheritdoc />
    public void Attach(TerminalControl terminal)
    {
        if (ReferenceEquals(_terminal, terminal)) return;
        Dispose();

        _terminal = terminal;
        terminal.RawOutputReceived += OnRawOutputReceived;
    }

    // Raised on the UI thread by the control, which is what lets this raise an event a view model is
    // bound to without a dispatch of its own.
    private void OnRawOutputReceived(object? sender, ReadOnlyMemory<byte> chunk)
    {
        var now = DateTime.UtcNow;
        if (now - _lastReport < ReportInterval) return;
        _lastReport = now;
        Reported?.Invoke(this, new ActivityReading(TileActivity.Working, Authority, now));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_terminal != null)
            _terminal.RawOutputReceived -= OnRawOutputReceived;
        _terminal = null;
    }
}
