using System.Diagnostics;
using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services.Providers;

/// <summary>
/// The one question only a provider that owns a service on this machine can answer: is it running, and
/// can it be brought up.
/// </summary>
/// <remarks>
/// <para>A second interface rather than a member on <see cref="IAiProvider"/>: a hosted provider cannot
/// start anything, and a local server the user starts themselves — LM Studio, Ollama — must not be
/// started for them, because "serve on local network" is a choice about who can reach the machine.
/// The callers that care are the launch path and the Test button, and both know they are asking about
/// a managed daemon.</para>
/// <para>Asked before anything else is asked of the provider — a model list or a context window needs
/// the service alive to answer — and the answer is the established one: a check, never a throw.</para>
/// </remarks>
public interface IManagedAiProvider : IAiProvider
{
    /// <summary>
    /// Makes sure this provider's service is running, starting it if it has to, or says why it cannot.
    /// </summary>
    /// <remarks>Idempotent: a service already up is a probe and no more. Starting one is a service
    /// action, not a hidden installation — the install of the tool itself stays a button the user
    /// presses and a tile they can see.</remarks>
    Task<ProviderCheck> EnsureRunningAsync(AiProviderInstance instance, CancellationToken ct = default);
}
