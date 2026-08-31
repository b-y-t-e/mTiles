using mTiles.Models;
using mTiles.Services.Agents;

namespace mTiles.Services.Providers;

/// <summary>
/// Everything a launch has settled about how this agent is going to run: the instance, the provider it
/// authenticates through, and the model it actually asks for.
/// </summary>
/// <remarks>
/// <para>One parameter rather than three, and it is what <c>IAiAgent.EnvFor</c> takes. The three
/// travel together everywhere and only ever grow together — stage 5 added the provider, and the model
/// is not the instance's field but the <em>resolved</em> one, which is a fourth thing again.</para>
/// <para><see cref="Model"/> is resolved rather than stored, because <c>AiModelChoice.FirstLoaded</c>
/// means "whatever this server has in memory when the session starts" and persisting the answer is the
/// same as not having the sentinel at all.</para>
/// </remarks>
/// <param name="Instance">The configured way of running the agent.</param>
/// <param name="Provider">What the instance authenticates through, or null for the agent's own
/// configuration — the case that needs no setting up and the one every seeded instance starts in.</param>
/// <param name="ProviderInstance">The key and the address, beside the provider that reads them.</param>
/// <param name="Model">The model to ask for, already resolved, or empty for the agent's own choice.</param>
/// <param name="SignIn">The CLI's own login this runs under, or null for the account it is already
/// signed into. Never set at the same time as <paramref name="Provider"/> — see
/// <see cref="AgentRuntime.For"/>.</param>
/// <param name="AutoCompactWindow">The auto-compact window <b>already reduced by the
/// <c>ModelContextWindow</c> rule</b> — 80% of the model's context, clamped to what the CLI accepts —"
/// resolved for this launch, or null when nobody said. The reduction happens once, in
/// <c>ModelContextWindow.ResolveAsync</c>, whose whole answer this is: an agent that applied
/// <c>Window</c> to it again would launch Claude Code at 64% of the context, and one that received the
/// raw context here would have to reduce it itself — two places for one rule. Resolved before
/// <see cref="IAiAgent.EnvFor"/> because that call is synchronous.</param>
/// <param name="MaxContextTokens">The context window <b>at 100%</b> — the other half of the same
/// resolution, <c>CLAUDE_CODE_MAX_CONTEXT_TOKENS</c>'s answer to what the CLI should <em>assume</em>
/// the model's window is, beside the 80% window it should <em>compact</em> at. Deliberately not
/// reduced: the margin is an opinion about when to compact, and this declares a fact. Null when the
/// provider did not say.</param>
public sealed record AgentRuntime(
    AiAgentInstance Instance,
    IAiProvider? Provider,
    AiProviderInstance? ProviderInstance,
    string Model,
    AiSignIn? SignIn = null,
    long? AutoCompactWindow = null,
    long? MaxContextTokens = null)
{
    /// <summary>The address for a wire format, or null when nothing configured here serves it.</summary>
    /// <remarks>Null is what tells an agent to leave the environment alone: an agent given no endpoint
    /// runs on its own configuration, which is exactly what an unconfigured instance means.</remarks>
    public Uri? EndpointFor(ApiFlavor flavor) =>
        Provider is not null && ProviderInstance is not null
            ? Provider.EndpointFor(flavor, ProviderInstance)
            : null;

    /// <summary>
    /// The model to actually ask an agent for: the resolved name, or empty when there is none.
    /// </summary>
    /// <remarks><b>An unresolved sentinel is not a model name.</b> <see cref="Model"/> is stored
    /// verbatim so that a caller which has not resolved <c>AiModelChoice.FirstLoaded</c> cannot have it
    /// silently turned into one — but every place that puts a model on a command line or into the
    /// environment wants the other answer, and passing <c>__first_loaded__</c> to a CLI is a request no
    /// provider can serve. Empty is the agent's own choice, which is what an instance naming no model
    /// means anyway.</remarks>
    public string RequestedModel => Model == AiModelChoice.FirstLoaded ? "" : Model;

    /// <summary>The key to authenticate with, or empty where there is none — a local server has no
    /// authentication at all.</summary>
    public string ApiKey => ProviderInstance?.ApiKey ?? "";

    /// <summary>
    /// Whether this run has an address that only the agent's own configuration can carry.
    /// </summary>
    /// <remarks>
    /// <para><b>Two cases, not one.</b> A local server is the obvious one — no catalogue names it. The
    /// other is a hosted provider the user has given an address of its own: a gateway, a proxy, a
    /// self-hosted mirror. <c>IsLocal</c> alone missed the second, so an opencode instance on
    /// "OpenRouter via my gateway" was offered by the chooser, launched with only
    /// <c>OPENROUTER_API_KEY</c>, and went to openrouter.ai — the typed address silently doing
    /// nothing.</para>
    /// <para>Read from the instance rather than the provider because that is where the override lives:
    /// an empty <c>BaseUrl</c> means "wherever the service is", which every agent can reach by
    /// name.</para>
    /// <para>The two cases are stated in <see cref="DeclaresEndpoint"/> and asked through it, because
    /// the Settings form asks the same question about the account it holds — an agent whose slot lives
    /// in a generated document gets its fast-model field exactly where this answers true — and one
    /// rule asked twice by two copies is a rule that drifts.</para>
    /// </remarks>
    public bool NeedsDeclaredEndpoint => DeclaresEndpoint(Provider, ProviderInstance);

    /// <summary>
    /// Whether this provider and its configured instance together declare an endpoint of their own.
    /// </summary>
    /// <remarks>Pure, and the one statement of the two cases <see cref="NeedsDeclaredEndpoint"/> and
    /// the fast-model field's visibility both ask about.</remarks>
    public static bool DeclaresEndpoint(IAiProvider? provider, AiProviderInstance? instance) =>
        provider is { IsLocal: true }
        || instance is { } configured && configured.BaseUrl.Trim().Length > 0;

    /// <summary>
    /// What an instance runs as when nothing has resolved a model for it.
    /// </summary>
    /// <remarks>The instance's own model verbatim, sentinel and all: a caller that has not resolved
    /// <c>AiModelChoice.FirstLoaded</c> must not have it silently turned into a name, and the launch
    /// path that can resolve it does so and passes the answer in.</remarks>
    /// <param name="agent">The agent that will really run, where the caller knows it. After a
    /// substitution that is not the one the instance names — and a sign-in belongs to one tool, so
    /// comparing against the instance's own id let a stand-in be pointed at another tool's credential
    /// directory and write its own into it.</param>
    public static AgentRuntime For(AppSettings settings, AiAgentInstance instance, string? model = null,
        IAiAgent? agent = null, long? autoCompactWindow = null, long? maxContextTokens = null)
    {
        // The two are one slot on screen and have to be one slot here: an instance carrying both would
        // point the CLI at a second subscription's directory and then hand it somebody else's API key
        // and address, so the run would go to the provider and be billed there while every row in the
        // application said it was running on the subscription. The sign-in wins because it is the more
        // specific answer — a provider is what an instance falls back to having.
        var runningAs = agent?.Id ?? instance.AgentId;
        var signIn = AiSignInStore.Find(settings, instance.SignInId);
        if (signIn is not null && signIn.AgentId != runningAs) signIn = null;

        var providerInstance = signIn is null
            ? AiProviderCatalog.FindInstance(settings, instance.ApiAccountId)
            : null;

        return new AgentRuntime(
            instance,
            providerInstance is null ? null : AiProviderCatalog.Find(providerInstance.ProviderId),
            providerInstance,
            model ?? instance.Model,
            signIn,
            autoCompactWindow,
            maxContextTokens);
    }
}
