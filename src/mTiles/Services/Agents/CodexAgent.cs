using System.Diagnostics;
using mTiles.Models;
using mTiles.Services.Providers;

namespace mTiles.Services.Agents;

/// <summary>
/// OpenAI's codex CLI.
/// </summary>
/// <remarks>
/// <para><b>Its effort is a config key, not a flag</b>: <c>-c model_reasoning_effort=high</c>. That is
/// the measured fact that makes <see cref="IAiAgent.EffortArgs"/> return a whole argv fragment rather
/// than a flag name and a value — no "flag plus value" shape can express it — and it is why
/// <see cref="EffortFlagFor"/> is overridden: a refused <c>-c</c> key does not print
/// <c>unknown option '-c'</c>, so the token worth blaming is the key.</para>
/// <para><b>Two orthogonal permission axes</b>, <c>--sandbox</c> and <c>-a</c>, which is the other
/// reason a subset of a closed enum could not describe these agents. <c>-a on-request</c> is
/// <em>not</em> our auto — it is the asking mode — and codex's own help says of <c>never</c> that
/// "execution failures are immediately returned to the model", which is what a run with nobody
/// watching needs. The second axis exists only on the interactive commands: <c>codex exec</c> refuses
/// <c>-a</c> outright, so a headless run carries the sandbox alone (see <see cref="BehaviourArgs"/>).
/// </para>
/// <para><b>The session is learned after the fact</b> and is the one that can hang a launch chain:
/// <c>codex resume &lt;unknown-id&gt;</c> opens an interactive <em>picker</em>. So this never hands
/// <c>resume</c> an id it has not seen — an empty session id starts a plain <c>codex</c>, and
/// <see cref="SessionCapture"/> reads the id back out of the newest <c>rollout-*.jsonl</c>
/// afterwards.</para>
/// <para><b>Local models are codex's own business.</b> It reaches LM Studio and Ollama through
/// <c>--oss --local-provider</c> rather than through a base URL, which is why
/// <see cref="ConsumesApiFlavors"/> names only <see cref="ApiFlavor.OpenAiResponses"/>: pairing codex
/// with a local provider through configuration would be offered, and would not work.</para>
/// </remarks>
public sealed class CodexAgent : AiAgent
{
    /// <summary>The config key that carries effort, and the token to blame when it is refused.</summary>
    private const string EffortKey = "model_reasoning_effort";

    public override string Id => "codex";
    public override string DisplayName => "Codex";
    public override string BinaryName => "codex";

