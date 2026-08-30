namespace mTiles.Models;

/// <summary>
/// What asking a provider whether it is there answered.
/// </summary>
/// <remarks>A record rather than a bool, because the interesting half is <em>why not</em>: a wrong key,
/// an address nothing is listening on and a local server that is running but has no model loaded are
/// three different things to do next, and a bare false is the same word for all three.</remarks>
public sealed record ProviderCheck
{
    /// <summary>Whether the provider answered as itself.</summary>
    public required bool Ok { get; init; }

    /// <summary>What to show — the version or the model count when it worked, the reason when it did
    /// not.</summary>
    public string Message { get; init; } = "";

    /// <summary>
    /// What is left on the key, where the service says so.
    /// </summary>
    /// <remarks><b><c>null</c> means "this service does not say", never zero.</b> OpenAI and Anthropic
    /// have no per-key balance endpoint and a local server has no concept of one, so the absent answer
    /// is the usual one — and showing it as 0 would tell a user with a working key that they had run
    /// out.</remarks>
    public decimal? Balance { get; init; }

    public static ProviderCheck Failed(string why) => new() { Ok = false, Message = why };

    public static ProviderCheck Reached(string what, decimal? balance = null) =>
        new() { Ok = true, Message = what, Balance = balance };
}
