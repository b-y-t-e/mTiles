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
        // The same sentence the chooser hides on and the Settings row shows - said out loud here,
        // because a tile restored from a layout is handed its stored instance without anybody choosing,
        // which is the one path no chooser filters.
        // Asked about the agent this launch resolved, which after a substitution is not the one the
        // instance names - see the overload's own remarks.
        if (AgentAvailability.Problem(instance, settings, agent) is { } unreachable)
            return (null, unreachable);

        // Before anything is asked of a provider — its model list needs the service alive to answer.
        // Only a managed one is started: a server the user runs themselves is started by the user, and
        // a hosted one has nothing to start. The ensure is the resolver's business because both places
        // that start an agent ask here first, and a dead proxy that got this far used to fail the
        // launch later, as the CLI's own network error mid-session rather than a sentence up front.
        if (instance.ApiAccountId.Length > 0
            && AiProviderCatalog.FindInstance(settings, instance.ApiAccountId) is { } managed
            && AiProviderCatalog.Find(managed.ProviderId) is IManagedAiProvider starts)
        {
            var ensured = await starts.EnsureRunningAsync(managed, ct);
            if (!ensured.Ok) return (null, ensured.Message);
        }

        if (instance.Model.Length > 0 && !agent.AcceptsModel)
            return (null, $"{agent.DisplayName} cannot be told which model to use, so "
                + $"\"{instance.Model}\" would be ignored. Clear the model on this instance, or run "
                + "it on an agent that takes one.");

        if (instance.Model != AiModelChoice.FirstLoaded)
            return (instance.Model, null);

        var runtime = AgentRuntime.For(settings, instance, agent: agent);
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
}
