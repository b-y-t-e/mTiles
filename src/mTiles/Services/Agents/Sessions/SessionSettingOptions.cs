using mTiles.AgentSessions.Events;
using mTiles.Models;

namespace mTiles.Services.Agents.Sessions;

/// <summary>
/// The application's own vocabulary for a conversation's permission mode and effort, as options a viewer
/// can offer and send back.
/// </summary>
/// <remarks>
/// <para><b>Canonical ids, never an agent's words.</b> A mode is an <see cref="AiBehaviour"/> name and an
/// effort an <see cref="AiEffort"/> name, so a browser offers "Plan" for every agent and each session
/// translates it into its own CLI's spelling — <c>set_permission_mode plan</c>, a codex sandbox, opencode's
/// permission rules. What an agent is offered is narrowed to what it supports interactively, the same
/// rule the Settings chooser follows.</para>
/// </remarks>
public static class SessionSettingOptions
{
    public static IReadOnlyList<SessionOption> Modes(IAiAgent agent, AiAgentInstance instance) =>
    [
        .. agent.SupportedBehaviours(instance, AiUsage.Interactive)
            .Select(mode => new SessionOption(ModeId(mode), AiBehaviours.Label(mode))),
    ];

    public static IReadOnlyList<SessionOption> Efforts(IAiAgent agent, AiAgentInstance instance) =>
    [
        .. agent.SupportedEfforts(instance, AiUsage.Interactive)
            .Select(effort => new SessionOption(EffortId(effort), AiEfforts.Label(effort))),
    ];

    /// <summary>
    /// The listed models an instance can actually run on: with a provider, only those under that provider's
    /// <c>provider/</c> prefix; without one, all of them.
    /// </summary>
    /// <remarks>opencode and pi list every provider in their own registry. A model under another provider would
    /// run there, on another key, while the instance names its own — and kept in the layout it would be
    /// qualified again at the next launch as <c>provider/other/model</c>, a third thing again.</remarks>
    public static IReadOnlyList<SessionOption> ModelsOfProvider(IEnumerable<SessionOption> models,
        Providers.AgentRuntime runtime)
    {
        if (runtime.Provider is not { } provider) return [.. models];

        var prefix = $"{provider.CatalogueId}/";
        return [.. models.Where(model => model.Id.StartsWith(prefix, StringComparison.Ordinal))];
    }

    public static string ModeId(AiBehaviour mode) => mode.ToString();

    public static string EffortId(AiEffort effort) => effort.ToString();

    public static AiBehaviour? ParseMode(string? id) =>
        Enum.TryParse<AiBehaviour>(id, ignoreCase: false, out var mode) ? mode : null;

    public static AiEffort? ParseEffort(string? id) =>
        Enum.TryParse<AiEffort>(id, ignoreCase: false, out var effort) ? effort : null;
}

/// <summary>
/// What a conversation tile runs differently from its instance — chosen in the tile, kept in its layout.
/// </summary>
/// <remarks>A copy of the instance with these applied, never a change to the instance itself: the instance
/// is every tile's, and switching one conversation to another model must not switch the others.</remarks>
public sealed record SessionOverrides(string? Model = null, AiBehaviour? Behaviour = null, AiEffort? Effort = null)
{
    public static readonly SessionOverrides None = new();

    public bool IsEmpty => Model is null && Behaviour is null && Effort is null;

    /// <summary>These overrides with a change laid over them.</summary>
    public SessionOverrides With(SessionSettings change) => new(
        change.Model ?? Model,
        SessionSettingOptions.ParseMode(change.Mode) ?? Behaviour,
        SessionSettingOptions.ParseEffort(change.Effort) ?? Effort);

    /// <summary>The instance as this tile runs it.</summary>
    public AiAgentInstance ApplyTo(AiAgentInstance instance)
    {
        if (IsEmpty) return instance;

        var copy = instance.Clone();
        copy.Model = Model ?? instance.Model;
        copy.DefaultEffort = Effort ?? instance.DefaultEffort;
        copy.DefaultBehaviour = Behaviour ?? instance.DefaultBehaviour;
        return copy;
    }
}
