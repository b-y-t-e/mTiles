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
        _accountName = AccountNameOf(instance, settings);
        UnavailableNote = UnavailabilityOf(instance, settings);
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

    /// <summary>Where the tool comes from, for the row's link — null for an agent this build does not
    /// have and for the one without a page.</summary>
    public string? InstallUrl => Agent?.InstallUrl;

    public bool HasInstallUrl => InstallUrl is not null;

    /// <summary>Why this instance cannot be run as configured, or empty where it can.</summary>
    /// <remarks><para>The row is the only place that can say it. An instance that cannot be run as
    /// configured is unavailable everywhere else — missing from the Agent tile's chooser and from the
    /// Goal tile's list — and a row that showed nothing left the user with a configuration that had
    /// silently stopped being offered.</para>
    /// <para><b>Named for what it counts, which is no longer only incompatibility.</b> Since it reads
    /// <c>AgentAvailability</c> it also carries "the sign-in has been removed", "that sign-in belongs
    /// to another tool" and "this agent cannot be pointed at a server of its own" — an INCOMPATIBLE
    /// chip over a sentence about a deleted login is the wrong word, and the old name was a trap for
    /// the next reader.</para></remarks>
    public string UnavailableNote { get; }

    public bool IsUnavailable => UnavailableNote.Length > 0;

    /// <remarks>Asked even for an agent this build does not have, which is the case
    /// <see cref="AgentAvailability"/> wrote its longest sentence for: the instance is unavailable
    /// everywhere (<c>AiAgentCatalog.IsAvailable</c> fails at the lookup), so answering nothing here
    /// recreated exactly the drift that class exists to end - hidden everywhere, explained nowhere -
    /// with a NOT INSTALLED chip beside it saying something else again.</remarks>
    private static string UnavailabilityOf(AiAgentInstance instance, AppSettings settings) =>
        AgentAvailability.Problem(instance, settings) ?? "";

    [ObservableProperty]
    private string _accountName;

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

            return $"{AccountName} · {model} · {AiBehaviours.Label(Instance.DefaultBehaviour)} · "
                + AiEfforts.Label(Instance.DefaultEffort);
        }
    }

    /// <summary>Which account the row runs as, in the words the chooser used.</summary>
    /// <remarks><b>It names the subscription rather than saying "the agent's own account".</b> That
    /// sentence was true while one CLI meant one login; with three of them configured it describes all
    /// three identically, and the row is the only place the user can tell which is which before
    /// launching a tile on it.</remarks>
    private static string AccountNameOf(AiAgentInstance instance, AppSettings settings)
    {
        // The same test AgentRuntime.For applies before it drops one: a login belongs to one tool, and
        // naming another agent's here made the summary line claim an account the run would not use
        // while the chip beside it said something was wrong.
        if (AiSignInStore.Find(settings, instance.SignInId) is { } signIn
            && signIn.AgentId == instance.AgentId)
            return signIn.Name.Length > 0 ? signIn.Name : "an unnamed sign-in";

        return AiProviderCatalog.FindInstance(settings, instance.ApiAccountId) is { } configured
            ? configured.Name.Length > 0
                ? configured.Name
                : AiProviderCatalog.Find(configured.ProviderId)?.DisplayName ?? configured.ProviderId
            // Empty is a configuration, not a gap: it is what every seeded instance starts in, and it
            // is the one case that needs nothing set up at all.
            : "the agent's own account";
    }
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

/// <summary>
/// One of a CLI's own logins, as a row on the AI page.
/// </summary>
/// <remarks><b>The status is read every time the page loads, never stored.</b> A remembered "signed in"
/// keeps saying so after the user has logged out in a terminal, and the first symptom of that is a tile
/// that starts and then cannot talk to anything.</remarks>
public sealed partial class AiSignInViewModel : ObservableObject
{
    public AiSignInViewModel(AiSignIn signIn)
    {
        SignIn = signIn;
        Agent = AiAgentCatalog.Find(signIn.AgentId);
        Status = Agent?.ReadSignIn(AiSignInStore.DirectoryFor(signIn)) ?? SignInStatus.NotSignedIn;
    }

    public AiSignIn SignIn { get; }

    public IAiAgent? Agent { get; }

    public SignInStatus Status { get; }

    public string Name => SignIn.Name.Length > 0 ? SignIn.Name : "Unnamed";

    public bool IsSignedIn => Status.SignedIn;

    /// <summary>Whether Sign in can do anything — it needs an agent to open a tile for.</summary>
    /// <remarks>A row whose agent this build does not have showed the button and did nothing when it
    /// was pressed; the row is still listed so it can be renamed or removed.</remarks>
    public bool CanSignIn => !Status.SignedIn && Agent is not null;

    /// <summary>The line under the name: which CLI, and who it is logged in as.</summary>
    /// <remarks>"Not signed in" is a state with a way out rather than an error, which is why the row
    /// carries the button that fixes it — the panel's own rule that a row which can be acted on carries
    /// the action.</remarks>
    public string Summary
    {
        get
        {
            var agent = Agent?.DisplayName ?? SignIn.AgentId;
            if (!Status.SignedIn) return $"{agent} · not signed in";

            return Status.Detail.Length > 0
                ? $"{agent} · {Status.Detail}"
                // Signed in, with a credential file that names nobody. Saying so beats inventing a
                // label for an account this cannot actually read.
                : $"{agent} · signed in";
        }
    }
}
