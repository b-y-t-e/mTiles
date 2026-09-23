using System.Diagnostics;
using mTiles.Models;
using mTiles.Services.Activity;
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
public interface IAiAgent : IAgentActivityReader
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
    /// Where this CLI looks for the project's skills, or null when it reads none.
    /// </summary>
    /// <remarks>
    /// <para>The same shape as <see cref="SignInEnv"/> and <see cref="SessionIdForTile"/>: a question
    /// only the CLI itself can answer, measured once (2026-09-03, against the binaries installed on
    /// this machine). The agent answers <em>where</em>; what goes in there is nothing it is told —
    /// <c>Services/Agents/</c> never learns that databases exist.</para>
    /// <para>Measured: claude <c>.claude/skills</c>, opencode <c>.opencode/skills</c>, and codex, pi
    /// and agy all three <c>.agents/skills</c>. <b>That shared directory is the whole reason
    /// <see cref="WorkspaceAgentFiles"/> exists</b>: closing a pi tile must not take the directory a
    /// codex tile is still reading, so the rule is "recompute the set and delete the difference"
    /// rather than "delete this agent's directory".</para>
    /// <para>Null by default, like <see cref="UsageAsync"/>: an agent whose author forgets this gets no
    /// skill, which is a loss and never a file written somewhere nobody measured.</para>
    /// </remarks>
    /// <param name="workspaceDir">The workspace's own directory, which is where every one of these
    /// CLIs looks first.</param>
    string? SkillsDirectory(string workspaceDir) => null;

    /// <summary>
    /// Whether this CLI, run on <paramref name="surface"/>, notices a skill written into
    /// <see cref="SkillsDirectory"/> while it is already running — and therefore whether the directory has
    /// to exist before it starts.
    /// </summary>
    /// <remarks>
    /// <para>Measured 2026-09-17 against the installed binaries and each CLI's own documentation, and
    /// <b>no agent answers yes</b>. Claude Code 2.1.274 documents a watcher in its terminal interface, and
    /// this used to answer yes there on the strength of it — until 2026-09-18, when a terminal agent tile
    /// on Claude Code did not pick up a second database ticked while it ran, while the yes had silenced
    /// both the notice and the lit Restart. A documented watcher is not a measured one. codex hedges its
    /// claim with "restart Codex"; opencode has an open bug (#49451); pi scans "at startup" and has no
    /// reload verb; agy documents nothing.</para>
    /// <para><b>The surface stays a parameter</b> because a watcher, if one is ever measured, is a fact
    /// about one surface: an Agent tile runs <c>claude -p --output-format stream-json</c>, not the TUI.</para>
    /// <para>No skills directory is made ahead of a skill any more: with nobody watching, an empty one
    /// buys nothing.</para>
    /// <para>False by default, and false is the safe answer: what it costs is a notice asking for a
    /// restart that was not strictly needed, against an agent that never sees the databases the user has
    /// just granted it and nothing on screen saying so.</para>
    /// </remarks>
    /// <param name="surface">Whether the CLI is running as its own terminal interface or as the session
    /// an Agent tile drives.</param>
    bool WatchesSkillsDirectory(AgentSurface surface) => false;

    /// <summary>
    /// This CLI's own record of the conversations it holds, where it keeps one this application can
    /// read. Null where it does not.
    /// </summary>
    /// <remarks>
    /// <para>Two things are read out of it and neither can be had any other way. <b>Which conversation a
    /// tile is really in</b>: every one of these CLIs lets the user change it from inside its own
    /// interface (<c>/clear</c>, <c>/resume</c> and their spellings), at which point the id in the layout
    /// resumes something nobody is looking at — <see cref="SessionStrategy"/> describes how a session
    /// gets its identity at launch and has nothing to say about it moving afterwards. And <b>how full
    /// the model's context is</b>, which a TUI paints into its own footer and no host can read off a
    /// pseudo-terminal.</para>
    /// <para>Null by default, like <see cref="SkillsDirectory"/> and <see cref="UsageAsync"/>: an agent
    /// whose author has measured nothing gets no gauge and keeps the session id it was launched with,
    /// which is exactly what it has today. Measured 2026-09-18, five of the six answer — and agy is the
    /// one that cannot, because its store is protobuf blobs in SQLite with the working directory buried
    /// inside them; it says so in its own class rather than being given a reader nobody has tested.</para>
    /// <para>A property rather than a method taking the instance, because where a CLI keeps its store is
    /// a fact about the CLI. What varies per tile — which sign-in, which workspace — is a parameter of
    /// the reads themselves.</para>
    /// </remarks>
    SessionLogs.IAgentSessionLog? SessionLog => null;

    /// <summary>
    /// Whether a conversation this CLI moved to by itself becomes the one the tile resumes.
    /// </summary>
    /// <remarks>
    /// <para>True wherever <see cref="SessionLog"/> answers, which is the point of having one: the user
    /// typing <c>/clear</c> in the TUI has changed conversation, and a tile that went on resuming the id
    /// it launched with would reopen something nobody is looking at.</para>
    /// <para><b>opencode is the exception and says so in its own class.</b> Its resume is backed by an
    /// import document whose path is a pure function of the <em>tile</em> id, so a followed id would be
    /// resumed by a command whose fallback recreates a different session — a mismatch that only shows
    /// when the followed conversation is gone, which is the worst moment for it to show. Its gauge still
    /// works; only the adoption is withheld.</para>
    /// <para>Answering true where <see cref="SessionLog"/> is null costs nothing: with no reader there
    /// is nothing to follow.</para>
    /// </remarks>
    bool FollowsSessionChanges => true;

    /// <summary>
    /// How large a context this CLI is served for a model on <em>its own account</em>, where it has a
    /// way to ask.
    /// </summary>
    /// <remarks>
    /// <para><b>The one question a provider cannot answer, because there is no provider.</b> An agent
    /// running on a subscription has no <c>AiProviderInstance</c> at all — which is the commonest
    /// configuration there is — so <c>ModelContextWindow.ContextOfAsync</c> has nothing to call and the
    /// context bar had no denominator. Only the CLI's own service knows, and only the CLI's own
    /// credentials can ask it.</para>
    /// <para>Measured 2026-09-18: Claude Code's OAuth token gets a <c>200</c> out of
    /// <c>api.anthropic.com/v1/models</c> with <c>max_input_tokens</c> per model. The other five have no
    /// such route measured, answer null, and show a count with no bar — which is the honest outcome, and
    /// the reason there is no guess anywhere behind this: a flat assumption drew a full bar over a
    /// conversation at a quarter of its real window.</para>
    /// <para>Read-only, cached by the implementation, and it never throws: what a failure costs is a
    /// bar.</para>
    /// </remarks>
    /// <param name="signIn">Which login to ask as, or null for the CLI's own default account.</param>
    Task<long?> AccountContextWindowAsync(AiSignIn? signIn, string model, CancellationToken ct = default) =>
        Task.FromResult<long?>(null);

    /// <summary>
    /// The project instruction file this CLI opens. <c>AGENTS.md</c> is the canon.
    /// </summary>
    /// <remarks>Measured 2026-09-03: opencode, codex, pi and agy all read <c>AGENTS.md</c>; only Claude
    /// Code does not — it loads <c>CLAUDE.md</c> by a hard-coded path and has no discovery for
    /// <c>AGENTS.md</c> at all. An agent that names its own file here gets a one-line shim
    /// (<c>@AGENTS.md</c>) rather than a second copy of the content; see
    /// <see cref="WorkspaceAgentFiles"/> and <c>docs/AGENTS-MD-SYNC.md</c>.</remarks>
    string InstructionFile => WorkspaceAgentFiles.CanonicalInstructionFile;

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
    /// How much of this account's allowance is left, or null when this CLI reports none.
    /// </summary>
    /// <remarks>
    /// <para><b>Null and a failed report are different answers.</b> Null is "there is no such question
    /// here" — an agent that publishes no limits, or a default account nobody has logged into on this
    /// machine — and nothing is recorded at all. A report carrying a <c>Problem</c> is an account that
    /// exists and could not be asked, and its sentence reaches the log through
    /// <c>AiUsageService.Explain</c>. Neither draws a card, and that is the tile's decision rather than
    /// this contract's; what this must never answer is a zero for either.</para>
    /// <para>Per sign-in <em>and</em> for the default account, because a second subscription is a
    /// second set of limits — which is the whole reason <see cref="SupportsSignIns"/> exists.</para>
    /// <para>Never throws, the rule <c>AiProvider</c> is built on: every caller is a piece of UI that
    /// has to say something either way.</para>
    /// </remarks>
    /// <param name="signIn">The login to ask about, or null for the CLI's own default account.</param>
    Task<AiUsageReport?> UsageAsync(AiSignIn? signIn, CancellationToken ct = default);

    /// <summary>The key one account's readings are filed under.</summary>
    /// <remarks>The same formula <see cref="UsageAsync"/> hands back as <c>AiUsageReport.SourceId</c>,
    /// promoted here so <c>AiUsageService</c>'s per-account throttle can be keyed on it without asking
    /// anybody anything first — the agent's id and the sign-in's, never a name, since nothing makes a
    /// sign-in's name unique and a renamed row must not start a fresh history or adopt somebody
    /// else's.</remarks>
    string UsageSourceId(AiSignIn? signIn);

    /// <summary>
    /// Which login this row is, where that can be told before anybody is asked anything.
    /// </summary>
    /// <remarks>
    /// <para>The same answer <c>AiUsageReport.AccountKey</c> carries, available <em>ahead</em> of the
    /// call rather than in its result. Two rows can be one login — a sign-in's directory that is also
    /// what <c>CLAUDE_CONFIG_DIR</c> points at is the ordinary case — and finding that out from the
    /// reports meant asking the service twice for one account every round, which is what the usage
    /// endpoint's 429s were.</para>
    /// <para><b>Null means "cannot say", and it is never treated as a match.</b> Two rows folded
    /// together wrongly is a subscription missing from the tile, which nobody notices; two rows kept
    /// apart wrongly is the extra call that is happening today anyway. The doubt is spent on the
    /// harmless side.</para>
    /// <para>Read from this machine only — a file the CLI already wrote — because a question asked to
    /// decide whether to ask a question must not itself be a request.</para>
    /// </remarks>
    /// <param name="signIn">The login to name, or null for the CLI's own default account.</param>
    string? UsageAccountKeyFor(AiSignIn? signIn);

    /// <summary>
    /// What a terminal agent tile runs: the command that resumes <paramref name="sessionId"/>, and the one to
    /// try when it does not work.
    /// </summary>
    /// <remarks><para>In code rather than in a user-editable field, which is the whole difference between an
    /// agent and the shell profile it replaces. <c>LaunchScripts</c> rather than a type of its own —
    /// the launch chain already reads exactly this pair, and a second shape for it would be two
    /// vocabularies for one idea.</para>
    /// <para><b>The instance is not decoration.</b> Its
    /// <see cref="AiAgentInstance.DefaultBehaviour"/>, <see cref="AiAgentInstance.DefaultEffort"/> and
    /// <see cref="AiAgentInstance.ExtraArgs"/> reach both commands, fitted to what this agent supports
    /// interactively — the instance's settings apply "wherever the instance is used", and a terminal agent tile
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

    /// <summary>What this application passes to every session it holds with a person on the other end
    /// — a terminal agent tile and an Agent tile alike — ahead of the instance's own
    /// <see cref="AiAgentInstance.ExtraArgs"/>, so an argument typed there still has the last word.</summary>
    /// <param name="runtime">The session being launched, so an answer can depend on what its row
    /// says — today, whether it asked for the output proxy — and on the sign-in it runs as.</param>
    IReadOnlyList<string> SessionDefaultArgs(AgentRuntime runtime);

    /// <summary>
    /// Whether this CLI can be given the output proxy (<c>rtk</c>), and by which route.
    /// </summary>
    /// <remarks>
    /// <para>Measured 2026-09-22 against rtk 0.46.0, whose own <c>init --agent</c> lists claude,
    /// cursor, windsurf, cline, kilocode, antigravity, kimi, pi, hermes, droid and vibe — so three of
    /// the agents here are named by it and each was probed against a sandboxed config directory:</para>
    /// <list type="bullet">
    /// <item><b>Claude Code</b> — a <c>PreToolUse</c> hook in a settings file, and this application
    /// already hands every Claude Code session a generated one. <see cref="OutputProxy.Support.GeneratedFile"/>.</item>
    /// <item><b>pi</b> — <c>rtk init --agent pi</c> writes <c>&lt;PI_CODING_AGENT_DIR&gt;/extensions/rtk.ts</c>
    /// and says in its own output that it can be loaded with <c>pi -e &lt;path&gt;</c>. That is the same
    /// shape as Claude Code's and is the reason this answer is an enum rather than a bool — it is the
    /// next one to wire, and what it still needs is for that file to be generated into a directory this
    /// application owns rather than the CLI's default.</item>
    /// <item><b>opencode</b> — <c>rtk init --opencode</c> writes a plugin into
    /// <c>~/.config/opencode/plugins/</c>, which is the user's own configuration, and opencode has no
    /// flag that carries a plugin for one run. <see cref="OutputProxy.Support.WritesOutsideOurDirectories"/>:
    /// the route is real, it is named, and taking it would make a per-instance tick change every
    /// opencode session on the machine.</item>
    /// </list>
    /// <para>No body here — <c>AiAgent</c> answers <see cref="OutputProxy.Support.None"/> by default, like <see cref="SkillsDirectory"/> and
    /// <see cref="SessionLog"/>: an agent nobody has measured gets no proxy, which costs tokens and
    /// never rewrites a command nobody checked.</para>
    /// </remarks>
    OutputProxy.Support OutputProxySupport { get; }

    /// <summary>Whether the user's own configuration for this CLI already routes its commands through the output proxy.</summary>
    /// <remarks>The agent's question because only the agent knows where its own settings live: asked by the launch, which then adds nothing, and by the Settings form, which says so.</remarks>
    bool IsOutputProxyAlreadyHooked(AiSignIn? signIn, string? workspaceDirectory = null);

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
    /// The inverse of <see cref="QualifiedModel"/>: a model spelled this CLI's way — as a running session
    /// lists it — turned back into what an instance stores, so qualifying it again at the next launch gives
    /// the same string rather than the provider twice.
    /// </summary>
    string InstanceModel(AgentRuntime runtime, string qualifiedModel);

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
    /// <c>TerminalAgentTileViewModel.LaunchEnvironment</c>: a <em>property</em>. Reading it made a directory and
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
    /// Whether a session id this tile holds is handed back to the CLI at its next launch.
    /// </summary>
    /// <remarks>
    /// <para>True for every agent whose <c>Resume</c> carries the id. <b>False where nothing survives a
    /// restart</b> — Grok, whose terminal resume has not been measured, and a binary nothing is known
    /// about — and there the id is only this launch's: it names the conversation the session store is
    /// read for, it is dropped at the next launch so the fresh conversation is captured in its place, and
    /// it is never written into the layout, where it would name something no launch will open.</para>
    /// </remarks>
    bool ResumesTerminalSession => true;

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
