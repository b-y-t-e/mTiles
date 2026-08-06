namespace mTiles.Services;

/// <summary>What the chain does next, once a command has ended.</summary>
internal enum ChainStep
{
    /// <summary>Start the same command again: it had been working.</summary>
    Relaunch,

    /// <summary>Start the profile over from its first command: the user closed a working tool.</summary>
    RestartChain,

    /// <summary>Try the next command in the profile: this one did not work. Off the end of the chain is
    /// a plain interactive shell, so this is also how the chain comes to rest — there is deliberately no
    /// "give up and start a shell" step, which only ever let a command skip the fallback the profile
    /// named for exactly its case.</summary>
    NextCommand,
}

/// <summary>
/// The rules a launch chain follows: the thresholds, and the two decisions taken against them.
/// <para>Separate from the chain that carries them out, and pure, because every one of these rules has
/// been wrong at some point and none of them was reachable by a test while they lived inside a loop
/// that needed a terminal, a dispatcher and a stopwatch to run.</para>
/// </summary>
/// <param name="MinLifetimeForRelaunch">A command that ended <em>cleanly</em> having run for less than
/// this is one that will not stay up, rather than a user quitting something that worked.</param>
/// <param name="Established">How long a command must run before a <em>failure</em> is read as a working
/// tool crashing rather than the command not working at all. Has to sit comfortably above how long a
/// tool takes to start up and give up; see <see cref="Decide"/>.</param>
/// <param name="Retry">Pause between one command failing and the next being tried.</param>
/// <param name="Relaunch">Pause before a command that had been working is started again.</param>
/// <param name="MaxRelaunches">How many relaunches may be spent within <paramref name="RelaunchWindow"/>;
/// see <see cref="RelaunchBudget"/>.</param>
/// <param name="RelaunchWindow">The window <paramref name="MaxRelaunches"/> is counted over. A setting
/// rather than a constant so a test can watch it expire, which is half of what the rule does.</param>
internal sealed record ChainPolicy(
    int MinLifetimeForRelaunch,
    int Established,
    int Retry,
    int Relaunch,
    int MaxRelaunches = 3,
    int RelaunchWindow = 10 * 60 * 1000)
{
    public static readonly ChainPolicy Default = new(
        MinLifetimeForRelaunch: 10_000, Established: 120_000, Retry: 200, Relaunch: 500);

    /// <summary>
    /// The whole verdict, as a function of how the command ended and how long it lasted.
    /// <para>Neither input decides on its own, which is the point. Judging on time alone made
    /// <c>claude -r &lt;unknown-id&gt;</c> — 21 seconds to print "Invalid session ID" and exit 1 — look
    /// like a tool the user was working in, so its failure was read as a quit and it was relaunched
    /// every 21 seconds for good, with the fallback unreachable. Judging on the code alone would demote
    /// a tile to a bare shell the first time a long-running tool crashed.</para>
    /// </summary>
    /// <param name="exitCode">The child's code, or null when there is none — a lost connection, or a
    /// session torn down. No code is read as a failure: there is no way left to reach it either.</param>
    /// <param name="lived">How long the session ran, as the terminal reports it.</param>
    public ChainStep Decide(int? exitCode, long lived) => exitCode switch
    {
        0 when lived >= MinLifetimeForRelaunch => ChainStep.RestartChain,
        // Cleanly, but at once: it did not stick. That is what the profile's fallback is *for*, so the
        // chain moves on to it rather than to a shell — and when there is no fallback, moving on is how
        // it reaches the shell anyway. The two are only the same answer for the last command.
        0 => ChainStep.NextCommand,
        _ when lived >= Established => ChainStep.Relaunch,
        _ => ChainStep.NextCommand,
    };

    /// <summary>
    /// Whether starting this again should be paid for out of the relaunch budget.
    /// <para>A session that ended <em>cleanly</em> after a real stretch of work is the user closing
    /// their tool, on purpose, and doing that four times in ten minutes is a perfectly ordinary
    /// morning. Charging it would have the tile answer the fourth quit by refusing to bring the tool
    /// back — punishing the one case the feature exists to serve.</para>
    /// <para><b>This leaves one loop deliberately unbounded</b>, and it is worth being explicit about:
    /// a command that exits cleanly after <see cref="Established"/>, for ever, is restarted for ever.
    /// That is not a spin — every lap costs at least <see cref="Established"/> of a real session, so the
    /// worst case is one spawn every two minutes, and it is indistinguishable from a user quitting and
    /// reopening their tool by hand. Bounding it would mean the tile stops honouring exactly the
    /// gesture it exists to honour, which is the worse failure of the two.</para>
    /// <para>Everything else is charged, because everything else might be a loop: a crash is one by
    /// definition if it keeps happening, and a clean exit that comes too quickly is indistinguishable
    /// from one. <see cref="Established"/> and not <see cref="MinLifetimeForRelaunch"/> is the bar —
    /// eleven seconds of "work" four times running is a tool falling over politely, not a user.</para>
    /// </summary>
    public bool CountsAgainstBudget(int? exitCode, long lived) => !(exitCode == 0 && lived >= Established);

    /// <summary>
    /// Rejects thresholds that cannot mean what they say.
    /// <para><see cref="Established"/> below <see cref="MinLifetimeForRelaunch"/> is the one that
    /// matters, and it is not obvious: there would then be a band of lifetimes in which a
    /// <em>failure</em> counts as a working tool crashing while a <em>clean</em> exit still counts as
    /// never having got going — the chain relaunching what fails and giving up on what succeeds.
    /// Nothing about the resulting behaviour would point back here.</para>
    /// <para>A method rather than a constructor body, which a positional record cannot have; called
    /// from the one place that consumes these.</para>
    /// </summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MinLifetimeForRelaunch);
        ArgumentOutOfRangeException.ThrowIfLessThan(Established, MinLifetimeForRelaunch);
        ArgumentOutOfRangeException.ThrowIfNegative(Retry);
        ArgumentOutOfRangeException.ThrowIfNegative(Relaunch);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRelaunches);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RelaunchWindow);
    }
}
