using System.Globalization;
using mTiles.Models;
using Terminal.Avalonia;
using Terminal.Emulation;

namespace mTiles.Services.Activity;

/// <summary>
/// Reads the progress the child reported (OSC 9;4) as a state.
/// </summary>
/// <remarks>
/// <para><b>The one source here that knows nothing about agents, and the only one besides raw output
/// that every tile gets.</b> A progress report says "I am working" in a way no CLI has to be recognised
/// for: Claude Code sends it under <c>terminalProgressBarEnabled</c>, and so do <c>npm</c>,
/// <c>cargo</c>, <c>winget</c> and anything else that ever wanted a taskbar. So a plain shell tile gets
/// a real answer about a build running in it, which nothing else on this layer could give it.</para>
/// <para><b>It is ranked with the title (<see cref="ActivityAuthority.Osc"/>) because it is the same
/// kind of evidence</b> — something the child chose to send rather than something we noticed about it —
/// and the two behave as one instrument: within a rank the newer reading wins, which is what a tool
/// that sets both a title and a progress bar needs.</para>
/// <para><b>Two of the five states answer nothing on purpose.</b> Error and Warning say what became of
/// the job, not whether the tile is busy — a tool can report either and carry on, or report either and
/// stop — so they fall through to whatever the output light says rather than asserting a state that
/// would silence it. Only <see cref="TerminalProgressState.None"/> is an affirmative "finished", and it
/// is the one report here allowed to put a light out.</para>
/// </remarks>
public sealed class TerminalProgressSource : IActivitySource
{
    private TerminalControl? _terminal;

    /// <inheritdoc />
    public ActivityAuthority Authority => ActivityAuthority.Osc;

    /// <inheritdoc />
    public event EventHandler<ActivityReading>? Reported;

    /// <inheritdoc />
    public void Attach(TerminalControl terminal)
    {
        if (ReferenceEquals(_terminal, terminal)) return;
        Dispose();

        _terminal = terminal;
        terminal.ProgressChanged += OnProgressChanged;

        // A job already in flight when this attached is still in flight, and the control re-raises
        // nothing. The same reason the title source reads Title on attach: a tile restored into a
        // running session would otherwise read as having said nothing until the job's next report —
        // which, for an indeterminate one, may be never.
        if (terminal.Progress.IsRunning) Report(terminal.Progress);
    }

    private void OnProgressChanged(object? sender, TerminalProgress progress) => Report(progress);

    private void Report(TerminalProgress progress)
    {
        var state = StateOf(progress.State);
        if (state == TileActivity.Unknown) return;
        Reported?.Invoke(this, new ActivityReading(state, Authority, DateTime.UtcNow, DetailOf(progress)));
    }

    /// <summary>What a reported job state means for the tile.</summary>
    internal static TileActivity StateOf(TerminalProgressState state) => state switch
    {
        TerminalProgressState.None => TileActivity.Idle,
        TerminalProgressState.Normal or TerminalProgressState.Indeterminate => TileActivity.Working,
        _ => TileActivity.Unknown,
    };

    /// <summary>The figure, for the tooltip — and only where there is one.</summary>
    /// <remarks><see cref="TerminalProgressState.Normal"/> is the only state that gives
    /// <see cref="TerminalProgress.Percent"/> a meaning; everywhere else 0 and "did not say" are the
    /// same word, and a row reading "0%" for an indeterminate job is worse than one saying nothing.
    /// </remarks>
    internal static string? DetailOf(TerminalProgress progress) =>
        progress.State == TerminalProgressState.Normal
            ? string.Format(CultureInfo.CurrentCulture, "{0}%", progress.Percent)
            : null;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_terminal != null)
            _terminal.ProgressChanged -= OnProgressChanged;
        _terminal = null;
    }
}
