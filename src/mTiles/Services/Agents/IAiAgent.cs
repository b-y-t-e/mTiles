using System.Diagnostics;
using mTiles.Models;
using mTiles.Services.Providers;
using mTiles.Services.Shells;

namespace mTiles.Services.Agents;

/// <summary>
/// One AI coding CLI, as behaviour rather than as a row holding a binary name and <c>--version</c>.
/// </summary>
/// <remarks>
/// <para>One class per agent, keyed by a string id the way <c>TileKindIds</c> and
/// <c>IShellTerminal</c> are. Everything a CLI does differently from the others lives in its class:
/// how it is told to resume a conversation, how effort and permission reach it, what it puts in the
/// environment, what its startup and fallback commands are, and how to read a line of its output.</para>
/// <para><b>Every table in an implementation is somebody else's CLI contract, measured once</b>
/// (2026-08-29, against Claude Code 2.1.251, codex-cli 0.141.0, opencode 1.18.18, pi 0.84.3 and
/// agy 1.1.22). They move — claude's permission modes went from three to six with nothing on this side
/// changing — which is why <see cref="RejectedFlag"/> exists and why nothing here is treated as
/// permanent.</para>
/// <para>Split from <see cref="AiAgentInstance"/> on purpose: this is the CLI and there is one of each,
/// that is configuration and there are as many as the user wants.</para>
/// </remarks>
public interface IAiAgent
{
    /// <summary>Stable, lowercase, and what settings and layouts store — <c>"claude"</c>,
    /// <c>"opencode"</c>. Never shown to the user.</summary>
    string Id { get; }

    /// <summary>What the user is shown — <c>"Claude Code"</c>, <c>"Antigravity"</c>.</summary>
    string DisplayName { get; }

    /// <summary>The program to look for on <c>PATH</c>, without an extension.</summary>
    string BinaryName { get; }

    /// <summary>Where to read about it, for an agent this machine does not have.</summary>
    string? InstallUrl { get; }

    /// <summary>What an install button would run, or null where there is nothing this application can
    /// honestly offer to run. Shown before it runs, always — see <see cref="Models.InstallPlan"/>.</summary>
    InstallPlan? InstallPlan { get; }

    /// <summary>How this agent's conversation gets an identity that survives a restart.</summary>
    SessionStrategy SessionStrategy { get; }

    /// <summary>The API shapes this agent can speak. A provider is compatible when its own list
    /// intersects this one — see <see cref="ApiFlavor"/> for why the OpenAI split matters.</summary>
    IReadOnlyList<ApiFlavor> ConsumesApiFlavors { get; }

    /// <summary>
    /// The permission modes this agent can actually be put in, here.
    /// </summary>
    /// <remarks>
    /// <para><b>A list, not a subset of a closed enum, and asked per <see cref="AiUsage"/>.</b> Three
    /// measured facts force both: codex has no single flag for this at all (two orthogonal axes),
    /// opencode's control is a boolean, and what an agent supports differs between its TUI and its
    /// headless mode — <c>opencode --variant</c> exists on <c>run</c> and not on the TUI. So the
    /// question is never "what does this agent support" but "what does it support <em>here</em>".</para>
    /// <para>Anything not on the list is rounded <b>down</b> by <see cref="AiBehaviours.RoundDown"/>,
    /// never up.</para>
    /// </remarks>
    /// <param name="instance">The configuration being run. It is what a provider hangs off, and some
    /// answers belong to the provider rather than to the agent — opencode's variants are the
    /// provider's list, not opencode's.</param>
    IReadOnlyList<AiBehaviour> SupportedBehaviours(AiAgentInstance instance, AiUsage usage);

    /// <inheritdoc cref="SupportedBehaviours"/>
    /// <summary>The effort levels this agent can actually be asked for, here. Anything else is rounded
    /// to the nearest by <see cref="AiEfforts.RoundToNearest"/>, ties upward.</summary>
    IReadOnlyList<AiEffort> SupportedEfforts(AiAgentInstance instance, AiUsage usage);

