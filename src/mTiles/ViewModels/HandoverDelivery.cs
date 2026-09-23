namespace mTiles.ViewModels;

/// <summary>
/// When the one line pointing a terminal agent at its handover brief is typed into it.
/// </summary>
/// <remarks>
/// <para><b>Typed, once, rather than put on the command line.</b> The launch chain keeps its commands and
/// runs them again — after a crash, and every time the user quits the tool and it is brought back — so a
/// prompt argument would hand the brief over again at each of those. Typed into the session, it is said
/// exactly once, the way the user would have said it.</para>
/// <para><b>Only into one of the tile's own commands, and only once it has drawn and gone quiet.</b> A TUI
/// that is still starting drops what it is sent, and the plain shell a chain falls back to would read the
/// line as a command. The plain shell is where this gives up: <see cref="Abandoned"/> is the caller's cue to
/// say where the brief is instead.</para>
/// <para><b>Typed again into the next command when the one it went to ended soon after.</b> The first link
/// of a chain is often a resume that fails — <c>claude --resume</c> on an id it never issued waits, says so
/// and exits — and a line typed into a process that then exits reached nobody.</para>
/// <para>Pure and driven by the clock it is handed, so the rules are argued in a table test rather than
/// against a terminal.</para>
/// </remarks>
public sealed class HandoverDelivery
{
    /// <summary>How long one of the tile's commands must have been running before anything is typed.</summary>
    public static readonly TimeSpan MinimumRun = TimeSpan.FromSeconds(4);

    /// <summary>How long the output must have been still.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(1500);

    /// <summary>After this long a command that never goes quiet is typed at anyway.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(20);

    /// <summary>A command that ends within this long of being typed at is taken not to have read it.</summary>
    public static readonly TimeSpan RetypeWindow = TimeSpan.FromSeconds(30);

    /// <summary>After this long with no command started at all — a launch that was refused — it is given
    /// up on, so the caller can say where the brief is.</summary>
    public static readonly TimeSpan NoLaunchWithin = TimeSpan.FromMinutes(2);

    /// <summary>How many times the line is typed at most.</summary>
    public const int MaxAttempts = 3;

    private DateTimeOffset? _firstTick;
    private DateTimeOffset? _commandStarted;
    private DateTimeOffset? _lastOutput;
    private DateTimeOffset? _typedAt;
    private int _attempts;

    /// <param name="prompt">The line to type.</param>
    public HandoverDelivery(string prompt) => Prompt = prompt;

    /// <summary>The line to type.</summary>
    public string Prompt { get; }

    /// <summary>Whether nothing more will be typed — delivered, or given up on.</summary>
    public bool IsFinished { get; private set; }

    /// <summary>Whether it was given up on before anything was typed into a command that stayed.</summary>
    public bool Abandoned { get; private set; }

    /// <summary>A process is starting in the tile.</summary>
    /// <param name="ownCommand">False for the plain shell the chain ends at.</param>
    public void OnCommandStarting(bool ownCommand, DateTimeOffset now)
    {
        if (IsFinished) return;

        if (!ownCommand)
        {
            Finish(abandoned: true);
            return;
        }

        if (_typedAt is { } typed)
        {
            // What was typed went to a command that ended at once, so it reached nobody.
            if (now - typed > RetypeWindow || _attempts >= MaxAttempts)
            {
                Finish(abandoned: now - typed <= RetypeWindow);
                return;
            }
            _typedAt = null;
        }

        _commandStarted = now;
        _lastOutput = null;
    }

    /// <summary>The command wrote something.</summary>
    public void OnOutput(DateTimeOffset now)
    {
        if (_commandStarted is not null) _lastOutput = now;
    }

    /// <summary>The line to type now, or null.</summary>
    /// <param name="heldByAQuestion">The CLI is waiting for an answer — a folder-trust or permission prompt —
    /// which the line's Enter would give on the user's behalf; nothing is typed, however long it waits.</param>
    public string? Tick(DateTimeOffset now, bool heldByAQuestion = false)
    {
        if (IsFinished) return null;
        if (NeverLaunched(now))
        {
            Finish(abandoned: true);
            return null;
        }

        if (_typedAt is { } typed)
        {
            if (now - typed > RetypeWindow) Finish(abandoned: false);
            return null;
        }

        if (_commandStarted is not { } started || now - started < MinimumRun) return null;
        if (heldByAQuestion)
        {
            // Quiet is counted again from the answer, and the longest wait from it too.
            _lastOutput = now;
            _commandStarted = now - MinimumRun;
            return null;
        }

        var settled = _lastOutput is { } last && now - last >= Quiet;
        if (!settled && now - started < LongestWait) return null;

        _typedAt = now;
        _attempts++;
        return Prompt;
    }

    private bool NeverLaunched(DateTimeOffset now)
    {
        _firstTick ??= now;
        return _commandStarted is null && _attempts == 0 && now - _firstTick > NoLaunchWithin;
    }

    private void Finish(bool abandoned)
    {
        IsFinished = true;
        Abandoned = abandoned;
    }
}
