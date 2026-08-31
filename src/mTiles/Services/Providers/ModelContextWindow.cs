using System.Collections.Concurrent;
using System.Diagnostics;
using mTiles.Models;
using mTiles.Services.Agents;

namespace mTiles.Services.Providers;

/// <summary>
/// The auto-compact window Claude Code is given for a third-party model, worked out from the model's
/// context window.
/// </summary>
/// <remarks>
/// <para><b>Why at all.</b> On its own provider Claude Code knows every model's context window. On a
/// third-party one — OpenRouter, z.ai, LM Studio, Ollama — the model id is one it does not recognise,
/// and it <em>assumes</em> a window for it: it can assume 200K where the model has 128K, and then the
/// compaction that should have saved the session instead lets it run off the provider's end.
/// <c>CLAUDE_CODE_AUTO_COMPACT_WINDOW</c> is the documented way to name the threshold outright.</para>
/// <para><b>Why 80%, and why that is ours and not the CLI's.</b> The documented default compacts at the
/// model's full context limit; 80% is this application's own margin — headroom for the output, the
/// tool results and the accounting that a real turn spends on top of the conversation. It is a policy
/// chosen here, said here, and argued in a table test, not a number anybody else's documentation
/// promises.</para>
/// <para>The value answers null — "set nothing" — rather than guessing, in the two cases where a guess
/// would be worse than the CLI's own assumption: the provider did not say, or the model's context is
/// too small for the variable's documented minimum (100 000 tokens) to stay inside 80% of it.</para>
/// <para><b>An instance that carries its own <c>AutoCompactWindow</c> is asked nothing at all</b>: the
/// typed value is the whole answer and this type is only the fallback for the field left empty.</para>
/// </remarks>
public static class ModelContextWindow
{
    /// <summary>The share of the model's context the auto-compact window takes.</summary>
    public const decimal Share = 0.8m;

    /// <summary>Claude Code's documented minimum for the variable; below it the value is clamped
    /// upward by the CLI, which for a small model would set a window at or above the model's whole
    /// context — the failure this exists to prevent, committed by hand.</summary>
    public const long MinimumWindow = 100_000;

    /// <summary>Claude Code's documented maximum, which it also enforces; applied here so a very large
    /// model does not hand the CLI a number it will only clamp back.</summary>
    public const long MaximumWindow = 1_000_000;

    /// <summary>
    /// The window to set for a model of <paramref name="contextTokens"/>, or null to set nothing.
    /// </summary>
    /// <remarks>80% of the context, rounded down — the same asymmetry <c>AiEfforts</c> argues for:
    /// being wrong upward spends context the model does not have. A result below the variable's
    /// documented minimum answers null, because the CLI would clamp it up past the margin this exists
    /// to keep.</remarks>
    public static long? Window(long? contextTokens) => contextTokens is not { } context
        ? null
        : Math.Min(MaximumWindow, (long)(context * Share)) is { } window && window >= MinimumWindow
            ? window
            : null;

    /// <summary>
    /// The context window of the model this launch runs on, or null when there is nothing to ask.
    /// </summary>
    /// <remarks>
    /// <para>Asked by both places that resolve a model — the agent tile and the Goal tile's run — so
    /// one launch cannot end up with the env var and the other without it. Gates on the agent's own
    /// answer to <c>IAiAgent.UsesModelContextWindow</c>, so no provider is fetched for an agent that
    /// reads none of this.</para>
    /// <para>Cached for half an hour against the provider, address and model: the Goal tile resolves
    /// per AI call, and OpenRouter's answer to the question is a ~1 MB catalogue that says the same
    /// thing for half an hour. The same shape as <c>AiAgentCatalog.Locate</c>'s detection cache.</para>
    /// </remarks>
    public static async Task<long?> ResolveAsync(AppSettings settings, IAiAgent agent,
        AiAgentInstance instance, string model, CancellationToken ct = default)
    {
        // A window typed on the instance is the launch's whole answer — read by ClaudeAgent.Configure
        // straight off the runtime — so there is nothing here to resolve and no provider to ask.
        if (instance.AutoCompactWindow is not null) return null;

        if (!agent.UsesModelContextWindow || model.Length == 0 || model == AiModelChoice.FirstLoaded)
            return null;

        var runtime = AgentRuntime.For(settings, instance, model, agent);
        if (runtime.Provider is not { } provider || runtime.ProviderInstance is not { } configured)
            return null;

        var key = $"{provider.Id}|{runtime.ProviderInstance.BaseUrl.Trim()}|{model}";
        if (Cache.TryGetValue(key, out var held)
            && DateTimeOffset.UtcNow - held.At < TimeSpan.FromMinutes(30))
            return held.Window;

        long? context;
        try
        {
            context = await provider.ContextWindowAsync(configured, model, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The same promise AgentModelResolver makes: this answers. A context window nobody could
            // read is "set nothing", not a launch that dies — the session runs on the CLI's own
            // assumption, which is what it did before this existed.
            Trace.TraceWarning("Reading the context window of {0} on {1} failed: {2}",
                model, provider.DisplayName, ex.Message);
            context = null;
        }

        var window = Window(context);
        Cache[key] = (DateTimeOffset.UtcNow, window);
        return window;
    }

    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, long? Window)> Cache = new();

    /// <summary>Empties the cache. Tests only: answers must not depend on what an earlier test asked.
    /// </summary>
    internal static void Reset() => Cache.Clear();
}