    /// <summary>
    /// The whole argv fragment that asks for this level of effort, or empty for "pass nothing".
    /// </summary>
    /// <remarks>
    /// <b>A fragment and not a flag with a value</b>, because codex's effort is
    /// <c>-c model_reasoning_effort=high</c> — a config key rather than an option — which no
    /// "flag name plus value" shape can express. That is also what weakens
    /// <see cref="RejectedFlag"/> for codex and why <see cref="EffortFlagFor"/> exists separately: a
    /// refused <c>-c</c> key does not read like <c>unknown option '--effort'</c>.
    /// </remarks>
    IReadOnlyList<string> EffortArgs(AiEffort effort, AiUsage usage);

    /// <inheritdoc cref="EffortArgs"/>
    /// <summary>The whole argv fragment that puts the agent in this mode, or empty for "pass
    /// nothing".</summary>
    IReadOnlyList<string> BehaviourArgs(AiBehaviour behaviour, AiUsage usage);

    /// <summary>
    /// The token to blame when the agent refuses what <see cref="EffortArgs"/> passed — or null when
    /// nothing was passed.
    /// </summary>
    /// <remarks>
    /// <para><b>Here rather than in <see cref="AiEfforts"/> or <see cref="AiBehaviours"/>, because the
    /// spelling is the agent's and not this application's.</b> Those two once hard-coded
    /// <c>--effort</c> and <c>--permission-mode</c> — Claude Code's words — so a <c>pi</c> older than
    /// <c>--thinking</c> answered <c>error: unknown option '--thinking'</c>, matched neither, and the
    /// user was told only that "the AI tool reported a failure" over a usage message about a flag they
    /// had never typed.</para>
    /// <para><b>Asked for the run that happened, not for the agent in general.</b> Every agent adds its
    /// flags conditionally, so a matcher told the agent's flag unconditionally reads a usage message as
    /// "the flag was refused" over a flag that was never on the command line.</para>
    /// </remarks>
    string? EffortFlagFor(AiEffort effort, AiUsage usage);

    /// <inheritdoc cref="EffortFlagFor"/>
    /// <summary>The token to blame when the agent refuses what <see cref="BehaviourArgs"/> passed.
    /// Named together with the effort one because recognising either needs the other: a usage message
    /// is only worth acting on when it mentions one of them alone.</summary>
    string? BehaviourFlagFor(AiBehaviour behaviour, AiUsage usage);

    /// <summary>
    /// The environment this instance's processes get. A <c>null</c> value <b>unsets</b> the variable.
    /// </summary>
    /// <remarks>The unset half is the whole reason this is not a plain dictionary: a machine that
    /// exports a global <c>ANTHROPIC_API_KEY</c> cannot otherwise be given a child that authenticates
    /// through <c>ANTHROPIC_AUTH_TOKEN</c> instead. Secrets go this way and never into a startup
    /// script, which is typed into a live PTY and lands in the scrollback and the shell's history.
    /// <para>It takes an <see cref="AgentRuntime"/> rather than an instance because most of what goes
    /// in here is the <em>provider's</em>: the address, the key, and the model resolved for this
    /// session. Which variables carry them is the agent's own business, and that is why this is a
    /// member here rather than one map in the provider layer.</para></remarks>
    IReadOnlyDictionary<string, string?> EnvFor(AgentRuntime runtime);

    /// <summary>
    /// What an agent tile runs: the command that resumes <paramref name="sessionId"/>, and the one to
    /// try when it does not work.
    /// </summary>
    /// <remarks><para>In code rather than in a user-editable field, which is the whole difference between an
    /// agent and the shell profile it replaces. <c>LaunchScripts</c> rather than a type of its own —
    /// the launch chain already reads exactly this pair, and a second shape for it would be two
    /// vocabularies for one idea.</para>
    /// <para><b>The instance is not decoration.</b> Its
    /// <see cref="AiAgentInstance.DefaultBehaviour"/>, <see cref="AiAgentInstance.DefaultEffort"/> and
    /// <see cref="AiAgentInstance.ExtraArgs"/> reach both commands, fitted to what this agent supports
    /// interactively — the instance's settings apply "wherever the instance is used", and an agent tile
    /// launched on the CLI's own defaults is that promise unkept.</para></remarks>
    /// <param name="shell">The shell the command is going to be typed into, which is the only thing
    /// that knows how to quote for itself: a <c>\"</c> escape means nothing to PowerShell, and inside
    /// its double quotes a <c>$</c> interpolates.</param>
    LaunchScripts Interactive(AgentRuntime runtime, string sessionId, IShellTerminal shell);

