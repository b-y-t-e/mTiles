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
    /// Whether this agent reads the model's context window out of the runtime — the question that
    /// decides whether one is resolved for a launch at all.
    /// </summary>
    /// <remarks><para>Resolving the window is a provider call — for Ollama one per model, for
    /// OpenRouter a whole catalogue — so it is made only for the agent that will use it. Default
    /// false: an agent that says nothing about the subject reads none of it.</para>
    /// <para>Claude Code is the one that does: on a third-party provider its model ids are unknown to
    /// it and it assumes a context window that can be wrong by half, so
    /// <c>CLAUDE_CODE_AUTO_COMPACT_WINDOW</c> is set from the provider's own answer. See
    /// <c>ModelContextWindow</c>.</para>
    /// <para><b>No default implementation here, deliberately.</b> A body on the interface member and
    /// an answer on the concrete class a step below it do not compose: interface mapping resolves
    /// against the class that lists the interface, and a member declared only on the derived class is
    /// never reached — the default wins, silently. The default lives on <see cref="AiAgent"/> as a
    /// <c>virtual</c>, where an override is an override.</para></remarks>
    bool UsesModelContextWindow { get; }

    /// <summary>
    /// Whether this CLI has a slot of its own for the small, frequent calls — a second model beside
    /// the real one.
    /// </summary>
    /// <remarks>Measured 2026-08-31, each against its binary and documentation: Claude Code reads
    /// <c>ANTHROPIC_DEFAULT_HAIKU_MODEL</c> (<c>ANTHROPIC_SMALL_FAST_MODEL</c>, the spelling it used
    /// to be read through, is deprecated in its favour) and opencode <c>small_model</c> in its config;
    /// codex, pi and
    /// agy answer their small calls with the main model or their own pick and offer no setting for
    /// one. Default false, and the agent-instance form hides the field where it is — a field that
    /// saves and does nothing is not offered. An agent whose slot exists but only sometimes (opencode
    /// takes the value where a provider document is written) still answers true and says the limit in
    /// its own remarks. Not a default interface member, for the reason <see
    /// cref="UsesModelContextWindow"/> spells out.</remarks>
    bool UsesFastModel { get; }

    /// <summary>
    /// Whether the fast-model slot is reached only through a configuration this application writes for
    /// a declared endpoint — so on an account where nothing is written, the field is not offered.
    /// </summary>
    /// <remarks><para>opencode carries its <c>small_model</c> in the generated provider document,
    /// which exists only where an endpoint is declared — a local server, or a hosted provider given an
    /// address of its own. On a hosted provider at its published address nothing is written, so a
    /// value typed there would save and do nothing — and a field that saves and does nothing is not
    /// offered; the form asks this to know which. Default false: Claude Code reads its slot through
    /// the environment, which exists at every launch.</para></remarks>
    bool FastModelNeedsDeclaredEndpoint => false;

    /// <summary>
    /// Whether this CLI can hold more than one login at a time — a second subscription.
    /// </summary>
    /// <remarks><b>An agent that cannot, says so</b>, and the chooser then offers it no sign-ins at
    /// all. Measured: claude, codex, opencode and pi each relocate their credentials with an
    /// environment variable — pi's was <em>missed</em> at first, from a reading of its <c>--help</c>
    /// rather than a run, and <c>PiAgent</c> carries that correction in its own words. Only agy has
    /// none, established by searching its binary rather than its help text. Inventing one would be a
    /// row the user could add, log into, and never actually run as — a second account that silently is
    /// the first.</remarks>
    bool SupportsSignIns { get; }

    /// <summary>
    /// The environment that points this CLI at one login's directory.
    /// </summary>
    /// <remarks><para>A whole block rather than a variable name and a value, for the reason
    /// <see cref="EffortArgs"/> is a whole fragment: opencode has no dedicated variable and is moved
    /// with the two XDG ones at once, so "one name, one value" could not describe four of the five.
    /// </para>
    /// <para>Empty for an agent that answers false to <see cref="SupportsSignIns"/>, and empty for the
    /// default account — which is <b>not</b> the same as pointing the variable at the CLI's own
    /// directory. Measured on Claude Code 2.1.251: with <c>CLAUDE_CONFIG_DIR</c> set, it keeps
    /// <c>.claude.json</c> <em>inside</em> that directory, while by default it keeps it at
    /// <c>~/.claude.json</c> and only the credentials in <c>~/.claude</c>. So pointing the variable at
    /// <c>~/.claude</c> yields a session that is logged in and has lost its projects, its MCP servers
    /// and its history — a half-configured account that looks like the real one.</para></remarks>
    IReadOnlyDictionary<string, string?> SignInEnv(string configDirectory);

    /// <summary>
    /// What that directory says about itself: whether the CLI is logged in there, and as whom.
    /// </summary>
    /// <remarks>Null asks about the agent's <em>own</em> default location, which for at least one agent
    /// is laid out differently from a relocated one — see <see cref="SignInEnv"/>. Read from the CLI's
    /// files and never from anything stored here, so logging out in a terminal is reflected on the row
    /// rather than remembered wrongly.</remarks>
    SignInStatus ReadSignIn(string? configDirectory);

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
    /// The model to ask for, spelled the way this CLI expects it.
    /// </summary>
    /// <remarks>
    /// <para><b>Which model and which provider are one string for some agents and two for others</b>,
    /// and that is the agent's business rather than the caller's. Claude Code takes the id verbatim and
    /// is told where to send it by <c>ANTHROPIC_BASE_URL</c>; opencode and pi keep their own registry of
    /// providers, identify them by name, and want <c>provider/model</c> — measured 2026-08-31, opencode
    /// answers a bare id with <c>ProviderModelNotFoundError</c> before any call is made.</para>
    /// <para>Asked once and used by both launch paths, so a tile and a headless goal run cannot spell
    /// the same instance differently. It is the <em>resolved</em> model, never the sentinel: see
    /// <c>AgentRuntime.RequestedModel</c>.</para>
    /// </remarks>
    string QualifiedModel(AgentRuntime runtime);

    /// <summary>
    /// Whether this CLI can be pointed at a service that is not in its own registry — a server on this
    /// machine or this network.
    /// </summary>
    /// <remarks>
    /// <para><b>False is an answer, and it has to be said out loud.</b> Measured 2026-08-31: pi has a
    /// key variable per named service and no generic base-URL setting of any kind — its only address is
    /// Azure's, which is that one service's own — so an instance of pi pointed at LM Studio cannot
    /// reach it, and without this it launched anyway and ran on pi's default provider with nothing on
    /// screen saying so.</para>
    /// <para>Distinct from <c>AiProviderCatalog.IsCompatible</c>, which asks whether the wire formats
    /// meet. They can meet perfectly and still leave no way to say <em>where</em>: opencode and pi both
    /// speak <c>/v1/chat/completions</c>, and only one of them has somewhere to put an address.</para>
    /// </remarks>
    bool SupportsCustomEndpoint { get; }

    /// <summary>
    /// Whether this CLI learns which service to use <em>from the model's name</em> and nowhere else.
    /// </summary>
    /// <remarks><b>True means a provider without a model names nothing.</b> opencode and pi take
    /// <c>provider/model</c>, so the provider half travels on the model; with no model there is no
    /// prefix, and neither CLI fails for want of a provider — each falls back to its own, which for pi
    /// is <c>google</c>. Configured, offered, launched, and the work goes to a service no row on screen
    /// mentions. <c>AgentAvailability</c> refuses that instance rather than letting it happen, because
    /// which model to use is a question only the user can answer.</remarks>
    bool NamesProviderInModel { get; }

    /// <summary>
    /// Anything that has to exist on disk before this agent is started.
    /// </summary>
    /// <remarks>
    /// <para><b>Because a getter is not a place to write files.</b> The one agent that needs this —
    /// opencode, whose only route to a local server is a generated provider document — used to write it
    /// from <c>Configure</c>, which is reached through <c>EnvFor</c>, which is reached through
    /// <c>AgentTileViewModel.LaunchEnvironment</c>: a <em>property</em>. Reading it made a directory and
    /// wrote a file, and the launch reads it twice, so the file was written twice per launch and any
    /// future reader — a debugger's watch window included — would write it again.</para>
    /// <para>Called on both launch paths, which is the reason this is on the agent rather than in
    /// <c>TileLauncher</c>: a headless Goal run does not go through the launcher, and a config prepared
    /// for the tile but not for the run would be the same instance reaching two different services.
    /// Nothing for every other agent, and idempotent for the one that answers.</para>
    /// </remarks>
    void PrepareToLaunch(AgentRuntime runtime);

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
