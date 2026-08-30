using System.Diagnostics;
using mTiles.Models;
using mTiles.Services.Agents;

namespace mTiles.Services.Providers;

/// <summary>
/// Which model an instance actually runs on, or the sentence that says why it cannot run at all.
/// </summary>
/// <remarks>
/// <para>One rule, asked by both places that start an agent — the agent tile and the Goal tile's run —
/// because it was written in the tile and the goal did not have it: a goal on an instance asking for
/// <c>__first_loaded__</c> launched with no model at all while the environment still pointed at the
/// local server, which is the silent substitution the sentinel exists to prevent. The same is true of a
/// model named on an agent that cannot be told one: the tile refused and said so, and the goal ignored
/// it.</para>
/// <para>Pure of any view model, and it answers rather than throws: both callers turn the problem into
/// a sentence on screen, and neither has anywhere to put an exception.</para>
/// </remarks>
public static class AgentModelResolver
{
    /// <summary>
    /// The model to ask for — empty for the agent's own choice — or a problem that refuses the launch.
    /// </summary>
    public static async Task<(string? Model, string? Problem)> ResolveAsync(AppSettings settings,
        IAiAgent agent, AiAgentInstance instance, CancellationToken ct = default)
    {
        if (ProviderProblem(settings, agent, instance) is { } unreachable)
            return (null, unreachable);

        if (instance.Model.Length > 0 && !agent.AcceptsModel)
            return (null, $"{agent.DisplayName} cannot be told which model to use, so "
                + $"\"{instance.Model}\" would be ignored. Clear the model on this instance, or run "
                + "it on an agent that takes one.");

        if (instance.Model != AiModelChoice.FirstLoaded)
            return (instance.Model, null);

        var runtime = AgentRuntime.For(settings, instance);
        if (runtime.Provider is not { } provider || runtime.ProviderInstance is not { } configured)
            return (null, "This instance runs on the first model loaded, but it names no provider "
                + "to ask. Choose a provider, or name a model.");

        try
        {
            return await AiModelChoice.ResolveAsync(provider, configured, instance.Model, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The promise above is that this answers, and both callers rely on it: a throw from here
            // travels past a launcher that swallows it and starts the session with no model at all,
            // against an address that was chosen for the model the user asked for. A sentence refuses
            // the launch; an exception silently loses the sentinel it was asked to honour.
            Trace.TraceWarning("Resolving the model for {0} failed: {1}", instance.Name, ex.Message);
            return (null, $"Could not work out which model {provider.DisplayName} has loaded: "
                + ex.Message);
        }
    }

    /// <summary>
    /// Why this instance cannot authenticate the way it says it does, or null when it can.
    /// </summary>
    /// <remarks>
    /// <para>The same question <c>AiAgentCatalog.IsAvailable</c> asks, said out loud instead of used as
    /// a filter. The chooser and the Goal tile's list both hide an instance whose provider has been
    /// deleted or whose agent has been changed to one that cannot speak to it — but a tile restored
    /// from a layout is handed its stored instance without anybody asking, so the one path that was
    /// never filtered is the one where the user is not choosing anything. Left unsaid, that tile
    /// launches on the CLI's own configuration: another account, another model, and nothing on screen
    /// saying so.</para>
    /// <para>An instance naming <em>no</em> provider is not this: "the agent's own configuration" is a
    /// choice somebody made, and it is what every seeded instance starts in.</para>
    /// </remarks>
    private static string? ProviderProblem(AppSettings settings, IAiAgent agent,
        AiAgentInstance instance)
    {
        if (instance.ProviderInstanceId.Length == 0) return null;

        if (AiProviderCatalog.FindInstance(settings, instance.ProviderInstanceId) is not { } configured
            || AiProviderCatalog.Find(configured.ProviderId) is not { } provider)
            return "The provider this instance authenticates through is gone, so it would run on "
                + $"{agent.DisplayName}'s own configuration. Choose a provider on this instance.";

        return AiProviderCatalog.IsCompatible(agent, provider)
            ? null
            : $"{agent.DisplayName} cannot talk to {provider.DisplayName}, so this instance would run "
              + "on its own configuration. Choose a provider it can use.";
    }
}
