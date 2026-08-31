using mTiles.Models;

namespace mTiles.Services.Providers;

/// <summary>
/// The two questions only a server running on this machine or this network can answer.
/// </summary>
/// <remarks>
/// <para>A second interface rather than two more members on <see cref="IAiProvider"/>: nothing hosted
/// can say which model is <em>loaded</em> right now, and a hosted provider forced to answer would have
/// to answer with a lie or a null that every caller then has to test. The callers that care are the
/// model chooser and the network scan, and both know they are asking about a local server.</para>
/// </remarks>
public interface ILocalAiProvider : IAiProvider
{
    /// <summary>
    /// The model this server has loaded right now, or null when it has none — or cannot be reached.
    /// </summary>
    /// <remarks>What <c>AiModelChoice.FirstLoaded</c> resolves to, at the start of every session and
    /// never persisted as a name: the point of the sentinel is that changing the model in LM Studio does
    /// not mean changing it in mTiles too.</remarks>
    Task<string?> FirstLoadedModelAsync(AiProviderInstance instance, CancellationToken ct = default);

    /// <summary>
    /// The ports worth trying when looking for this server, most likely first.
    /// </summary>
    /// <remarks><para>Here rather than on <see cref="IAiProvider"/>, which is where it was declared and
    /// where it meant nothing: the only caller is the network scan, it takes an
    /// <see cref="ILocalAiProvider"/>, and on the four hosted providers the property resolved to
    /// <c>[443]</c> — an answer nothing asks for and nobody could act on. The same argument this
    /// interface makes for itself, applied to a third member.</para>
    /// <para><c>IAiProvider.DefaultPort</c> stays where it is, because it answers a different question
    /// every provider has: what port to assume when the user types a bare host. That has to be one
    /// value, and it is the first of these.</para>
    /// <para>Kept short on purpose. This is a scan of somebody's network, so every extra entry is
    /// another connection attempt per address — and a port nobody actually uses buys nothing but
    /// time.</para></remarks>
    IReadOnlyList<int> DiscoveryPorts { get; }

    /// <summary>
    /// Whether this address is really this provider — asked by protocol, never by port.
    /// </summary>
    /// <remarks>An open 11434 is not proof of Ollama. A scan that reported it as one would offer the
    /// user a provider that answers every call with an HTML error page.</remarks>
    Task<bool> IsServingAsync(Uri baseUrl, CancellationToken ct = default);
}