    /// <summary>
    /// The argv fragment that asks this agent for <paramref name="model"/>, or nothing when it has no
    /// way of being told one on the command line.
    /// </summary>
    /// <remarks>
    /// <para>A fragment for the same reason <see cref="EffortArgs"/> is one: the spelling is somebody
    /// else's, measured (2026-08-30) as <c>--model</c> on opencode, codex, pi and agy. Claude Code is
    /// the exception and answers with nothing — it is told through <c>ANTHROPIC_MODEL</c> in
    /// <see cref="EnvFor"/>, which is the same route its base URL and token take.</para>
    /// <para><b>An empty model is not a model.</b> It means "whatever the agent would pick", which is
    /// the state every seeded instance is in, so nothing is passed for it.</para>
    /// </remarks>
    IReadOnlyList<string> ModelArgs(string model, AiUsage usage);

    /// <summary>
    /// Whether a model set on an instance reaches this agent at all — by argv or by environment.
    /// </summary>
    /// <remarks>Asked so that a model which would be silently ignored can be said out loud instead: an
    /// instance pointed at a provider and a model, running on an agent that can carry neither, is the
    /// quiet substitution <c>AiModelChoice</c> exists to prevent.</remarks>
    bool AcceptsModel { get; }

    /// <summary>
    /// The session id a tile of this agent runs under, given the tile's own identity.
    /// </summary>
    /// <remarks>The agent's answer and not the tile's, because the spelling belongs to the CLI:
    /// claude and pi take the tile id verbatim, while opencode insists on a <c>ses_</c> prefix and
    /// refuses anything else. A tile that built the id itself would have to know which — and the one
    /// that did handed <c>OpenCodeAgent</c> a bare GUID, which threw before the tile ever launched.
    /// Not asked of a <see cref="SessionStrategy.CapturedAfterStart"/> agent, which names its own.
    /// </remarks>
    string SessionIdForTile(string tileId);

    /// <summary>
    /// The session id to launch this agent's tile with, for an agent that will not be told one.
    /// </summary>
    /// <remarks>
    /// <para>Null for every agent that lets us choose (<see cref="SessionStrategy.Fixed"/>,
    /// <see cref="SessionStrategy.ImportedFixed"/>) — there is nothing to capture, because the id was
    /// never the agent's to pick. The two that answer here do so in opposite ways, which is why this is
    /// a method on the agent rather than a branch somewhere central: agy is <em>asked</em> (one cheap
    /// run whose JSON carries a <c>conversation_id</c>), codex is <em>read</em> (the rollout file it
    /// left behind).</para>
    /// <para>Called once, when a tile is created, and the layout has to be saved at that moment — a
    /// captured id that is not written down is a conversation lost at the next restart.</para>
    /// </remarks>
    /// <param name="request">Which tile is asking, where it is running and since when — everything a
    /// capture that has to <em>guess</em> which session is its own needs in order not to take one that
    /// belongs to a neighbouring tile.</param>
    Task<string?> CaptureSessionAsync(AiAgentInstance instance, SessionCaptureRequest request,
        CancellationToken ct);

    /// <summary>
    /// Whether <see cref="CaptureSessionAsync"/> has to be asked <em>while the tile's agent is running</em>,
    /// rather than before it starts.
    /// </summary>
    /// <remarks>
    /// <para>The two captured agents sit on opposite sides of this and the tile cannot guess which.
    /// agy's capture <em>creates</em> the conversation, so it has to happen first or the tile would
    /// resume an id that is not the one on screen; codex's reads the rollout file its own session
    /// leaves behind, which does not exist until the session does — and it may take a moment to appear,
    /// so a caller answering true is telling the tile to keep asking for a while rather than once.</para>
    /// <para>False by default: an agent that names nothing is asked nothing, and getting this wrong the
    /// other way costs a model call per tile.</para>
    /// </remarks>
    bool CapturesWhileRunning { get; }

