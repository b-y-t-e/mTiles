using mTiles.Models;
using Terminal.Avalonia;

namespace mTiles.Services.Activity;

/// <summary>
/// Reads the title the child set (OSC 0/2) as a state, through the agent that knows what it means.
/// </summary>
/// <remarks>
/// <para><b>The best signal available without writing a single file into anybody's configuration.</b>
/// Claude Code has set an animated title while it thinks since 2.1.6 and re-asserts it continuously
/// since 2.1.132; Antigravity injects its own title from a named <c>agent_state</c>. Both were arriving
/// at this application already — <c>TerminalControl.TitleChanged</c> has carried them all along — and
/// nothing was listening.</para>
/// <para><b>Ranked above the screen and below a hook.</b> A title is something the CLI chose to send
/// rather than a frame it happened to paint, so it is worth more than pattern-matching its UI; it is
/// still a symptom rather than a statement, so a hook that says "this turn is over" outranks it.</para>
/// <para>The reading itself is the agent's, never this class's: what a spinner glyph or a star means is
/// one CLI's convention, and a table here would be five CLIs' conventions in a file that knows nothing
/// about any of them.</para>
/// </remarks>
public sealed class TerminalTitleSource : IActivitySource
{
    private readonly IAgentActivityReader _reader;
    private TerminalControl? _terminal;

    public TerminalTitleSource(IAgentActivityReader reader) => _reader = reader;

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
        terminal.TitleChanged += OnTitleChanged;

        // A title set before this attached is still the current one, and the control re-raises nothing.
        // Without this a tile restored into an existing session read as having no title at all until the
        // agent next changed it — which, for an idle agent, is never.
        if (terminal.Title.Length > 0) Report(terminal.Title);
    }

    private void OnTitleChanged(object? sender, string title) => Report(title);

    private void Report(string title)
    {
        var state = _reader.ReadTitle(title);
        if (state == TileActivity.Unknown) return;
        Reported?.Invoke(this, new ActivityReading(state, Authority, DateTime.UtcNow));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_terminal != null)
            _terminal.TitleChanged -= OnTitleChanged;
        _terminal = null;
    }
}
