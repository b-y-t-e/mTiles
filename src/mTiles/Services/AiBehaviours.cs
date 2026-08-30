using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// The canonical vocabulary for <see cref="AiBehaviour"/>: what each one is called on screen, how much
/// it lets an agent do, and what an agent that does not support one should be given instead.
/// </summary>
/// <remarks>
/// <para>Nothing here spells a flag. Every agent words this differently — <c>--permission-mode</c>,
/// <c>--auto</c>, <c>--sandbox</c> with <c>-a</c>, <c>--mode</c>, and pi has no gate at all — so the
/// flags belong to the agent classes and this holds only what is true of all of them. That is a
/// correction: these spellings used to live here as Claude Code's words under a neutral name, which is
/// how a second agent got given a first agent's flags.</para>
/// <para>What is left is genuinely shared, and the ranking is the load-bearing part of it.</para>
/// </remarks>
public static class AiBehaviours
{
    /// <summary>How the mode reads in the tile's status strip — lower case, like everything else
    /// there.</summary>
    public static string Label(AiBehaviour mode) => mode switch
    {
        AiBehaviour.Ask => "ask",
        AiBehaviour.Plan => "plan",
        AiBehaviour.Auto => "auto",
        AiBehaviour.AcceptEdits => "accept edits",
        AiBehaviour.BypassPermissions => "bypass",
        _ => "tool default",
    };

    /// <summary>The modes in the order a combo box offers them: safest first, and the one that asks
    /// nothing last.</summary>
    public static IReadOnlyList<AiBehaviour> All { get; } =
    [
        AiBehaviour.Plan,
        AiBehaviour.Ask,
        AiBehaviour.Auto,
        AiBehaviour.AcceptEdits,
        AiBehaviour.BypassPermissions,
        AiBehaviour.ToolDefault,
    ];

    /// <summary>The labels, for a combo box bound to strings.</summary>
    public static IReadOnlyList<string> Labels { get; } = All.Select(Label).ToList();

    /// <summary>
    /// The modes worth offering for a run nobody is watching — the Goal tile's strip.
    /// </summary>
    /// <remarks>
    /// <para>Shorter than <see cref="All"/>, and the three that are missing are missing for one reason:
    /// a headless run has nobody to ask, so every mode that asks turns a refusal into a tool call that
    /// simply fails. <see cref="AiBehaviour.Ask"/> denies everything;
    /// <see cref="AiBehaviour.AcceptEdits"/> is the worst of them because it looks like it is working,
    /// letting edits through while quietly refusing every other tool; and
    /// <see cref="AiBehaviour.Plan"/> would leave the implement phase unable to write a single file
    /// while the loop spends its attempts on "the last attempt changed no files".</para>
    /// <para>A second guard rather than the only one: <c>AiProcessRunner</c> rounds whatever it is
    /// given through the agent's own <c>SupportedBehaviours</c>, so a value stored by a newer build —
    /// or by hand — still cannot reach a CLI that would choke on it.</para>
    /// </remarks>
    public static IReadOnlyList<AiBehaviour> Headless { get; } =
    [
        AiBehaviour.Auto,
        AiBehaviour.BypassPermissions,
        AiBehaviour.ToolDefault,
    ];

    /// <summary>The labels of <see cref="Headless"/>, for the Goal tile's combo box.</summary>
    public static IReadOnlyList<string> HeadlessLabels { get; } = Headless.Select(Label).ToList();

    /// <summary>The mode a label came from. Anything unrecognised is <see cref="AiBehaviour.Auto"/>
    /// — the default — rather than an exception: this is fed by a combo box whose contents come from
    /// <see cref="Labels"/>, so the only way to miss is a change made here, and the safe answer to that
    /// is the default rather than a crash while the tile is being built.</summary>
    public static AiBehaviour FromLabel(string? label) =>
        All.FirstOrDefault(m => string.Equals(Label(m), label, StringComparison.OrdinalIgnoreCase),
            AiBehaviour.Auto);

    /// <summary>
    /// How much this mode lets an agent do, as a number that can be compared.
    /// </summary>
    /// <remarks>
    /// <para>Not the enum's own order: those values are a file format and the two newest members could
    /// not be inserted in the middle of it. Spelled out here so the ranking is a decision somebody made
    /// rather than a side effect of the order two members happened to be written in.</para>
    /// <para><see cref="AiBehaviour.Auto"/> outranks <see cref="AiBehaviour.AcceptEdits"/> because it
    /// acts on more without asking: accept-edits still stops at every non-edit tool.
    /// <see cref="AiBehaviour.ToolDefault"/> is off the scale — it is the absence of an answer, not a
    /// weak one, which is why it cannot be compared and why <see cref="RoundDown"/> falling to it is a
    /// floor rather than a weakening.</para>
    /// </remarks>
    public static int Strength(AiBehaviour mode) => mode switch
    {
        AiBehaviour.Plan => 0,
        AiBehaviour.Ask => 1,
        AiBehaviour.AcceptEdits => 2,
        AiBehaviour.Auto => 3,
        AiBehaviour.BypassPermissions => 4,
        _ => -1,
    };

