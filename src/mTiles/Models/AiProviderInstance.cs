using System.Text.Json.Serialization;
using mTiles.Services;

namespace mTiles.Models;

/// <summary>
/// One configured way of reaching a provider — a key, an address, and what the user calls it.
/// </summary>
/// <remarks>
/// <para>Separate from <c>IAiProvider</c> for the same reason <see cref="AiAgentInstance"/> is separate
/// from an agent: that is the service and there is one of each, this is configuration and there are as
/// many as somebody wants. <b>Several instances of one provider are the point</b> — two OpenRouter keys,
/// a work one and a personal one, are two rows here and one class there.</para>
/// <para>Which agent instances use it is <em>derived</em> by scanning
/// <see cref="AppSettings.AiAgentInstances"/> and never stored as a back-reference: a stored one is a
/// second copy of the same fact, and the two disagree the first time an instance is deleted.</para>
/// </remarks>
public sealed class AiProviderInstance
{
    /// <summary>This instance's own identity, which is what an agent instance stores.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Which provider it reaches — an <c>IAiProvider.Id</c>, so a row naming a provider this
    /// build does not have finds nothing rather than failing the load.</summary>
    public string ProviderId { get; set; } = "";

    /// <summary>What the user calls it. Seeded as the provider's own display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Where it lives, or empty for the provider's own address. What a local server needs and
    /// a hosted one almost never does.</summary>
    /// <remarks>Stored as the user typed it and parsed by <c>ProviderEndpoint</c> at every use, rather
    /// than normalised on the way in: a value rewritten as it is typed is a field that fights the
    /// person filling it in, and the parse is pure and cheap.</remarks>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// The key, encrypted at rest by the same route the database passwords take.
    /// </summary>
    /// <remarks>DPAPI on Windows, and on other platforms what <see cref="ProtectedStringConverter"/>
    /// can honestly offer — the settings file's own permissions. Empty for a local server, which has no
    /// authentication at all: that is a fact worth one sentence in the UI, because a discovered
    /// instance is open to everyone on that network.</remarks>
    [JsonConverter(typeof(ProtectedStringConverter))]
    public string ApiKey { get; set; } = "";

    /// <summary>How long a call to this provider may take before it is given up on.</summary>
    /// <remarks>Per instance rather than global: a model loading itself into a local server's memory
    /// takes a great deal longer than a hosted endpoint answering, and one number for both is wrong for
    /// one of them.</remarks>
    public int TimeoutSeconds
    {
        get;
        set => field = value > 0 ? value : DefaultTimeoutSeconds;
    } = DefaultTimeoutSeconds;

    /// <summary>Long enough for a cold local server, short enough that a wrong address is reported
    /// rather than waited on.</summary>
    public const int DefaultTimeoutSeconds = 30;
}
