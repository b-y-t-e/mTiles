using mTiles.Models;

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
public sealed record AgentRuntime(
    AiAgentInstance Instance,
    IAiProvider? Provider,
    AiProviderInstance? ProviderInstance,
    string Model)
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
    /// What an instance runs as when nothing has resolved a model for it.
    /// </summary>
    /// <remarks>The instance's own model verbatim, sentinel and all: a caller that has not resolved
    /// <c>AiModelChoice.FirstLoaded</c> must not have it silently turned into a name, and the launch
    /// path that can resolve it does so and passes the answer in.</remarks>
    public static AgentRuntime For(AppSettings settings, AiAgentInstance instance, string? model = null)
    {
        var providerInstance = AiProviderCatalog.FindInstance(settings, instance.ProviderInstanceId);
        return new AgentRuntime(
            instance,
            providerInstance is null ? null : AiProviderCatalog.Find(providerInstance.ProviderId),
            providerInstance,
            model ?? instance.Model);
    }
}
