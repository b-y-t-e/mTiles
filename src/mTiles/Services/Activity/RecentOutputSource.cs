using System.Text;
using mTiles.Models;
using Terminal.Avalonia;

namespace mTiles.Services.Activity;

/// <summary>
/// Matches a CLI's own words against what it has painted recently.
/// </summary>
/// <remarks>
/// <para><b>Recently painted text, not a snapshot of the screen — and the difference is the whole
/// reason this sits below OSC.</b> Tools that read a pane through <c>tmux capture-pane</c> get the
/// rendered buffer: what is on screen now, wherever the cursor put it. This keeps the last few
/// kilobytes the child wrote, with the escape sequences taken out. For the question being asked they
/// mostly agree — a status bar that says the agent is working is <em>painted</em> when it appears, and
/// again on every redraw — but they part company in one direction: text that scrolled away is still in
/// this window for a few seconds after it stopped being true. That is what
/// <see cref="ActivityMarkers.LastWins"/> exists to survive and why nothing here may outrank a signal
/// the CLI sent on purpose.</para>
/// <para><b>It reads no buffer, and that is deliberate.</b> Asking the control for its screen would
/// couple this to the emulator's internals and need an API that does not exist; a private ring of the
/// child's own bytes needs neither, and cannot be wrong about what the child sent. If this is ever not
/// enough, the fix is to ask the terminal for its buffer — not to grow <see cref="AnsiText"/> into a
/// second emulator.</para>
/// </remarks>
public sealed class RecentOutputSource : IActivitySource
{
    /// <summary>How much of the child's output is kept. A few screens' worth of a status bar being
    /// redrawn — enough that a marker painted once is still there a moment later, small enough that
    /// re-reading it costs nothing.</summary>
    private const int WindowBytes = 16 * 1024;

    /// <summary>How often the window is re-read. Output arrives in bursts many times a second and the
    /// answer cannot change faster than the user can look at it.</summary>
    private static readonly TimeSpan EvaluateInterval = TimeSpan.FromMilliseconds(250);

    private readonly IAgentActivityReader _reader;
    private readonly StringBuilder _window = new(WindowBytes);
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();

    private TerminalControl? _terminal;
    private DateTime _lastEvaluated = DateTime.MinValue;
    private char[] _chars = new char[4096];

    public RecentOutputSource(IAgentActivityReader reader) => _reader = reader;

    /// <inheritdoc />
    public ActivityAuthority Authority => ActivityAuthority.Screen;

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

    private void OnRawOutputReceived(object? sender, ReadOnlyMemory<byte> chunk)
    {
        Append(chunk.Span);

        var now = DateTime.UtcNow;
        if (now - _lastEvaluated < EvaluateInterval) return;
        _lastEvaluated = now;

        // Stripped here rather than on the way in, because an escape sequence split across two chunks
        // would otherwise be half-removed and leave its tail in the text as words that were never
        // painted. Re-reading sixteen kilobytes four times a second is nothing next to being wrong.
        var state = _reader.ReadRecentOutput(AnsiText.Strip(AsSpan()), out var detail);
        if (state == TileActivity.Unknown) return;
        Reported?.Invoke(this, new ActivityReading(state, Authority, now, detail));
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        // A stateful decoder, so a multi-byte character split across chunks survives it. A fresh
        // Encoding.GetString per chunk turns every such split into two replacement characters, right in
        // the middle of the box-drawing these UIs are built out of.
        var needed = _decoder.GetCharCount(bytes, false);
        if (needed > _chars.Length) _chars = new char[Math.Max(needed, _chars.Length * 2)];
        var written = _decoder.GetChars(bytes, _chars, false);

        _window.Append(_chars, 0, written);
        if (_window.Length > WindowBytes) _window.Remove(0, _window.Length - WindowBytes);
    }

    private ReadOnlySpan<char> AsSpan()
    {
        var buffer = new char[_window.Length];
        _window.CopyTo(0, buffer, 0, _window.Length);
        return buffer;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_terminal != null)
            _terminal.RawOutputReceived -= OnRawOutputReceived;
        _terminal = null;
        _window.Clear();
    }
}
