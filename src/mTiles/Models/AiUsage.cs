namespace mTiles.Models;

/// <summary>
/// Where an agent is being used right now: as the tile's own interactive session, or headlessly for
/// one phase of a goal.
/// </summary>
/// <remarks>
/// <para>A parameter to <c>IAiAgent</c>'s capability questions rather than a property of the agent,
/// because the answers genuinely differ between the two. Measured: <c>opencode --variant</c> exists on
/// <c>opencode run</c> and not on the TUI, so "what efforts does opencode support" has no answer until
/// you say which of the two you mean. And the phases that neither edit the repository nor run its own
/// commands run read-only whatever the user picked for execution — the agent decides that by phase,
/// not the user.</para>
/// <para>A struct with a phase inside rather than a two-case hierarchy: it is asked for constantly, it
/// carries no behaviour of its own, and the phase is meaningless without the headless half.</para>
/// </remarks>
public readonly record struct AiUsage
{
    private AiUsage(bool headless, GoalPhase phase, bool checksProjectHealth)
    {
        IsHeadless = headless;
        Phase = phase;
        ChecksProjectHealth = checksProjectHealth;
    }

    /// <summary>Whether this is a headless run rather than the tile's own session.</summary>
    public bool IsHeadless { get; }

    /// <summary>Which phase of the goal loop is running. Only meaningful when
    /// <see cref="IsHeadless"/>; an interactive session is not part of a goal.</summary>
    public GoalPhase Phase { get; }

    /// <summary>Whether the tile's completion criteria ask this run to establish that the project
    /// builds and its tests pass.</summary>
    /// <remarks>Carried rather than derived from the phase, because it is the user's setting: with
    /// both <c>RequireBuild</c> and <c>RequireTestsPass</c> off, the review is asked to run nothing and
    /// can be held to reading alone.</remarks>
    public bool ChecksProjectHealth { get; }

    /// <summary>The agent driving a tile the user is typing into.</summary>
    public static AiUsage Interactive { get; } = new(headless: false, GoalPhase.Goal, checksProjectHealth: false);

    /// <summary>One run of the goal loop, with nobody watching it.</summary>
    /// <param name="phase">Which phase is about to run.</param>
    /// <param name="checksProjectHealth">Whether the completion criteria ask for the build and the
    /// tests — see <see cref="RunsProjectCommands"/>.</param>
    public static AiUsage Headless(GoalPhase phase, bool checksProjectHealth = false) =>
        new(headless: true, phase, checksProjectHealth);

    /// <summary>
    /// Whether this run needs to be able to edit the repository.
    /// </summary>
    /// <remarks>Asked here rather than by each agent, because the answer is a fact about the phase and
    /// not about the CLI: clarifying, planning, reviewing and summarising all read and none of them
    /// edit anything. It is what lets an agent hand those phases <see cref="AiBehaviour.Plan"/> without
    /// every one of the five repeating the same list of phases.</remarks>
    public bool WritesToTheRepository => !IsHeadless || Phase == GoalPhase.Implement;

    /// <summary>
    /// Whether this run has to be able to <em>run</em> the project's own build and test commands, even
    /// though it edits nothing.
    /// </summary>
    /// <remarks>The review is told to establish the health criteria "by running this project's own
    /// commands rather than by reading the diff" — and a build writes: <c>obj/</c>, <c>bin/</c>,
    /// <c>target/</c>, <c>node_modules/.cache</c>. Under a read-only sandbox those commands fail, so
    /// the reviewer either reports a build failure the changes never caused — burning an attempt on the
    /// next iteration — or quietly skips the check that is the tile's default completion criterion.
    /// Its licence to write is therefore the smallest thing that lets the check happen at all, and what
    /// keeps it from editing source is the sentence in the review prompt that says so.</remarks>
    public bool RunsProjectCommands => IsHeadless && Phase == GoalPhase.Review && ChecksProjectHealth;

    /// <summary>Whether this run may be held to reading and nothing else.</summary>
    /// <remarks>The one question the agents ask, so that "writes nothing" and "runs nothing" cannot
    /// drift apart between five CLIs.</remarks>
    public bool MayOnlyRead => IsHeadless && !WritesToTheRepository && !RunsProjectCommands;
}
