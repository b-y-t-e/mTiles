using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using mTiles.Models;
using mTiles.Services.Providers;

namespace mTiles.Services.Agents;

/// <summary>
/// Anthropic's Claude Code.
/// </summary>
/// <remarks>
/// <para>The one agent here that both streams and reads its prompt from standard input, which is why
/// its <see cref="ParseLine"/> is the only one that says anything structured — the rest of the file is
/// reading somebody else's JSON, and every rule in it was paid for by a run that reported the wrong
/// thing.</para>
/// <para><b>Sessions are the easy case:</b> <c>--session-id &lt;uuid&gt;</c> creates one and
/// <c>--resume &lt;uuid&gt;</c> continues it — each refusing what the other wants — and a tile's
/// <c>TileId</c> is already a hyphenated GUID, so <see cref="SessionStrategy.Fixed"/> and no
/// bookkeeping anywhere. See <see cref="Resume"/> for why the continuing command goes first.</para>
/// <para><b>Six permission modes, measured</b>: <c>acceptEdits</c>, <c>auto</c>,
/// <c>bypassPermissions</c>, <c>manual</c>, <c>dontAsk</c>, <c>plan</c>. This application knew three of
/// them, which is how the plan and review phases had no read-only mode to run in.</para>
/// </remarks>
public sealed class ClaudeAgent : AiAgent
{
    public override string Id => "claude";
    public override string DisplayName => "Claude Code";
    public override string BinaryName => "claude";
    public override string? InstallUrl => "https://docs.anthropic.com/en/docs/claude-code";
    public override SessionStrategy SessionStrategy => SessionStrategy.Fixed;

    public override InstallPlan? InstallPlan => new("npm",
        ["install", "-g", "@anthropic-ai/claude-code"],
        "Installs Claude Code globally through npm, which has to be on PATH already.");

    /// <summary>Anthropic's own wire format, which OpenRouter and z.ai also serve. Not the two OpenAI
    /// shapes: Claude Code speaks <c>/v1/messages</c> and nothing else.</summary>
    public override IReadOnlyList<ApiFlavor> ConsumesApiFlavors => [ApiFlavor.Anthropic];