    /// <summary>
    /// The mode to actually use when <paramref name="wanted"/> is not among
    /// <paramref name="supported"/>: the strongest supported one that is still no stronger than what
    /// was asked for, or <see cref="AiBehaviour.ToolDefault"/> when there is none.
    /// </summary>
    /// <remarks>
    /// <para><b>Down, never up</b> among the modes the agent actually has, and the asymmetry with
    /// <c>AiEfforts.RoundToNearest</c> is deliberate. Being given less than was asked for costs a run
    /// that stops to ask about something; being given more means an agent doing unattended what the
    /// user only authorised it to do under supervision. The one direction is an inconvenience and the
    /// other is somebody's repository.</para>
    /// <para><b>What the floor is, exactly.</b> When the agent has no gate weaker than what was asked
    /// for — pi and opencode have no weak gate at all — the answer is
    /// <see cref="AiBehaviour.ToolDefault"/>, which is <em>no flag passed</em> and therefore whatever
    /// the CLI is configured to do on this machine: codex's <c>config.toml</c> can say
    /// <c>dangerously-bypass</c>. So the guarantee this makes is that nothing here ever <em>asks</em>
    /// for more than was wanted, not that the run ends up weaker — that is not something an agent
    /// without the gate can be made to promise. It is still the least bad of the three answers: the
    /// weakest mode such an agent does support is <see cref="AiBehaviour.BypassPermissions"/>, which
    /// would make it certain rather than possible, and refusing to launch would take the tile away over
    /// a restriction the CLI never offered. The chooser is narrowed to
    /// <c>IAiAgent.SupportedBehaviours</c> everywhere it is offered, so this path is reached by a
    /// stored value rather than by a choice made in front of somebody.</para>
    /// </remarks>
    public static AiBehaviour RoundDown(AiBehaviour wanted, IReadOnlyList<AiBehaviour> supported)
    {
        if (supported.Contains(wanted)) return wanted;

        // ToolDefault asked for is ToolDefault given, whether or not the agent lists it: passing no
        // flag is something every CLI can do, and it is also the floor everything else falls to.
        var ceiling = Strength(wanted);
        if (ceiling < 0) return AiBehaviour.ToolDefault;

        return supported
            .Where(mode => Strength(mode) >= 0 && Strength(mode) < ceiling)
            .OrderByDescending(Strength)
            .DefaultIfEmpty(AiBehaviour.ToolDefault)
            .First();
    }

    /// <summary>
    /// Whether what the tool printed is it rejecting the mode this tile gave it.
    /// </summary>
    /// <remarks>
    /// <para>The spellings are somebody else's CLI contract, and it has already moved once: an older
    /// Claude Code called the default mode <c>default</c> and does not know <c>auto</c>. On such a
    /// machine <em>every</em> run of this tile fails — on the default setting, with no goal ever
    /// reaching a plan — and the transcript says only "the AI tool reported a failure" over a usage
    /// message about a flag the user never typed and cannot see. The fix is two clicks away and nothing
    /// pointed at it.</para>
    /// <para>Matched on the flag's own name plus a word that says it was rejected, rather than on the
    /// word "permission" alone: the tool prints that in plenty of messages that are about the work. The
    /// cost of a miss is the old, unhelpful sentence; the cost of a false positive is telling somebody
    /// their settings are wrong when the failure was something else.</para>
    /// </remarks>
    /// <param name="permissionFlag">The flag the tool was actually given, from
    /// <c>IAiAgent.BehaviourFlagFor</c> — <c>--permission-mode</c> for Claude Code, <c>--mode</c> for
    /// Antigravity, <c>--sandbox</c> for codex, nothing at all for pi.</param>
    /// <param name="effortFlag">The tool's other flag, needed to read a usage message: one is only
    /// worth acting on when it mentions this flag alone.</param>
    public static bool LooksLikeRejectedMode(
        string? toolOutput, string? permissionFlag, string? effortFlag) =>
        permissionFlag is { Length: > 0 }
        && RejectedFlag.Named(toolOutput, permissionFlag, valueRejectionCounts: true,
            effortFlag is { Length: > 0 } ? [effortFlag] : []);

    /// <summary>What to tell the user when it was. Names the control and the value that always works.
    /// </summary>
    public const string RejectedModeAdvice =
        "This looks like the AI tool refusing the permission mode this tile asked for, which an older " +
        "version of it will do for a mode it has never heard of. Pick \"tool default\" in the strip " +
        "above to pass no flag at all, or update the tool.";
}