    /// <summary>
    /// <c>CODEX_HOME</c>, measured 2026-08-30 against codex-cli 0.141.0.
    /// </summary>
    /// <remarks>Pointed at an empty directory, <c>codex exec</c> fails with <c>401 Unauthorized</c>
    /// rather than falling back — so it reads none of <c>~/.codex/auth.json</c>, which is what makes a
    /// second ChatGPT subscription possible. It relocates <c>sessions/</c> with it, so the same warning
    /// applies as for Claude Code.</remarks>
    public override bool SupportsSignIns => true;

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string?> SignInEnv(string configDirectory) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CODEX_HOME"] = configDirectory,
        };

    /// <summary>
    /// Whether <c>auth.json</c> is there, and which of the two ways it authenticates.
    /// </summary>
    /// <remarks>
    /// <para>Measured 2026-08-30: the file's <c>auth_mode</c> is <c>chatgpt</c> for a subscription
    /// login and names the key otherwise, which is the one distinction worth a row here — a sign-in set
    /// up to spend a plan and one set up to spend an API balance are different bills, and nothing else
    /// on screen tells them apart.</para>
    /// <para><b>The account's address is deliberately not shown.</b> It exists, inside the
    /// <c>id_token</c>'s claims, and getting at it means parsing a bearer token this application has no
    /// business opening — for a line of text. "Signed in with ChatGPT" is the true answer that costs
    /// nothing; an email would be the same answer with a credential read to obtain it.</para>
    /// </remarks>
    public override SignInStatus ReadSignIn(string? configDirectory)
    {
        var home = configDirectory is { Length: > 0 } directory
            ? directory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

        var authFile = Path.Combine(home, "auth.json");
        if (!File.Exists(authFile)) return SignInStatus.NotSignedIn;

        // Present but unreadable still means signed in: the file is the evidence, and a format that has
        // moved must not turn a working login into a row offering to fix it.
        return ReadJsonString(authFile, "auth_mode") switch
        {
            "chatgpt" => new SignInStatus(true, "ChatGPT subscription"),
            { Length: > 0 } mode => new SignInStatus(true, mode),
            _ => SignInStatus.SignedInAnonymously,
        };
    }
    public override string? InstallUrl => "https://github.com/openai/codex";
    public override SessionStrategy SessionStrategy => SessionStrategy.CapturedAfterStart;

    public override InstallPlan? InstallPlan => new("npm",
        ["install", "-g", "@openai/codex"],
        "Installs the codex CLI globally through npm, which has to be on PATH already.");

    public override IReadOnlyList<ApiFlavor> ConsumesApiFlavors => [ApiFlavor.OpenAiResponses];

    /// <summary>
    /// The OpenAI-compatible pair, when the instance names a provider that serves that shape.
    /// </summary>
    /// <remarks>Nothing at all when it does not: an agent with no provider runs on its own
    /// configuration, which is what an unconfigured instance means and what a first run is in.</remarks>
    protected override void Configure(IDictionary<string, string?> environment, AgentRuntime runtime)
    {
        // The same clearing every agent that can hold a sign-in needs: an instance on a subscription
        // names no provider, so the early return below left an inherited OPENAI_API_KEY in place and
        // codex authenticated with it rather than with the login in CODEX_HOME.
        //
        // What it then sets is deliberately still OPENAI_API_KEY whatever the provider is: codex is
        // pointed at a service by *address* (OPENAI_BASE_URL below), so the key variable is the
        // transport rather than the choice of provider — the opposite of opencode and pi, which is why
        // those two were taken off this pattern and this one was not - hence `setKey: false`, which
        // clears without also exporting a variable nothing here reads.
        ApplyProviderKey(environment, runtime, setKey: false);

        // The address, for the reason ClaudeAgent removes its pair: OPENAI_BASE_URL is nobody's
        // KeyEnvironmentVariable, so nothing above clears it, and an instance on a subscription takes
        // the branch below not at all - leaving a globally exported address pointing this login at
        // somebody else's gateway. The key half is covered already: OPENAI_API_KEY *is* a provider's
        // variable, so ApplyProviderKey removes it.
        if (runtime.SignIn is not null) environment["OPENAI_BASE_URL"] = null;

        if (runtime.EndpointFor(ApiFlavor.OpenAiResponses) is not { } endpoint)
            return;

        environment["OPENAI_BASE_URL"] = endpoint.ToString();
        environment["OPENAI_API_KEY"] = runtime.ApiKey;
    }

    /// <summary>
    /// Read-only, get-on-with-it, and nothing-is-asked — the three points on codex's two axes that mean
    /// something canonical.
    /// </summary>
    /// <remarks>Its asking modes are deliberately absent from the headless list for the reason every
    /// agent's are: there is nobody to ask, so a refusal becomes a tool call that quietly fails.
    /// </remarks>
    public override IReadOnlyList<AiBehaviour> SupportedBehaviours(AiAgentInstance instance, AiUsage usage) =>
        !usage.IsHeadless
            ? [AiBehaviour.Plan, AiBehaviour.Ask, AiBehaviour.Auto, AiBehaviour.BypassPermissions,
               AiBehaviour.ToolDefault]
            : !usage.MayOnlyRead
                ? [AiBehaviour.Auto, AiBehaviour.BypassPermissions, AiBehaviour.ToolDefault]
                : [AiBehaviour.Plan, AiBehaviour.ToolDefault];

    /// <summary>Measured: <c>minimal|low|medium|high</c>. The canonical scale's top two round down to
    /// <c>high</c>, which is what <see cref="AiEfforts.RoundToNearest"/> is for; <c>minimal</c> is
    /// codex's alone and is not offered, as pi's <c>off</c> is not.</summary>
    public override IReadOnlyList<AiEffort> SupportedEfforts(AiAgentInstance instance, AiUsage usage) =>
        Efforts;

    /// <summary>Codex's scale, held once so that <see cref="EffortArgs"/> can round against it without
    /// inventing an instance to ask about.</summary>
    private static readonly IReadOnlyList<AiEffort> Efforts =
        [AiEffort.Low, AiEffort.Medium, AiEffort.High, AiEffort.ToolDefault];

    /// <summary>
    /// <c>-c model_reasoning_effort=&lt;level&gt;</c>, for a level codex has.
    /// </summary>
    /// <remarks>The rounding happens here rather than at the call site, so a caller cannot pass
    /// <c>max</c> and have it silently reach a config key that would be rejected by a non-reasoning
    /// model.</remarks>
    public override IReadOnlyList<string> EffortArgs(AiEffort effort, AiUsage usage)
    {
        var level = AiEfforts.RoundToNearest(effort, Efforts);
        return AiEfforts.Name(level) is { } name ? ["-c", $"{EffortKey}={name}"] : [];
    }

    /// <summary>Measured (2026-08-30, codex-cli 0.141.0): <c>--model</c>, accepted by the interactive
    /// command, by <c>resume</c> and by <c>exec</c> alike.</summary>
    /// <remarks>The long spelling rather than <c>-m</c>, because this fragment is also what a user
    /// reads in the tile's scrollback. Not <c>-c model=…</c>: a config override would be a second way
    /// of saying the same thing, and the flag is the one codex documents.</remarks>
    public override IReadOnlyList<string> ModelArgs(string model, AiUsage usage) =>
        model.Length > 0 ? ["--model", model] : [];

    /// <inheritdoc />
    /// <remarks><c>-c</c> is the same token for every config key codex has, so blaming it would name a
    /// flag rather than a setting. The key is what a user can act on.</remarks>
    public override string? EffortFlagFor(AiEffort effort, AiUsage usage) =>
        EffortArgs(effort, usage).Count > 0 ? EffortKey : null;

    /// <summary>
    /// The sandbox and the approval policy together, because in codex neither means anything alone.
    /// </summary>
    /// <remarks>A phase that <c>AiUsage.MayOnlyRead</c> holds to reading is put in <c>read-only</c>
    /// whatever it asked for — decision 9, and the thing that stops a review agent editing the
    /// worktree a goal is being judged in. A review asked to establish the build and the tests is not
    /// one of them: <c>read-only</c> denies the writes a build makes into <c>obj/</c> and <c>bin/</c>,
    /// so the check the tile completes on could not be carried out at all.
    /// <para><b><c>codex exec</c> has no <c>-a</c>.</b> Measured (codex-cli 0.141.0):
    /// <c>--ask-for-approval</c> is on <c>codex</c> and <c>codex resume</c> and nowhere else, and
    /// <c>codex exec --sandbox workspace-write -a never</c> answers
    /// <c>error: unexpected argument '-a' found</c> without running anything — which on the default
    /// <c>Auto</c> was every implementation phase and every health-checking review. The axis is
    /// dropped for a headless run rather than translated: <c>exec</c> asks nobody in the first place,
    /// so the sandbox alone already says everything that mode meant.</para></remarks>
    public override IReadOnlyList<string> BehaviourArgs(AiBehaviour behaviour, AiUsage usage)
    {
        if (usage.MayOnlyRead)
            return ["--sandbox", "read-only"];

        return behaviour switch
        {
            AiBehaviour.Plan => ["--sandbox", "read-only"],
            AiBehaviour.Ask => WorkspaceWrite(usage, "on-request"),
            AiBehaviour.Auto => WorkspaceWrite(usage, "never"),
            AiBehaviour.BypassPermissions => ["--dangerously-bypass-approvals-and-sandbox"],
            _ => [],
        };
    }

    /// <summary>The writing sandbox, with the approval axis only where codex has one.</summary>
    private static IReadOnlyList<string> WorkspaceWrite(AiUsage usage, string approval) =>
        usage.IsHeadless
            ? ["--sandbox", "workspace-write"]
            : ["--sandbox", "workspace-write", "-a", approval];

    /// <summary>
    /// Resumes a session this application has actually seen, and starts a fresh one when it has not.
    /// </summary>
    /// <remarks><b>Never <c>resume</c> with an id we invented.</b> An unknown id makes codex open its
    /// session picker, and a picker in a launch chain is a tile that waits for a keystroke nobody knows
    /// it wants. The fallback is a plain <c>codex</c> for the same reason.</remarks>
    protected override LaunchScripts Resume(string sessionId) =>
        LaunchScripts.FromProfile(
            sessionId is { Length: > 0 } ? $"codex resume {sessionId}" : "codex", "codex");

    /// <summary>Read from the session codex itself started, so there is nothing to read until it has.
    /// </summary>
    public override bool CapturesWhileRunning => true;

    /// <summary>
    /// Reads back the id codex gave the session it has just started, from the rollout file it wrote.
    /// </summary>
    /// <remarks>
    /// <para>Nothing is run here. codex is already running in the tile — this only looks at what it
    /// left on disk, so the capture costs no model call at all.</para>
    /// <para>This is a <em>guess</em>, and all three of the filters it passes are what keep it from
    /// being somebody else's conversation: started no earlier than this tile, recording this tile's own
    /// working directory, and still free to be taken — which it takes in the same step, so two tiles
    /// capturing at once cannot both come away with it. Two codex tiles in one workspace is the ordinary
    /// case in this application, and the first two filters alone cannot tell them apart.
    /// </para>
    /// </remarks>
    public override Task<string?> CaptureSessionAsync(AiAgentInstance instance,
        SessionCaptureRequest request, CancellationToken ct) =>
        Task.FromResult(SessionCapture.NewestSessionId(SessionsRoot, request.StartedAt,
            request.WorkingDirectory,
            sessionId => CapturedSessions.TryClaim(sessionId, request.TileId)));

    /// <summary>Where codex keeps its rollout files: <c>~/.codex/sessions</c>, then a directory per
    /// year, month and day.</summary>
    private static string SessionsRoot => SessionsRootFor(null);

    /// <summary>The sessions directory of one codex home.</summary>
    /// <remarks><c>CODEX_HOME</c> takes <c>sessions/</c> with it, which is the same fact that makes a
    /// sign-in a different set of conversations — and here it is what makes it a different set of
    /// limits.</remarks>
    private static string SessionsRootFor(string? configDirectory) =>
        Path.Combine(
            configDirectory is { Length: > 0 } directory
                ? directory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".codex"),
            "sessions");

    /// <summary>
    /// The two windows codex last reported, read out of its own transcript.
    /// </summary>
    /// <remarks><para>Answered for a sign-in always, and for the default account only where codex has
    /// actually been used here: a machine with no <c>~/.codex/sessions</c> has no such account, and a
    /// card saying so would be an explanation for something the user never configured. A sign-in row
    /// they did make is the opposite case and gets its sentence.</para>
    /// <para><b>The work goes on the thread pool, because it is the only source here with no network
    /// call to yield at.</b> It walks the whole of <c>sessions/</c> and reads a rollout file that runs
    /// into megabytes; left synchronous it ran to completion before the first real <c>await</c> of the
    /// refresh, which is on the UI thread whenever a tile is built or the Refresh button is
    /// pressed.</para></remarks>
    public override Task<AiUsageReport?> UsageAsync(AiSignIn? signIn, CancellationToken ct = default)
    {
        var root = SessionsRootFor(signIn is null ? null : AiSignInStore.DirectoryFor(signIn));
        var sourceId = UsageSourceId(signIn);
        var name = UsageSourceName(signIn);

        var accountKey = UsageAccountKey(root);

        return Task.Run<AiUsageReport?>(() => signIn is null && !Directory.Exists(root)
            ? null
            : CodexUsageReader.Read(sourceId, name, root, DateTimeOffset.Now, accountKey), ct);
    }

    /// <inheritdoc />
    /// <remarks>The sessions directory, canonicalised — the same key <see cref="UsageAsync"/> files its
    /// report under. codex has no account id to read anywhere, so what this can say is that two rows
    /// are reading one directory, which is the case <c>CODEX_HOME</c> produces.</remarks>
    public override string? UsageAccountKeyFor(AiSignIn? signIn) =>
        UsageAccountKey(SessionsRootFor(signIn is null ? null : AiSignInStore.DirectoryFor(signIn)));

    public override void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiUsage usage, AiBehaviour behaviour = AiBehaviour.Auto,
        AiEffort effort = AiEffort.High, string model = "")
    {
        psi.ArgumentList.Add("exec");

        foreach (var argument in BehaviourArgs(behaviour, usage)
                     .Concat(EffortArgs(effort, usage))
                     .Concat(ModelArgs(model, usage)))
            psi.ArgumentList.Add(argument);

        psi.ArgumentList.Add(prompt);
    }
}
