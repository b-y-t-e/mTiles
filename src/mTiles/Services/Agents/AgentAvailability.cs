using mTiles.Models;
using mTiles.Services.Providers;

namespace mTiles.Services.Agents;

/// <summary>
/// Why a configured instance cannot be run as it stands — asked once, answered in one place.
/// </summary>
/// <remarks>
/// <para><b>One rule, three readers, and they had drifted.</b> The chooser hides an unavailable
/// instance, the Settings row explains why, and the launch refuses it — three questions with one
/// answer, which had become three answers: a sign-in that had been deleted made the instance vanish
/// from every chooser while its row said nothing, and an agent that cannot be pointed at a local server
/// was offered everywhere and then refused at startup. The division of labour this application actually
/// wants is *the chooser hides and the row explains*, and that only works while both are reading the
/// same sentence.</para>
/// <para>Answers a <b>sentence</b> rather than a flag, because the row needs the words and a flag can
/// always be derived from them — never the other way round. Says nothing about whether the CLI is
/// installed: that is a fact about the machine rather than about the configuration, the row already
/// shows it as its own chip, and an instance for a tool somebody is about to install is still a
/// perfectly good instance.</para>
/// </remarks>
public static class AgentAvailability
{
    /// <summary>The reason this instance cannot be run, or null when it can.</summary>
    /// <remarks>Looks the agent up from the instance, which is right for a chooser and for the row —
    /// both are asking about the configuration as written.</remarks>
    public static string? Problem(AiAgentInstance instance, AppSettings settings)
    {
        if (AiAgentCatalog.Find(instance.AgentId) is not { } agent)
            // Nothing about this agent is knowable - which flavors it speaks, whether it holds
            // sign-ins - so it cannot be run and nothing on it can be checked. Said whatever account it
            // names, and that "whatever" is the correction: narrowed to instances naming one, an
            // instance on the agent's own account was hidden by AiAgentCatalog.IsAvailable - which
            // fails at the lookup, before any account is considered - while its row answered nothing
            // and showed NOT INSTALLED beside it, which is a different claim. Hidden everywhere and
            // explained nowhere is the drift this class exists to end. A launch never reaches this: it
            // passes the agent it resolved.
            return $"This instance runs \"{instance.AgentId}\", which this version of mTiles does not "
                + "have. Update mTiles, or choose another agent for it.";

        return Problem(instance, settings, agent);
    }

    /// <summary>
    /// The same question about an agent the caller has already resolved.
    /// </summary>
    /// <remarks><b>A launch knows which agent is really running, and it is not always the one the
    /// instance names.</b> After a Velopack rollback <c>AgentTileKind.WithAgent</c> stands another
    /// agent in and says so through <c>AgentSubstitution</c> — a notice, over a tile that <em>is</em>
    /// running. Judging by the instance's own id there answered "this build does not have that agent",
    /// which became a <c>LaunchProblem</c> and a dead tile carrying two messages at once. Asked about
    /// the stand-in, the account it will actually use is checked instead, which is the useful
    /// question.</remarks>
    public static string? Problem(AiAgentInstance instance, AppSettings settings, IAiAgent agent) =>
        SignInProblem(instance, settings, agent)
        ?? ProviderProblem(instance, settings, agent)
        ?? ModelProblem(instance, agent);

