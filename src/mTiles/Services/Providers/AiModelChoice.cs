using mTiles.Models;

namespace mTiles.Services.Providers;

/// <summary>
/// What an instance's stored model name means when it is not a model name.
/// </summary>
/// <remarks>
/// <para>One sentinel, <see cref="FirstLoaded"/>, offered <b>beside</b> a local server's real list: it
/// exists so that changing the model in LM Studio does not also mean changing it in mTiles. Resolved at
/// the start of every session and <b>never persisted as a concrete name</b> — persisting the resolution
/// is the same as not having the sentinel at all.</para>
/// <para>A sentinel rather than an empty model, because empty already means something else and means it
/// everywhere: "whatever the agent would pick", which is a decision taken by the CLI without asking
/// anybody.</para>
/// </remarks>
public static class AiModelChoice
{
    /// <summary>Whatever this server has loaded when the session starts.</summary>
    /// <remarks>Underscored on both sides so it cannot collide with a real model id, in the style of
    /// every other sentinel that has to live in the same namespace as user data.</remarks>
    public const string FirstLoaded = "__first_loaded__";

    /// <summary>
    /// The model to actually ask for, or a failure that names why there is none.
    /// </summary>
    /// <remarks><b>A launch that cannot resolve this fails with a readable message rather than falling
    /// back to a model of our choosing.</b> The user asked for whatever was loaded; picking something
    /// else and not saying so is how a session quietly runs on the wrong model — and a local server
    /// that cannot be reached is a fact worth one sentence, not a silent substitution.</remarks>
    public static async Task<(string? Model, string? Problem)> ResolveAsync(IAiProvider provider,
        AiProviderInstance instance, string model, CancellationToken ct = default)
    {
        if (model != FirstLoaded)
            return (model, null);

        if (provider is not ILocalAiProvider local)
            return (null, $"{provider.DisplayName} cannot say which model is loaded, so "
                + "\"first loaded model\" has no meaning there. Choose a model.");

        var loaded = await local.FirstLoadedModelAsync(instance, ct);
        return loaded is { Length: > 0 }
            ? (loaded, null)
            : (null, $"{provider.DisplayName} has no model loaded, or could not be reached at "
                + $"{(instance.BaseUrl.Length > 0 ? instance.BaseUrl : "its address")}.");
    }
}
