using System.Diagnostics;
using mTiles.Models;
using mTiles.Services.Providers;

namespace mTiles.Services.Agents;

/// <summary>
/// The pi coding agent.
/// </summary>
/// <remarks>
/// <para><b>pi has no permission gate at all</b>, and that is measured rather than assumed:
/// <c>--approve</c> is about trusting project-local files, not about tool calls. A pi run is always
/// unrestricted, so this agent declares <see cref="AiBehaviour.BypassPermissions"/> and nothing else —
/// offering "auto" here would be a lie about what is going to happen to somebody's repository, and it
/// is a lie the rounding rules would have no way to catch.</para>
/// <para><b>Sessions are the easy case</b>: <c>--session-id &lt;id&gt;</c> creates the session if it
/// is missing, so the tile's own id is the whole of the bookkeeping.</para>
/// </remarks>
public sealed class PiAgent : AiAgent
{
    public override string Id => "pi";
    public override string DisplayName => "Pi Agent";
    public override string BinaryName => "pi";

    /// <summary>
    /// <c>PI_CODING_AGENT_DIR</c>, measured 2026-08-31 against pi 0.84.3.
    /// </summary>
    /// <remarks><b>This was first recorded as "pi has no such variable", and that was wrong.</b> It is
    /// listed in <c>pi --help</c> as "Config directory (default: ~/.pi/agent)", and it really does move
    /// the credentials: with <c>OPENROUTER_API_KEY</c> removed from the environment,
    /// <c>pi auth check --provider openrouter</c> answers <c>not_ready</c> against a fresh directory and
    /// <c>ready</c> against the default one. The first reading was taken by grepping <c>--help</c> for
    /// the word "config" near the top and stopping too early — which is why the note in
    /// <c>AiSignInTests</c> now says what was actually run.
    /// <para>Its <c>auth.json</c> names the providers it holds and no address, so the row says whether
    /// there is a login and not whose — the same answer codex's file allows.</para></remarks>
    public override bool SupportsSignIns => true;

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string?> SignInEnv(string configDirectory) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PI_CODING_AGENT_DIR"] = configDirectory,
        };

    /// <summary>Whether <c>auth.json</c> is there.</summary>
    /// <remarks>Deliberately not "is pi ready": readiness can come from an API key in the environment,
    /// which is not this sign-in's login and would report every empty directory as signed in.</remarks>
    public override SignInStatus ReadSignIn(string? configDirectory)
    {
        var home = configDirectory is { Length: > 0 } directory
            ? directory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".pi", "agent");

        return File.Exists(Path.Combine(home, "auth.json"))
            ? SignInStatus.SignedInAnonymously
            : SignInStatus.NotSignedIn;
    }
    public override string? InstallUrl => "https://github.com/mariozechner/pi-coding-agent";
    public override SessionStrategy SessionStrategy => SessionStrategy.Fixed;

    public override InstallPlan? InstallPlan => new("npm",
        ["install", "-g", "@mariozechner/pi"],
        "Installs pi globally through npm, which has to be on PATH already.");

    /// <summary>Bring-your-own-key against an OpenAI-shaped chat endpoint.</summary>
    public override IReadOnlyList<ApiFlavor> ConsumesApiFlavors => [ApiFlavor.OpenAiChatCompletions];

    /// <summary>
    /// The chosen service's own key, under the name pi reads it from.
    /// </summary>
    /// <remarks>Same correction as opencode's, and for the same reason: pi has no generic base-URL
    /// variable. Its <c>--help</c> lists a key variable per service — <c>OPENROUTER_API_KEY</c>,
    /// <c>ANTHROPIC_API_KEY</c>, <c>ZAI_API_KEY</c> and a dozen more — and the only address it takes is
    /// Azure's, which is that one service's own. <c>OPENAI_API_KEY</c> here means the real OpenAI.
    /// </remarks>
    protected override void Configure(IDictionary<string, string?> environment, AgentRuntime runtime) =>
        ApplyProviderKey(environment, runtime);

    /// <inheritdoc />
    /// <remarks><c>pi --help</c>: <c>--model</c> "supports provider/id". The separate
    /// <c>--provider</c> flag says the same thing twice, and its default is <c>google</c> — which is
    /// what an unprefixed model was quietly running on.</remarks>
    public override string QualifiedModel(AgentRuntime runtime) => WithProviderPrefix(runtime);

    /// <inheritdoc />
    /// <remarks>The prefix is the only place this CLI hears which service to use.</remarks>
    public override bool NamesProviderInModel => true;

    /// <summary>
    /// No. pi has a key variable per named service and nowhere to put an address.
    /// </summary>
    /// <remarks>Measured 2026-08-31 against pi 0.84.3: its <c>--help</c> lists twenty key variables and
    /// exactly one base URL, Azure's, which belongs to that service rather than being a generic
    /// endpoint. <c>pi --list-models</c> with <c>OPENAI_BASE_URL</c> set shows no local provider. So an
    /// instance of pi on a local server is a configuration that cannot work through pi alone.
    /// <para><b>A third-party extension does provide a route</b> — <c>pi-localllm-provider</c>, which
    /// registers <c>localllm-&lt;slug&gt;</c> providers from a <c>localllm</c> block in
    /// <c>settings.json</c>. It is not assumed here because it may not be installed, and because it
    /// reads a hardcoded path in the home directory rather than <c>PI_CODING_AGENT_DIR</c>, so its
    /// servers are one global list rather than something an instance can own. What it would take to
    /// allow the pairing when it <em>is</em> there is written down in <c>docs/ROADMAP.md</c>.</para>
    /// </remarks>
    public override bool SupportsCustomEndpoint => false;

    /// <summary>
    /// Bypass, and the option of passing nothing — which come to the same thing here, and are both
    /// listed so that the difference between "we chose this" and "we said nothing" stays visible.
    /// </summary>
    /// <remarks>Anything else asked for rounds <em>down</em> to <see cref="AiBehaviour.ToolDefault"/>
    /// rather than up to bypass, which is the correct direction and also has no effect: pi will do as
    /// it likes either way. What it buys is that a Goal tile showing "auto" on a pi run is impossible,
    /// so nobody reads a restriction into a run that has none.</remarks>
    public override IReadOnlyList<AiBehaviour> SupportedBehaviours(AiAgentInstance instance, AiUsage usage) =>
        [AiBehaviour.BypassPermissions, AiBehaviour.ToolDefault];

    /// <summary>Measured against <c>pi --help</c>: <c>--thinking off|minimal|low|medium|high|xhigh|max</c>.
    /// Every level the canonical scale names exists there under the same word.
    /// <para><c>off</c> and <c>minimal</c> are pi's alone and are deliberately not offered: the scale
    /// has no level below <c>low</c>, and adding two for one agent would make every other agent's table
    /// answer a question it cannot.</para></summary>
    public override IReadOnlyList<AiEffort> SupportedEfforts(AiAgentInstance instance, AiUsage usage) =>
        FullEffortScale;

    public override IReadOnlyList<string> EffortArgs(AiEffort effort, AiUsage usage) =>
        AiEfforts.Name(effort) is { } level ? ["--thinking", level] : [];

    /// <summary>Nothing, whatever is asked. There is no flag to pass — see the type's remarks.</summary>
    public override IReadOnlyList<string> BehaviourArgs(AiBehaviour behaviour, AiUsage usage) => [];

    /// <summary>Measured (2026-08-30, pi 0.84.3): <c>--model &lt;pattern&gt;</c>, which also accepts a
    /// <c>provider/id</c> form — so the instance's model name is passed through as it was written
    /// rather than being split into a provider and an id here.</summary>
    public override IReadOnlyList<string> ModelArgs(string model, AiUsage usage) =>
        model.Length > 0 ? ["--model", model] : [];

    protected override LaunchScripts Resume(string sessionId) =>
        LaunchScripts.FromProfile($"pi --session-id {sessionId}", "pi");

    public override void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiUsage usage, AiBehaviour behaviour = AiBehaviour.Auto,
        AiEffort effort = AiEffort.High, string model = "")
    {
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add("text");

        foreach (var argument in EffortArgs(effort, usage).Concat(ModelArgs(model, usage)))
            psi.ArgumentList.Add(argument);
    }
}