    /// <summary>
    /// The model half, for the two agents that are told which service to use by the model's name.
    /// </summary>
    /// <remarks><para>Where a provider is configured, <c>AiAgent.WithProviderPrefix</c> writes the
    /// qualifier itself and this has nothing to say. Where one is <em>not</em> — a sign-in, or the
    /// CLI's own account — there is nothing to prefix with, so whatever is typed here reaches the
    /// command line verbatim: measured 2026-08-31, <c>opencode --model gpt-5</c> answers
    /// <c>ProviderModelNotFoundError</c> before a socket is opened.</para>
    /// <para>Said rather than guessed at. Inventing a provider for it would be this application
    /// choosing which service somebody's subscription runs against, and silently starting on the CLI's
    /// default is the failure every other sentence in this class exists to replace. The form's own
    /// model hint says the same thing before it can be got wrong.</para></remarks>
    private static string? ModelProblem(AiAgentInstance instance, IAiAgent agent)
    {
        if (!agent.NamesProviderInModel) return null;
        if (instance.ApiAccountId.Length > 0) return null;

        // Nothing typed is a different answer and a legitimate one: the CLI then picks its own model
        // within its own provider, which is exactly what "the agent's own account" asks for.
        if (instance.Model.Length == 0 || instance.Model.Contains('/')) return null;

        // Nor the sentinel, which nobody can write as provider/model — it is a question asked of a
        // provider at launch. AgentModelResolver has the sentence for it ("names no provider to ask"),
        // and this one runs first, so saying anything here would be the wrong advice given twice.
        if (instance.Model == AiModelChoice.FirstLoaded) return null;

        return $"{agent.DisplayName} is told which service to use by the model's name, so with no "
            + $"provider set here \"{instance.Model}\" would be refused before a request is made. "
            + "Write it as provider/model, or choose a provider account.";
    }

    /// <summary>
    /// Whether this agent can be run through this provider at all — what a chooser filters on.
    /// </summary>
    /// <remarks>Both halves, because compatibility alone is only one of them: speaking the same wire
    /// format is not the same as having somewhere to put an address, and offering the pairing on the
    /// first test alone put pi + LM Studio in the list and then refused the instance the moment it was
    /// saved. Shares its second condition with <see cref="ProviderProblem"/> so that what the chooser
    /// hides and what the row explains cannot part company.</remarks>
    public static bool CanPair(IAiAgent agent, IAiProvider provider,
        AiProviderInstance? configured = null) =>
        AiProviderCatalog.IsCompatible(agent, provider)
        && (agent.SupportsCustomEndpoint || !NeedsAnAddress(provider, configured));

    /// <summary>Whether reaching this particular instance means carrying an address.</summary>
    /// <remarks><b>The instance's own, which <c>IsLocal</c> alone cannot see.</b> A hosted provider the
    /// user has typed a gateway into needs exactly what a local server needs, so pi + "OpenRouter via
    /// my gateway" was offered by the chooser and the row it saved said UNAVAILABLE immediately.
    /// <see cref="ProviderProblem"/> asks the same question through <c>AgentRuntime</c>, which is what
    /// keeps what the chooser hides and what the row explains from parting company.</remarks>
    private static bool NeedsAnAddress(IAiProvider provider, AiProviderInstance? configured) =>
        provider.IsLocal || configured is { } instance && instance.BaseUrl.Trim().Length > 0;

    /// <summary>Whether this agent can reach the address this particular instance names.</summary>
    /// <remarks>The instance's own, which <see cref="CanPair"/> cannot see: a hosted provider given a
    /// gateway address needs the same thing a local server does, and an agent with nowhere to put one
    /// would run against the service's published address instead — quietly, since the pairing itself is
    /// perfectly legal.</remarks>
    private static bool CanReach(IAiAgent agent, AgentRuntime runtime) =>
        !runtime.NeedsDeclaredEndpoint || agent.SupportsCustomEndpoint;

    /// <summary>
    /// The account half: a login that has gone, or that belongs to another tool.
    /// </summary>
    /// <remarks>Whether it is <em>signed in</em> is deliberately not asked. That is somebody's to fix by
    /// logging in, and hiding the row they configured because they have not yet done so would take away
    /// the button that fixes it.</remarks>
    private static string? SignInProblem(AiAgentInstance instance, AppSettings settings, IAiAgent agent)
    {
        if (instance.SignInId.Length == 0) return null;

        // Both at once is not a state the chooser can produce - it writes one and clears the other -
        // but settings.json is hand-editable, and an older build wrote only the provider field.
        // AgentRuntime.For resolves it by dropping the provider, silently; the row would then say
        // "account: the subscription" while a configured provider quietly stopped being used, and the
        // next Save would delete it. Named here so the resolution is somebody's decision rather than a
        // side effect.
        if (instance.ApiAccountId.Length > 0)
            return "This instance names both a sign-in and a provider, which cannot both be true, so it "
                + "is not offered and a tile on it will not start. Choose one account for it.";

        if (AiSignInStore.Find(settings, instance.SignInId) is not { } signIn)
            return "The sign-in this instance runs as has been removed, so it would run on "
                + $"{agent.DisplayName}'s own account instead. Choose an account on this instance.";

        // Against the agent that will run, not the id the instance stores: after a substitution they
        // differ, and a login belongs to one tool.
        if (signIn.AgentId != agent.Id)
            return $"That sign-in is {Named(signIn.AgentId)}'s, not {agent.DisplayName}'s — a login is "
                + "one tool's. Choose an account this agent can use.";

        return agent.SupportsSignIns
            ? null
            : $"{agent.DisplayName} cannot be pointed at a second login, so this instance would run on "
              + "its own account. Choose another account.";
    }