    /// <summary>
    /// Where Claude Code talks and what it authenticates with, when the instance names a provider.
    /// </summary>
    /// <remarks>
    /// <para><b><c>ANTHROPIC_API_KEY</c> is unset, not left alone,</b> and that one null is the reason
    /// stage 2 existed at all: on a machine that exports a global key, a block that could only add
    /// would leave the inherited key in place beside the token we just set, and the run would go to
    /// Anthropic on somebody else's account rather than to the provider the user configured.</para>
    /// <para>The token variable rather than the key one because that is the pair Claude Code reads for
    /// a third-party endpoint. A provider that serves no Anthropic-shaped endpoint contributes nothing
    /// here — <see cref="AgentRuntime.EndpointFor"/> answers null — which is the same rule as "not
    /// compatible", said once.</para>
    /// </remarks>
    protected override void Configure(IDictionary<string, string?> environment, AgentRuntime runtime)
    {
        // An instance on a *sign-in* names no provider, so the branch below never ran for it and a
        // globally exported ANTHROPIC_API_KEY stayed in the environment — the CLI then authenticated
        // with that key instead of the OAuth login in the directory the row names, and the work went to
        // another account with nothing on screen saying so. Exactly the failure the branch below exists
        // to prevent, reached by the other route.
        //
        // Clearing only (`setKey: false`): it authenticates through ANTHROPIC_AUTH_TOKEN below, so the
        // provider's own key variable would be a secret sitting in the tile's shell that nothing reads.
        ApplyProviderKey(environment, runtime, setKey: false);

        // The pair below, removed when this instance runs as a *sign-in*. ApplyProviderKey clears only
        // variables that are some provider's KeyEnvironmentVariable, and neither of these is one:
        // ANTHROPIC_AUTH_TOKEN is what this CLI actually authenticates with, and ANTHROPIC_BASE_URL is
        // where it sends it. An instance on a subscription names no provider, so the branch below never
        // runs for it - and on a machine exporting either of them globally, a tile whose row says
        // "work subscription" ran on somebody else's token, at somebody else's address, with nothing on
        // screen saying so. The same failure ANTHROPIC_API_KEY is unset for, reached by a variable that
        // is not on any list.
        //
        // Only for a sign-in: an instance that has chosen no account at all runs on the CLI's own
        // configuration, and a globally exported endpoint is part of that configuration.
        if (runtime.SignIn is not null)
        {
            environment["ANTHROPIC_BASE_URL"] = null;
            environment["ANTHROPIC_AUTH_TOKEN"] = null;
        }

        // The model pair, on the same rule and for either kind of account. They are nobody's key
        // variable either, and they were the third and fourth of a rule applied to two: on a machine
        // exporting ANTHROPIC_MODEL globally, an instance that names no model ran on somebody else's
        // while the row and the tile header both showed none. What the row says is what runs, so where
        // an account has been chosen the model is this instance's or the CLI's own - never a leftover
        // from the environment. Set again below when the instance names one.
        //
        // Not for an instance that has chosen no account at all: that is the CLI's own configuration,
        // and a globally exported model is part of it.
        if (runtime.SignIn is not null || runtime.Provider is not null)
        {
            environment["ANTHROPIC_MODEL"] = null;
            // The deprecated spelling, still read by the CLI, and the alias pins that hold the rest of
            // the vocabulary: every one of these is a model variable a machine may export, and the
            // same rule that clears ANTHROPIC_MODEL clears them — what the row says is what runs.
            environment["ANTHROPIC_SMALL_FAST_MODEL"] = null;
            environment["ANTHROPIC_DEFAULT_OPUS_MODEL"] = null;
            environment["ANTHROPIC_DEFAULT_SONNET_MODEL"] = null;
            environment["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = null;
            environment["ANTHROPIC_DEFAULT_FABLE_MODEL"] = null;
            // And the compaction window is the same question one more time: a machine exporting it
            // globally would run a session whose provider said nothing about its model on a threshold
            // inherited from somewhere else — the session running past the model's context by the one
            // path the unset exists to close. The assumed window beside it, for the same reason — a
            // global export there would override the CLI's own knowledge of models it does know.
            environment["CLAUDE_CODE_AUTO_COMPACT_WINDOW"] = null;
            environment["CLAUDE_CODE_MAX_CONTEXT_TOKENS"] = null;
        }

        if (runtime.EndpointFor(ApiFlavor.Anthropic) is { } endpoint
            && runtime.Provider is { } provider
            && runtime.ProviderInstance is { } configured)
        {
            environment["ANTHROPIC_BASE_URL"] = endpoint.ToString();
            // What to authenticate with is the provider's answer, not a rule spelled here: the key
            // typed on the instance, or — for a server that takes none — the word that stands in for
            // one, or, for one that manages its own (CCS), the key its config hands out. The measured
            // reasoning about empty tokens lives with the member, which is where every agent that
            // presents a credential will ask the same question.
            environment["ANTHROPIC_AUTH_TOKEN"] = provider.ClientToken(configured);
            // Empty rather than removed, and an inherited global key is still shut out — an empty
            // value overrides it as surely as a removal does, and the CLI cannot authenticate with an
            // empty key. The distinction is the gateway's own recipe (OpenRouter's Claude Code
            // cookbook, read 2026-09-01): "must be explicitly empty, not unset" — the CLI's auth
            // resolution treats a missing variable and a present-but-empty one differently, and the
            // missing one lets a cached claude.ai login answer questions it should not.
            environment["ANTHROPIC_API_KEY"] = "";
            // Headless model validation against a gateway the CLI does not have a family for. Without
            // this, `-p` refuses a model id it cannot match against Anthropic's own catalogue before
            // asking the model anything — measured 2026-09-01 against 2.1.250–2.1.252 with
            // `z-ai/glm-5.3-flash` on OpenRouter, every spelling, env and flag alike. The switch is
            // the documented one (the same cookbook), and it makes the CLI ask the gateway's model
            // list instead: the provider then answers for the ids it serves. The CLI handles a
            // gateway that fails to answer — the discovery path logs its own failure and the run
            // carries on — which is why it is set for every provider rather than only the ones this
            // application has measured serving a list.
            environment["CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY"] = "1";
        }

        if (runtime.RequestedModel.Length > 0)
        {
            environment["ANTHROPIC_MODEL"] = runtime.RequestedModel;

            // The aliases are how the CLI reaches for a large model beside the session's own: a
            // plan-mode upgrade, the auto-mode classifier, everything that asks for opus, sonnet or
            // fable by name. On a third-party provider those names resolve to Anthropic ids nobody
            // serves, so the model the session runs on answers for them all. Only there, though: on
            // the CLI's own account or a subscription every alias resolves to a model that exists,
            // and a pin here would stand between the user and /model.
            if (runtime.Provider is not null)
            {
                environment["ANTHROPIC_DEFAULT_OPUS_MODEL"] = runtime.RequestedModel;
                environment["ANTHROPIC_DEFAULT_SONNET_MODEL"] = runtime.RequestedModel;
                environment["ANTHROPIC_DEFAULT_FABLE_MODEL"] = runtime.RequestedModel;
            }
        }

        // The small, frequent calls follow the row: the instance's FastModel where it names one, and —
        // on a third-party provider only — the same model the real calls run on when it does not. Left
        // unset there, the CLI answers these calls with its own default small model, an Anthropic id
        // that provider does not serve, so on OpenRouter every one of them failed while the real ones
        // worked. Nowhere else, though: on the CLI's own account or a subscription that default exists
        // and answers, and the fallback here would instead move every background call onto the
        // session's large model — the very thing "small, frequent" is not. A Fast model typed on the
        // row is a decision and is handed over wherever the CLI reads it.
        if (runtime.Instance.FastModel.Length > 0)
            environment["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = runtime.Instance.FastModel;
        else if (runtime.Provider is not null && runtime.RequestedModel.Length > 0)
            environment["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = runtime.RequestedModel;

        // The auto-compact window. On a third-party provider the CLI does not recognise the model id
        // and assumes a window that can be wrong by half, so the provider's own answer is named here
        // instead — resolved by ModelContextWindow, whose answer is already the reduced window (80%,
        // clamped), and is carried by the runtime under that meaning. On the CLI's own account the
        // resolution stays out of the way, because the CLI knows its models and an empty field is that
        // configuration's own compaction behaviour. A window typed on the instance is a decision and
        // is honoured at every launch, whichever account it runs as — the same rule the fast model
        // follows, and the promise the field's hint makes. Null answers set nothing: an unknown window
        // is the CLI's own assumption, not a number we made up.
        if ((runtime.Instance.AutoCompactWindow ?? runtime.AutoCompactWindow) is { } window)
            environment["CLAUDE_CODE_AUTO_COMPACT_WINDOW"] =
                window.ToString(CultureInfo.InvariantCulture);

        // The assumed window, beside it — the other half of the same resolution, and the answer to
        // the failure the compact window alone cannot reach: CLAUDE_CODE_AUTO_COMPACT_WINDOW moves
        // when compaction fires, but the CLI's belief about how much context the model has stays at
        // its own 200 000 assumption for an id it does not recognise, and the hard "Context limit
        // reached" stop fires there first. Measured 2026-09-01 on z-ai/glm-5.3-flash, advertised at
        // 1 310 720: compact window 1 000 000, stop at 199.8k. CLAUDE_CODE_MAX_CONTEXT_TOKENS is the
        // documented variable for exactly this — "override the context window size Claude Code
        // assumes for the active model" — and the answer carried here is the provider's context at
        // 100%, the assumption being corrected being a fact the margin is not. Typed on the instance
        // it wins, as the compact window does: a provider can advertise more than its upstream
        // serves, and the row is where that is corrected. Null answers set nothing, and on the CLI's
        // own account the resolution never answered at all — the CLI knows its models there.
        if ((runtime.Instance.MaxContextTokens ?? runtime.MaxContextTokens) is { } assumed)
            environment["CLAUDE_CODE_MAX_CONTEXT_TOKENS"] =
                assumed.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Through the environment, which is where its base URL and token already go.</summary>
    /// <remarks><c>claude --model</c> exists and is deliberately not used: <c>ANTHROPIC_MODEL</c>
    /// reaches the headless run and the interactive tile by one route instead of two.</remarks>
    public override bool AcceptsModel => true;

    /// <summary>
    /// The one agent that reads the model's context window: on a third-party provider it assumes a
    /// window for ids it does not recognise, and the assumption can be wrong by half.
    /// </summary>
    /// <remarks>That answer is what spends a provider call on resolving the window at every launch —
    /// see <c>ModelContextWindow</c> for what the answer becomes, and the gate that keeps the call
    /// away from the five agents that read none of it.</remarks>
    public override bool UsesModelContextWindow => true;

    /// <summary>
    /// The small, frequent calls take a second model here: <c>ANTHROPIC_DEFAULT_HAIKU_MODEL</c>.
    /// </summary>
    /// <remarks>Set in <see cref="Configure"/>: the instance's fast model, or — empty, on a
    /// third-party provider only — the same model the real calls run on. The fallback is the part that
    /// matters: left unset there, the CLI answered these calls with its own default small model, an
    /// Anthropic id that provider does not serve, so every one of them failed while the real ones
    /// worked. On the CLI's own account the default exists and the field left empty leaves it alone.
    /// Read through the current spelling — <c>ANTHROPIC_SMALL_FAST_MODEL</c> is deprecated in this
    /// one's favour, measured in the environment-variables reference on 2026-08-31.</remarks>
    public override bool UsesFastModel => true;

    /// <summary>
    /// <c>CLAUDE_CONFIG_DIR</c>, measured 2026-08-30 against Claude Code 2.1.251.
    /// </summary>
    /// <remarks>Pointed at an empty directory it answers <c>Not logged in · Please run /login</c> and
    /// builds its own <c>.claude.json</c>, <c>sessions/</c> and <c>projects/</c> there — so it reads
    /// <em>none</em> of <c>~/.claude</c>, which is exactly what a second subscription needs. It also
    /// takes the conversations with it, which is why a sign-in added to an instance that already has
    /// tiles is a different set of sessions rather than the same ones on another account.</remarks>
    public override bool SupportsSignIns => true;

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string?> SignInEnv(string configDirectory) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CLAUDE_CONFIG_DIR"] = configDirectory,
        };

    /// <summary>
    /// The account behind a directory, from the two files Claude Code writes.
    /// </summary>
    /// <remarks><b>The two layouts are not the same, and that is measured rather than assumed.</b> A
    /// relocated configuration keeps <c>.claude.json</c> inside the directory; the default one keeps it
    /// at <c>~/.claude.json</c> and puts only <c>.credentials.json</c> in <c>~/.claude</c>. Asking the
    /// wrong one is how the default account would come out as "not signed in" on a machine that is
    /// signed in perfectly well.</remarks>
    public override SignInStatus ReadSignIn(string? configDirectory)
    {
        var (settingsFile, credentialsFile) = FilesFor(configDirectory);

        // The file's existence is what says logged in; its contents only say who. Reading a field first
        // and answering NotSignedIn when it is missing put a Sign in button over a working login — a
        // subscription is not the only way to authenticate, and a format that moves must not turn an
        // account into an offer to create one. This is the policy CodexAgent already states in its own
        // words, and the two had drifted apart.
        if (!File.Exists(credentialsFile)) return SignInStatus.NotSignedIn;

        var plan = ReadJsonString(credentialsFile, "claudeAiOauth", "subscriptionType");
        var email = ReadJsonString(settingsFile, "oauthAccount", "emailAddress");

        var detail = string.Join(" · ", new[]
        {
            email,
            plan is { Length: > 0 } ? Capitalised(plan) : null,
        }.Where(part => part is not null));

        return detail.Length > 0 ? new SignInStatus(true, detail) : SignInStatus.SignedInAnonymously;
    }

    /// <summary>
    /// The two files a login is read from: the one naming the account and the one holding the token.
    /// </summary>
    /// <remarks><b>The two layouts are not the same, and that is measured rather than assumed.</b> A
    /// relocated configuration keeps <c>.claude.json</c> inside the directory; the default one keeps it
    /// at <c>~/.claude.json</c> and puts only <c>.credentials.json</c> in <c>~/.claude</c>. Asking the
    /// wrong one is how the default account would come out as "not signed in" on a machine that is
    /// signed in perfectly well — which is why the rule is stated once and read by both the sign-in row
    /// and the usage question.</remarks>
    private static (string Settings, string Credentials) FilesFor(string? configDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // A machine that already exports CLAUDE_CONFIG_DIR has its default account *there*, so asking
        // about ~/.claude would report a working login as signed out - the false "not signed in" this
        // rule exists to avoid.
        configDirectory ??= Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");

        return configDirectory is { Length: > 0 } directory
            ? (Path.Combine(directory, ".claude.json"), Path.Combine(directory, ".credentials.json"))
            : (Path.Combine(home, ".claude.json"), Path.Combine(home, ".claude", ".credentials.json"));
    }

    /// <summary>
    /// The five-hour and seven-day windows this subscription reports, or null where nobody is logged in.
    /// </summary>
    /// <remarks><para><b>A login that is not there answers null, a login that is there and cannot be
    /// asked answers a sentence.</b> That is the whole distinction <see cref="IAiAgent.UsageAsync"/>
    /// draws: a machine where nobody has run <c>claude</c> has no such account to report on, while a
    /// sign-in row the user made and never logged into is an account that owes them an explanation —
    /// which reaches the log rather than the tile. This answers null only when there is no credential
    /// at all.</para>
    /// <para>The token is read here and handed straight to <see cref="ClaudeUsageReader"/>, held for the
    /// length of one call: what it authenticates is somebody else's service, and nothing in this
    /// application stores, logs or shows it.</para></remarks>
    public override async Task<AiUsageReport?> UsageAsync(AiSignIn? signIn,
        CancellationToken ct = default)
    {
        var directory = signIn is null ? null : AiSignInStore.DirectoryFor(signIn);
        var credentialsFile = FilesFor(directory).Credentials;
        var name = UsageSourceName(signIn);
        var sourceId = UsageSourceId(signIn);
        var now = DateTimeOffset.Now;

        if (ReadJsonString(credentialsFile, "claudeAiOauth", "accessToken") is not { Length: > 0 } token)
            return signIn is null
                ? null
                : AiUsageReport.Failed(sourceId, name,
                    "Nobody is signed in here, so there is no allowance to report.", now);

        var plan = ReadJsonString(credentialsFile, "claudeAiOauth", "subscriptionType");

        return await ClaudeUsageReader.ReadAsync(sourceId, name,
            plan is { Length: > 0 } ? Capitalised(plan) : null, token, now, ct);
    }

    /// <summary>"max" is what the file says; "Max" is what the subscription is called.</summary>
    private static string Capitalised(string word) =>
        char.ToUpperInvariant(word[0]) + word[1..];

    /// <summary>
    /// All six of Claude Code's modes interactively; headlessly, only the two that cannot become a
    /// silent denial.
    /// </summary>
    /// <remarks>
    /// A headless run has nobody to ask, so every mode that asks turns a refusal into a tool call that
    /// simply fails — which is what <c>AiChunkKind.Denied</c> counts. <see cref="AiBehaviour.AcceptEdits"/>
    /// is out for the same reason and is the worst of the three, because it looks like it is working:
    /// the edits go through and every other tool call is quietly refused. What is left is
    /// <see cref="AiBehaviour.Auto"/> and <see cref="AiBehaviour.BypassPermissions"/>, and even auto can
    /// refuse — the difference is one of degree, which is what the denial counter is for.
    /// <para>A phase that writes nothing is given <see cref="AiBehaviour.Plan"/> outright by
    /// <see cref="BehaviourArgs"/>; it is on this list so that a caller asking what a plan phase
    /// supports is not told something the phase will not be run in.</para>
    /// </remarks>
    public override IReadOnlyList<AiBehaviour> SupportedBehaviours(AiAgentInstance instance, AiUsage usage) =>
        !usage.IsHeadless
            ? [AiBehaviour.Plan, AiBehaviour.Ask, AiBehaviour.AcceptEdits, AiBehaviour.Auto,
               AiBehaviour.BypassPermissions, AiBehaviour.ToolDefault]
            : !usage.MayOnlyRead
                ? [AiBehaviour.Auto, AiBehaviour.BypassPermissions, AiBehaviour.ToolDefault]
                : [AiBehaviour.Plan, AiBehaviour.ToolDefault];

    /// <summary>The canonical scale, which <c>claude --effort</c> happens to spell the same way.
    /// <para>Whether the provider's model can do anything with a level is not asked here:
    /// <c>--effort</c> is Claude Code's own abstraction over a thinking budget, so the provider's list
    /// is irrelevant to it.</para></summary>
    public override IReadOnlyList<AiEffort> SupportedEfforts(AiAgentInstance instance, AiUsage usage) =>
        FullEffortScale;

    /// <summary>
    /// Measured: an unrecognised <em>value</em> is forgiving — the tool warns and uses its own default
    /// — but an unrecognised <em>flag</em> is not, and a Claude Code from before <c>--effort</c>
    /// existed runs nothing at all. <see cref="AiEfforts.LooksLikeRejectedEffort"/> is what turns that
    /// into a sentence naming the way out rather than "the AI tool reported a failure".
    /// </summary>
    public override IReadOnlyList<string> EffortArgs(AiEffort effort, AiUsage usage) =>
        AiEfforts.Name(effort) is { } level ? ["--effort", level] : [];

    /// <summary>
    /// The mode, said out loud rather than inherited.
    /// </summary>
    /// <remarks>
    /// <para>Without it the run takes whatever mode the user's own Claude Code settings are in, and the
    /// factory default is to ask — which a <c>-p</c> run cannot do, so every edit is refused and the
    /// implementation writes nothing. <see cref="AiBehaviour.ToolDefault"/> is the way back to that
    /// inheritance for somebody who wants it, and it is the only mode that adds no flag.</para>
    /// <para>A headless phase that neither edits the repository nor runs its own build is put in
    /// <c>plan</c> whatever it asked for — <c>AiUsage.MayOnlyRead</c>. That is
    /// decision 9 — the user chooses for the execution phase and the agent chooses for the rest — and
    /// it matters more now that review can run as a <em>second</em> agent: two agents writing into one
    /// worktree is something <c>GoalBaseline</c> only photographed once.</para>
    /// </remarks>
    public override IReadOnlyList<string> BehaviourArgs(AiBehaviour behaviour, AiUsage usage)
    {
        if (usage.MayOnlyRead)
            return ["--permission-mode", "plan"];

        return Mode(behaviour) is { } mode ? ["--permission-mode", mode] : [];
    }

    /// <summary>Claude Code's own spelling of a canonical mode, or null for "pass no flag".
    /// <para>Here rather than in <see cref="AiBehaviours"/>, which is where it used to be under a
    /// neutral name — and that is how a second agent got given the first agent's flags.</para></summary>
    private static string? Mode(AiBehaviour behaviour) => behaviour switch
    {
        AiBehaviour.Plan => "plan",
        AiBehaviour.Ask => "manual",
        AiBehaviour.AcceptEdits => "acceptEdits",
        AiBehaviour.Auto => "auto",
        AiBehaviour.BypassPermissions => "bypassPermissions",
        _ => null,
    };

    /// <summary>
    /// <c>claude --resume &lt;tileId&gt;</c>, falling back to <c>claude --session-id &lt;tileId&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>The order is the whole of it, and it is not symmetrical.</b> Measured against Claude
    /// Code 2.1.251: <c>--session-id</c> <em>creates</em> the session and refuses an id that already
    /// exists ("Session ID … is already in use", exit 1), while <c>--resume</c> continues one and
    /// refuses one that does not. So the first launch of a tile fails on <c>--resume</c>, is created by
    /// the fallback, and every launch after it is resumed by the startup command — which is exactly
    /// what the chain's "non-zero and short-lived, so try the next command" rule delivers.</para>
    /// <para>Putting the creating command first is what loses the conversation: from the second launch
    /// on it fails as fast, the chain drops to the fallback, and a fallback that is a plain
    /// <c>claude</c> starts a session with no id at all — so the next restart repeats it and no launch
    /// ever resumes anything. If both commands are refused the chain still ends at an interactive
    /// shell, so the tile is not left dead.</para>
    /// </remarks>
    protected override LaunchScripts Resume(string sessionId) =>
        LaunchScripts.FromProfile($"claude --resume {sessionId}", $"claude --session-id {sessionId}");

    /// <summary><c>claude -p</c> with no prompt after it reads the prompt from standard input.</summary>
    public override bool AcceptsPromptOnStdin => true;

    public override bool SupportsStreaming => true;

    /// <summary>
    /// The command line, and one flag that is deliberately absent.
    /// </summary>
    /// <remarks>
    /// <para><b>No <c>--max-turns</c>, at any number.</b> It was 20, then 200, and both were my numbers
    /// rather than anything about the work. An agent that reads a few files, loads a skill and then
    /// edits spends turns quickly, so a ceiling is reachable in ordinary work: the 200 was hit half way
    /// through a real implementation, which is the failure the 20 was raised for. Reporting the stop
    /// honestly — <c>error_max_turns</c> arrives as a result line marked as an error, and
    /// <see cref="ParseLine"/> reports it as one — makes a truncated run visible, but a visible
    /// truncation is still a truncation, and the tile is meant to be left alone for hours. Turns are
    /// the wrong unit for this: what they count is the tool's inner loop, and what the user cares about
    /// is the work.</para>
    /// <para><b>Which leaves one run with no ceiling of any kind, and that is the accepted risk rather
    /// than something covered elsewhere.</b> The attempt budget bounds how many runs a goal gets, not
    /// how long one lasts, and <c>AiProcessRunner.RunPlainAsync</c> deliberately has no wall-clock timeout — so
    /// Pause is the whole of the stop. Not "the user can set one in their settings": measured against
    /// Claude Code 2.1.251, <c>maxTurns</c> is a hidden CLI flag, a field in an agent file's front
    /// matter and an SDK option, and <c>settings.json</c> has no equivalent at all. The only ceiling
    /// available for this run is the flag on this line, which is exactly the one being refused.</para>
    /// <para><c>--verbose</c> is not optional with <c>stream-json</c>: print mode refuses the pair
    /// without it.</para>
    /// </remarks>
    public override void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiUsage usage, AiBehaviour behaviour = AiBehaviour.Auto,
        AiEffort effort = AiEffort.High, string model = "")
    {
        // No prompt argument: `claude -p` with nothing after it reads it from standard input, which
        // AiProcessRunner writes. `prompt` is unused here for exactly that reason.
        psi.ArgumentList.Add("-p");

        // Both fragments whole, from the one place that knows how this agent spells them. They used to
        // be built again here, which is how a mode could be added to the table and never reach a
        // command line.
        foreach (var argument in BehaviourArgs(behaviour, usage).Concat(EffortArgs(effort, usage)))
            psi.ArgumentList.Add(argument);

        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add(streaming ? "stream-json" : "text");

        if (!streaming) return;

        psi.ArgumentList.Add("--verbose");
    }

    public override IReadOnlyList<AiOutputChunk> ParseLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return [];

            var type = typeProp.GetString() ?? "";

            if (type == "assistant" && root.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var contentArr)
                && contentArr.ValueKind == JsonValueKind.Array)
            {
                // Both halves, in the order the message wrote them. This used to return the tool call
                // and drop the prose, on the grounds that the answer comes from the result line anyway
                // — true until the run is interrupted, which is the one case where what it managed to
                // say is all there is.
                var said = new List<AiOutputChunk>();

                foreach (var block in contentArr.EnumerateArray())
                {
                    if (block.TryGetProperty("text", out var text)
                        && text.GetString() is { Length: > 0 } prose)
                        said.Add(new AiOutputChunk { Kind = AiChunkKind.Text, Content = prose });

                    if (Activity(block) is { Length: > 0 } doing)
                        said.Add(new AiOutputChunk { Kind = AiChunkKind.Activity, Content = doing });
                }

                if (said.Count > 0) return said;
            }

            // A refused tool call comes back as a user turn carrying the tool_result, not as an error
            // line, so nothing above this ever saw one: the run looked like an agent that read some
            // files and decided to change nothing. Counted here so the tile can tell "it declined the
            // work" from "it was not allowed to do the work" — the two produce an identical worktree.
            if (type == "user" && root.TryGetProperty("message", out var userMsg)
                && userMsg.TryGetProperty("content", out var userContent)
                && userContent.ValueKind == JsonValueKind.Array)
            {
                var refused = userContent.EnumerateArray().Count(IsPermissionDenial);
                if (refused > 0)
                    return Enumerable.Repeat(
                        new AiOutputChunk { Kind = AiChunkKind.Denied, Content = "" }, refused).ToList();
            }

            if (type == "content_block_delta" && root.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("text", out var deltaText))
            {
                return [new AiOutputChunk { Kind = AiChunkKind.Text, Content = deltaText.GetString() ?? "" }];
            }

            if (type == "result")
            {
                // Asked before the text is taken, because the text is there either way. A result line
                // carrying is_error is the tool saying this is what went wrong, not what it produced —
                // and read as an answer it becomes a plan, an implementation, or a review that nobody
                // wrote. The error path keeps it out of the answer unless there is nothing else.
                var failed = root.TryGetProperty("is_error", out var isError)
                             && isError.ValueKind == JsonValueKind.True;

                if (root.TryGetProperty("subtype", out var subtype)
                    && (subtype.GetString() ?? "").StartsWith("error", StringComparison.OrdinalIgnoreCase))
                    failed = true;

                var text = root.TryGetProperty("result", out var result) ? result.GetString() ?? "" : "";

                if (failed)
                    return [new AiOutputChunk
                    {
                        Kind = AiChunkKind.Error,
                        Content = text.Length > 0 ? text : "Claude returned an error.",
                    }];

                if (text.Length > 0)
                    return [new AiOutputChunk { Kind = AiChunkKind.Result, Content = text }];
            }

            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether one block of a user turn is the harness saying a tool call was not allowed.
    /// </summary>
    /// <remarks>
    /// <para>Matched on the words, because there is no field that says so: a denial arrives as an
    /// ordinary <c>tool_result</c> with <c>is_error</c> set, which is also what a failed command or a
    /// missing file looks like. The error flag is therefore the gate and the wording is the test, and
    /// both halves are needed — the wording alone would count an agent quoting the sentence.</para>
    /// <para>Two spellings, because the harness has used both. Getting this wrong is cheap in one
    /// direction and not the other: a missed denial only leaves the old, unhelpful message in place,
    /// while a false one would tell a user their permission mode is wrong when it is not. Hence the
    /// narrow phrases rather than the word "permission" on its own.</para>
    /// <para>There was a third test here — "permission" and "denied" anywhere in the same result —
    /// as a catch-all, and it was the exact failure the paragraph above forbids, written one line
    /// below it. Those two words appear together in <c>Permission denied (publickey)</c> from a git
    /// push, in <c>bash: ./x: Permission denied</c>, and in <c>EACCES: permission denied, open</c> from
    /// node — every one of them an ordinary <c>tool_result</c> with <c>is_error</c> set, and every one
    /// of them a real failure of the work rather than a refusal by the harness. It turned "your ssh key
    /// is not loaded" into "mTiles was not allowed to touch a file; change the permission mode", which
    /// sends the user to a setting that will not help. A new spelling by the harness costs a missed
    /// denial and the old message; a guess costs the user the diagnosis.</para>
    /// </remarks>
    private static bool IsPermissionDenial(JsonElement block)
    {
        if (!block.TryGetProperty("type", out var kind) || kind.GetString() != "tool_result") return false;
        if (!block.TryGetProperty("is_error", out var isError) || isError.ValueKind != JsonValueKind.True)
            return false;

        var text = block.TryGetProperty("content", out var content) ? Flatten(content) : "";

        return text.Contains("requested permissions", StringComparison.OrdinalIgnoreCase)
               || text.Contains("permission to use", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A tool_result's content, which is a string in the simple case and a list of blocks in
    /// the other. Both shapes are the harness's, not a choice this side gets to make.</summary>
    private static string Flatten(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString() ?? "",
        JsonValueKind.Array => string.Join(" ", content.EnumerateArray()
            .Select(b => b.ValueKind == JsonValueKind.Object && b.TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : "")),
        _ => "",
    };

    /// <summary>
    /// One tool call, in the fewest words that still say what is happening.
    /// </summary>
    /// <remarks>
    /// The tool's name alone is nearly useless — "Edit" tells you it is editing something — and the
    /// whole input is a JSON object nobody wants in a status strip. What is worth a line is the name
    /// and the one field that says which thing: the file, the command, the skill.
    /// </remarks>
    private static string Activity(JsonElement block)
    {
        if (!block.TryGetProperty("type", out var kind) || kind.GetString() != "tool_use") return "";
        if (!block.TryGetProperty("name", out var nameProp)) return "";

        var name = nameProp.GetString() ?? "";
        if (name.Length == 0) return "";

        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return name;

        foreach (var field in Subjects)
            if (input.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } subject)
                return $"{name} {Shorten(subject)}";

        return name;
    }

    /// <summary>The field that says what a tool call is about, in the order worth trying. A path comes
    /// before a pattern because "Grep src/Goal" reads better than "Grep TODO", and a command near the
    /// end because it is the one that is usually long; description is last, as the fallback for a tool
    /// that names nothing else.</summary>
    private static readonly string[] Subjects =
        ["file_path", "path", "notebook_path", "skill", "pattern", "url", "command", "description"];

    /// <summary>One line's worth, with the useful end kept: a path is told apart by its last segment,
    /// not its first, so this trims from the left and marks it.</summary>
    private static string Shorten(string subject)
    {
        var flat = subject.ReplaceLineEndings(" ").Trim();
        if (flat.Length <= 48) return flat;

        // By rune, not by char. A path with an emoji or anything else outside the basic plane is two
        // chars for one character, and cutting at 47 of them splits the pair and leaves a lone
        // surrogate on the status strip — the distinction CommandDisplay.Visible already spells out a
        // few files away.
        var runes = flat.EnumerateRunes().ToList();
        return "\u2026" + string.Concat(runes.Skip(Math.Max(0, runes.Count - 47)));
    }
}
