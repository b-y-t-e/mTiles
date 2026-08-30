using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Providers;

namespace mTiles.ViewModels;

/// <summary>
/// One configured way of running an agent, as a row in Settings.
/// </summary>
/// <remarks>
/// <para>A row rather than the model bound directly, because two of the four things it shows are not on
/// the instance at all: whether the agent is installed on this machine, and what installing it would
/// run. Both are the agent's, and an instance that names an agent this build does not have has neither.
/// </para>
/// <para><b>An uninstalled agent is shown, not hidden.</b> The chooser on a tile hides what will not
/// work; this page is where somebody comes to make it work, and a list that quietly omitted the agent
/// they were looking for would say nothing at all about why.</para>
/// </remarks>
public sealed partial class AiAgentInstanceViewModel : ObservableObject
{
    public AiAgentInstanceViewModel(AiAgentInstance instance, AppSettings settings)
    {
        Instance = instance;
        Agent = AiAgentCatalog.Find(instance.AgentId);
        _isInstalled = Agent is not null && AiAgentCatalog.Locate(Agent) is not null;
        _providerName = ProviderNameOf(instance, settings);
        IncompatibleNote = IncompatibilityOf(Agent, instance, settings);
    }

    public AiAgentInstance Instance { get; }

    /// <summary>The CLI behind it, or null for an instance naming an agent this build does not have.
    /// </summary>
    public IAiAgent? Agent { get; }

    public string Name => Instance.Name.Length > 0 ? Instance.Name : AgentName;

    public string AgentName => Agent?.DisplayName ?? Instance.AgentId;

    [ObservableProperty]
    private bool _isInstalled;

    /// <summary>What installing it would run, or null where nothing can honestly be offered.</summary>
    public InstallPlan? InstallPlan => Agent?.InstallPlan;

    public bool CanBeInstalled => !IsInstalled && InstallPlan is not null;

    /// <summary>Why this agent cannot be run through the provider it names, or empty where it can.
    /// </summary>
    /// <remarks>The row is the only place that can say it. An instance whose pairing does not work is
    /// unavailable everywhere else — missing from the Agent tile's chooser and from the Goal tile's
    /// list — and a row that showed nothing left the user with a configuration that had silently
    /// stopped being offered.</remarks>
    public string IncompatibleNote { get; }

    public bool IsIncompatible => IncompatibleNote.Length > 0;

    private static string IncompatibilityOf(IAiAgent? agent, AiAgentInstance instance,
        AppSettings settings)
    {
        if (agent is null
            || AiProviderCatalog.FindInstance(settings, instance.ProviderInstanceId) is not { } configured
            || AiProviderCatalog.Find(configured.ProviderId) is not { } provider
            || AiProviderCatalog.IsCompatible(agent, provider))
            return "";

        return $"{agent.DisplayName} does not speak {provider.DisplayName}'s API, so this instance is "
            + "not offered on a tile. Point it at another provider.";
    }

    [ObservableProperty]
    private string _providerName;

    /// <summary>The line under the name: where it authenticates, what model it asks for, how it runs.
    /// </summary>
    /// <remarks>One line rather than a column each, which is the panel's own rule: a value present on
    /// every row is metadata, and four short columns in a dialog this narrow leave nothing to line
    /// up.</remarks>
    public string Summary
    {
        get
        {
            var model = Instance.Model.Length == 0
                ? "the agent's own model"
                : Instance.Model == AiModelChoice.FirstLoaded
                    ? "first loaded model"
                    : Instance.Model;

            return $"{ProviderName} · {model} · {AiBehaviours.Label(Instance.DefaultBehaviour)} · "
                + AiEfforts.Label(Instance.DefaultEffort);
        }
    }

    private static string ProviderNameOf(AiAgentInstance instance, AppSettings settings) =>
        AiProviderCatalog.FindInstance(settings, instance.ProviderInstanceId) is { } configured
            ? configured.Name.Length > 0
                ? configured.Name
                : AiProviderCatalog.Find(configured.ProviderId)?.DisplayName ?? configured.ProviderId
            // Empty is a configuration, not a gap: it is what every seeded instance starts in, and it
            // is the one case that needs nothing set up at all.
            : "the agent's own account";
}

/// <summary>
/// One configured way of reaching a provider, as a row in Settings.
/// </summary>
public sealed partial class AiProviderInstanceViewModel : ObservableObject
{
    public AiProviderInstanceViewModel(AiProviderInstance instance)
    {
        Instance = instance;
        Provider = AiProviderCatalog.Find(instance.ProviderId);
    }

    public AiProviderInstance Instance { get; }

    public IAiProvider? Provider { get; }

    public string Name => Instance.Name.Length > 0
        ? Instance.Name
        : Provider?.DisplayName ?? Instance.ProviderId;

    /// <summary>The address, and whether there is a key — never the key.</summary>
    public string Summary
    {
        get
        {
            var address = Instance.BaseUrl.Length > 0
                ? Instance.BaseUrl
                : Provider?.DefaultBaseUrl?.ToString() ?? "no address";

            // "No key needed" rather than a blank: on a local server it is the whole story, and it is
            // worth saying that anybody who can reach it can use it.
            var key = Provider is { NeedsApiKey: false }
                ? "no key needed — open to anyone who can reach it"
                : Instance.ApiKey.Length > 0 ? "key set" : "no key";

            return $"{address} · {key}";
        }
    }
}
