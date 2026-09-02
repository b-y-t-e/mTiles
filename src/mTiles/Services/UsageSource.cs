using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Providers;

namespace mTiles.Services;

/// <summary>
/// One account that can be asked how much of its allowance is left.
/// </summary>
/// <remarks>
/// <para>The seam <see cref="AiUsageService"/> depends on, so that it knows nothing about agents,
/// providers or sign-ins: what it does is ask, cache, record and announce, and none of that changes when
/// a seventh CLI or a second metered provider starts answering. A kind of account added later is a class
/// implementing this and a line in <see cref="UsageSources"/>.</para>
/// <para>It is also what makes the service testable at all — a fake source answers in a microsecond,
/// where the real ones want a subscription, a transcript and a network.</para>
/// </remarks>
public interface IUsageSource
{
    /// <summary>Answers, or null when there is no such question here.</summary>
    /// <remarks><b>Null and a failed report are different answers</b>, the distinction
    /// <c>IAiAgent.UsageAsync</c> spells out: null is an account this machine does not have and the tile
    /// draws no card for it, a report carrying a problem is one it has and could not ask. Never
    /// throws.</remarks>
    Task<AiUsageReport?> ReadAsync(CancellationToken ct = default);
}

/// <summary>Every account this machine could ask, worked out from the settings.</summary>
/// <remarks>
/// <para>Separate from the service for the reason the service has an interface at all: <em>which</em>
/// accounts exist is a fact about settings, agents and providers, and <em>how</em> they are polled is a
/// fact about a dashboard. Two reasons to change, two files.</para>
/// <para><b>Both halves of every agent are offered — the default account and each sign-in</b> — because
/// a second subscription is a second set of limits, which is the whole reason a sign-in exists. The
/// agents with nothing to say answer null and cost one awaited method call each.</para>
/// </remarks>
public static class UsageSources
{
    /// <summary>The accounts described by these settings, in the order their cards are drawn.</summary>
    /// <remarks>Subscriptions first and keys after, because a subscription is the thing a user is
    /// rationing by the hour and a key is a balance that changes slowly.</remarks>
    public static IReadOnlyList<IUsageSource> From(AppSettings settings) =>
    [
        .. AiAgentCatalog.All.SelectMany(agent => AccountsOf(agent, settings)),
        .. settings.AiProviderInstances
            .Select(instance => (Instance: instance, Provider: AiProviderCatalog.Find(instance.ProviderId)))
            .Where(pair => pair.Provider is not null)
            .Select(pair => new ProviderUsageSource(pair.Provider!, pair.Instance)),
    ];

    /// <remarks><b>The sign-ins come first and the default account last, which is what decides the name
    /// a duplicate keeps.</b> A machine that exports <c>CLAUDE_CONFIG_DIR</c> — which is what an mTiles
    /// sign-in sets for the tiles it launches — has its default account inside one of those sign-in
    /// directories, so the two are one login read twice; the tile keeps the first it sees, and the row
    /// the user named and can find in Settings is the better of the two to keep. A machine with no
    /// sign-ins at all still gets its default account, because nothing came before it.</remarks>
    private static IEnumerable<IUsageSource> AccountsOf(IAiAgent agent, AppSettings settings)
    {
        foreach (var signIn in AiSignInStore.For(settings, agent.Id))
            yield return new AgentUsageSource(agent, signIn);

        yield return new AgentUsageSource(agent, null);
    }
}

/// <summary>One CLI's account — its default login, or one of its sign-ins.</summary>
/// <param name="agent">The CLI being asked.</param>
/// <param name="signIn">The login, or null for the CLI's own default account.</param>
public sealed class AgentUsageSource(IAiAgent agent, AiSignIn? signIn) : IUsageSource
{
    /// <inheritdoc />
    public Task<AiUsageReport?> ReadAsync(CancellationToken ct = default) =>
        agent.UsageAsync(signIn, ct);
}

/// <summary>One configured key at a metered service.</summary>
public sealed class ProviderUsageSource(IAiProvider provider, AiProviderInstance instance) : IUsageSource
{
    /// <inheritdoc />
    public Task<AiUsageReport?> ReadAsync(CancellationToken ct = default) =>
        provider.UsageAsync(instance, ct);
}