    /// <summary>
    /// Whether this agent can report what it is doing as it does it.
    /// </summary>
    /// <remarks>Opt-in per agent, like <see cref="AcceptsPromptOnStdin"/> and for the same reason: it
    /// is a claim about somebody else's CLI. An agent that does not stream has its output read at the
    /// end.</remarks>
    bool SupportsStreaming { get; }

    /// <summary>
    /// Whether this agent reads its prompt from standard input when the prompt is left off the command
    /// line.
    /// <para>Opt-in, and false by default, because it is a claim about somebody else's CLI. Windows
    /// caps a command line at 32 767 characters — 8 191 through the <c>.cmd</c> shim npm installs — and
    /// a prompt carrying a diff passes that easily, at which point <c>Process.Start</c> throws and the
    /// tile can only offer to try again and fail identically. Stdin removes the limit, but an agent
    /// that does <em>not</em> read stdin would sit waiting for input that never comes.</para>
    /// </summary>
    bool AcceptsPromptOnStdin { get; }

    /// <summary>
    /// Puts this agent's headless command line together: the prompt, the output format, and whatever
    /// <see cref="EffortArgs"/> and <see cref="BehaviourArgs"/> say.
    /// </summary>
    /// <param name="behaviour">How much the agent may do without asking. Defaulted to
    /// <see cref="AiBehaviour.Auto"/> rather than to "pass nothing", because "pass nothing" is what
    /// made a headless run refuse every edit on a machine whose Claude Code is at its factory
    /// ask-first default.</param>
    /// <param name="effort">How hard the agent is asked to think. Defaulted to
    /// <see cref="AiEffort.High"/> rather than to the agent's own, because a goal run is left alone and
    /// an attempt spent on a shallow answer costs as much of the budget as a careful one.</param>
    /// <param name="usage">Which phase this run is. Not optional, and not defaulted to the phase that
    /// writes: a default here would silently give a review the execution phase's permission, which is
    /// the second agent editing the worktree that <c>GoalBaseline</c> only photographed once.</param>
    /// <param name="model">Which model to ask for, or empty for the agent's own choice. Already
    /// resolved: a sentinel must never reach a command line, which is what
    /// <c>AgentRuntime.RequestedModel</c> is for.</param>
    void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming, AiUsage usage,
        AiBehaviour behaviour = AiBehaviour.Auto,
        AiEffort effort = AiEffort.High,
        string model = "");

    /// <summary>
    /// Where on the command line <see cref="ConfigureProcess"/> has just written the instance's own
    /// <see cref="AiAgentInstance.ExtraArgs"/> may be inserted.
    /// </summary>
    /// <remarks>
    /// The agent's answer rather than a rule the caller works out, because only the agent knows which
    /// of its own arguments belong together. Guessing "in front of the last argument when it equals the
    /// prompt" is right for the agents that pass the prompt as a bare positional and wrong for agy,
    /// whose prompt is the <c>--print</c> flag's own value: an argument slipped between the two is read
    /// as that value, and the prompt is left as a stray positional.
    /// </remarks>
    int ExtraArgsIndex(IReadOnlyList<string> arguments, string prompt);

    /// <summary>
    /// Everything one line of the agent's output says, in the order it says it.
    /// </summary>
    /// <remarks>
    /// A list rather than one chunk, because a single assistant message carries both prose and tool
    /// calls — "let me look at the cart" and then the Read. Returning one meant choosing, and choosing
    /// the tool call threw the sentence away: invisible while the run ends with a result line, and the
    /// whole of what is left when it does not — which is exactly the interrupted run, where what it
    /// managed to say is all there is to show for it.
    /// </remarks>
    IReadOnlyList<AiOutputChunk> ParseLine(string line);
}
