using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Providers;

namespace mTiles.ViewModels;

/// <summary>
/// Where the context gauge's denominator comes from when the agent did not name one, and in which order —
/// the one copy both agent tiles ask.
/// </summary>
/// <remarks>
/// <para><b>Three sources in order.</b> The user's own figure (<c>AiAgentInstance.MaxContextTokens</c>,
/// Settings → AI) is a decision and is handed over unchanged, the precedence
/// <c>ModelContextWindow.Answer</c> already gives it. Then the provider, which is a fact about what is
/// being served and is also what this application hands the CLI, so the gauge and the run agree. Then the
/// agent's own account (<see cref="IAiAgent.AccountContextWindowAsync"/>), which is the only route left
/// for a subscription — the commonest configuration there is, and the one with no provider to ask.</para>
/// <para><b>There is deliberately no guess after that.</b> An earlier version fell back to what Claude
/// Code documents itself as assuming for a model it cannot verify — 200 000 — and drew a full bar over a
/// conversation of 234k on <c>claude-opus-5</c>, whose window is 1 000 000. A bar pinned at 100% is read
/// as "about to run out", which is the one thing it must not say wrongly, so a tile with no source for
/// the window shows the count and no bar.</para>
/// <para>A class of its own rather than a member of <see cref="ContextWindowFollower"/>, which owns only
/// which model an answer is for: the order changes when a new source appears, the following does not.
/// </para>
/// </remarks>
public static class GaugeWindowSources
{
    /// <summary>The window to count against: the user's own figure, else the one followed for the
    /// model the conversation runs on.</summary>
    public static long? For(AiAgentInstance instance, ContextWindowFollower followed) =>
        instance.MaxContextTokens ?? followed.Window;

    /// <summary>How large a context <paramref name="model"/> is served with: the provider where the
    /// instance runs on one, otherwise the agent's own account.</summary>
    /// <remarks>Never both: a provider and a sign-in are one slot, so an instance on a provider must not
    /// have its window answered by — nor a request made with the token of — a subscription it does not
    /// run as.</remarks>
    public static async Task<long?> LookupAsync(AppSettings settings, IAiAgent agent,
        AiAgentInstance instance, string model, CancellationToken ct) =>
        RunsOnProvider(instance)
            ? await ModelContextWindow.ContextOfAsync(settings, agent, instance, model, ct)
            : await agent.AccountContextWindowAsync(AiSignInStore.Find(settings, instance.SignInId), model, ct);

    private static bool RunsOnProvider(AiAgentInstance instance) =>
        !string.IsNullOrEmpty(instance.ApiAccountId);
}