    /// <summary>The provider half: gone, a wire format the agent does not speak, or an address it has
    /// nowhere to put.</summary>
    private static string? ProviderProblem(AiAgentInstance instance, AppSettings settings, IAiAgent agent)
    {
        if (instance.ApiAccountId.Length == 0) return null;

        if (AiProviderCatalog.FindInstance(settings, instance.ApiAccountId) is not { } configured
            || AiProviderCatalog.Find(configured.ProviderId) is not { } provider)
            return "The provider this instance authenticates through has been removed, so it would run "
                + $"on {agent.DisplayName}'s own configuration. Choose an account on this instance.";

        // Typed and unreadable, which AiProvider.BaseUrlFor answers with null rather than with this
        // machine's own address. Every consumer treats that null as "there is nowhere to call" and then
        // says nothing: ClaudeAgent.Configure sets neither ANTHROPIC_BASE_URL nor a token, so the tile
        // starts on the user's real subscription while the row names a gateway. That is the failure
        // LaunchProblem exists for, so it is named here - where the row explains it and the chooser
        // hides it - rather than left to be found in a log.
        if (configured.BaseUrl.Trim().Length > 0 && provider.BaseUrlFor(configured) is null)
            return $"\"{configured.BaseUrl.Trim()}\" is not an address this can read, so this instance "
                + $"would run on {agent.DisplayName}'s own configuration instead. Correct the address "
                + $"on {provider.DisplayName}, or clear it to use the published one.";

        if (!AiProviderCatalog.IsCompatible(agent, provider))
            return $"{agent.DisplayName} does not speak {provider.DisplayName}'s API, so this instance "
                + "is not offered on a tile. Point it at another provider.";

        // Speaking the same wire format is not the same as having somewhere to put an address: opencode
        // and pi both speak /v1/chat/completions and only one of them can be told where to send it.
        // Asked of the runtime rather than of IsLocal, because a hosted provider with an address typed
        // into it needs exactly the same thing - and that case used to pass here silently and then run
        // against the published address instead.
        // A provider that travels on the model names nothing when there is no model — and neither of
        // these CLIs fails for want of one, each falls back to its own. Said rather than worked around:
        // which model to use is a question only the user can answer.
        if (agent.NamesProviderInModel && instance.Model.Length == 0)
            return $"{agent.DisplayName} is told which service to use by the model's name, so with no "
                + $"model this instance would run on {agent.DisplayName}'s own provider rather than "
                + $"{provider.DisplayName}. Choose a model for it.";

        var runtime = AgentRuntime.For(settings, instance, agent: agent);
        if (!CanReach(agent, runtime))
            return provider.IsLocal
                ? $"{agent.DisplayName} cannot be pointed at a server of its own, so this instance "
                  + $"would run on its own provider rather than {provider.DisplayName}. Choose a hosted "
                  + "provider, or run this instance on another agent."
                : $"{agent.DisplayName} cannot be given an address of its own, so this instance would "
                  + $"run against {provider.DisplayName}'s published address rather than the one set on "
                  + "it. Clear that address, or run this instance on another agent.";

        return null;
    }

    /// <summary>An agent this build may not have, named as well as it can be.</summary>
    private static string Named(string agentId) =>
        AiAgentCatalog.Find(agentId)?.DisplayName ?? agentId;
}
