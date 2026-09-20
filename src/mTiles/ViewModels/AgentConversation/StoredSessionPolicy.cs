using mTiles.AgentSessions.Events;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Agents.Sessions;
using mTiles.Services.Providers;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>What opening a stored conversation does to the tile's account: take a row, say something, or
/// nothing at all.</summary>
/// <param name="InstanceToTake">The configured row the conversation last ran as, where the tile holds another.
/// </param>
/// <param name="Notice">What the user has to know before the first message, because the login moves.</param>
public sealed record StoredAccountDecision(AiAgentInstance? InstanceToTake = null, string? Notice = null)
{
    public static readonly StoredAccountDecision Nothing = new();
}

/// <summary>
/// The pure rules for putting a tile back on the account and the settings a stored conversation last ran as.
/// </summary>
/// <remarks>
/// <para>A separate class from the tile because it is a separate reason to change: <i>what</i> is adopted is
/// an opinion — never bypass, never a value the instance already answers, never a model spelled for another
/// account — and an opinion is argued in a table test, the convention <c>SkillChangePolicy</c> and
/// <c>ChainPolicy</c> follow. The tile keeps only the doing: taking the row, raising the notice, saving.</para>
/// <para><b>Every path on which the login moves says so before the first message</b>: a stored row that is
/// gone, and equally the stored row whose sign-in has been edited in Settings since — the resume token lives
/// in the login's directory, so both start cold, and neither may be learnt only from a seam drawn after it.
/// </para>
/// </remarks>
public static class StoredSessionPolicy
{
    /// <summary>Decides what the account stored with a conversation asks of the tile about to run it.</summary>
    /// <param name="stored">The account the conversation last ran as, or null where nothing said.</param>
    /// <param name="running">The account the tile would run as without this decision.</param>
    /// <param name="runnable">The configured instances this machine can run, which is what may be taken.</param>
    public static StoredAccountDecision DecideAccount(
        SessionAccount? stored, SessionAccount running, IEnumerable<AiAgentInstance> runnable)
    {
        if (stored is null || stored.AgentId != running.AgentId || stored.IsSameAs(running))
            return StoredAccountDecision.Nothing;

        if (StoredRow(stored, runnable) is not { } row)
            return stored.SharesLoginWith(running)
                ? StoredAccountDecision.Nothing
                : new StoredAccountDecision(Notice: LoginGoneNotice(stored));

        // The row this tile already holds, its sign-in edited in Settings since: nothing to take, and taking
        // it again would drop a model picked in the strip for a switch that never happened.
        return row.Id == running.InstanceId
            ? new StoredAccountDecision(Notice: LoginChangedNotice(stored))
            : new StoredAccountDecision(InstanceToTake: row);
    }

    /// <summary>The model, mode and effort a conversation ran on that the tile should restore, as a change to
    /// lay over its overrides.</summary>
    /// <param name="stored">What the conversation last ran on — the model only where somebody picked it, and
    /// already spelled the instance's way.</param>
    /// <param name="overrides">What the user has already chosen in this tile, which is never overruled.</param>
    /// <param name="instance">The instance the tile runs on, whose own answers are left to it.</param>
    /// <param name="modelStillFits">The model was resolved against the account about to run.</param>
    public static SessionSettings SettingsToRestore(
        SessionSettings stored, SessionOverrides overrides, AiAgentInstance instance, bool modelStillFits) =>
        new(overrides.Model is null && modelStillFits ? ModelWorthAdopting(stored.Model, instance) : null,
            overrides.Behaviour is null ? ModeWorthAdopting(stored.Mode, instance) : null,
            overrides.Effort is null ? EffortWorthAdopting(stored.Effort, instance) : null);

    /// <summary>What a seam or a notice calls the account the work ran as.</summary>
    /// <remarks>The instance's name first, because that is the row the user named and can find in Settings;
    /// the agent's own name where nothing named the instance.</remarks>
    public static string AccountLabel(SessionAccount account) =>
        account.InstanceName is { Length: > 0 } name
            ? name
            : AiAgentCatalog.Find(account.AgentId)?.DisplayName ?? account.AgentId;

    public static string LoginGoneNotice(SessionAccount stored) =>
        $"This conversation ran as \"{AccountLabel(stored)}\", which is no longer available here, so it " +
        "continues on another account — the transcript stays, what the model remembers does not.";

    public static string LoginChangedNotice(SessionAccount stored) =>
        $"\"{AccountLabel(stored)}\" is signed in to another account than when this conversation ran, so it " +
        "continues there — the transcript stays, what the model remembers does not.";

    private static AiAgentInstance? StoredRow(SessionAccount stored, IEnumerable<AiAgentInstance> runnable) =>
        stored.InstanceId is { Length: > 0 } instanceId
            ? runnable.FirstOrDefault(row => row.Id == instanceId)
            : null;

    /// <remarks>A model the instance resolves at every launch (<c>AiModelChoice.FirstLoaded</c>) is never
    /// written down, since the point of it is that it moves.</remarks>
    private static string? ModelWorthAdopting(string? model, AiAgentInstance instance) =>
        instance.Model == AiModelChoice.FirstLoaded || model is not { Length: > 0 } || model == instance.Model
            ? null
            : model;

    /// <remarks><b>bypass is never adopted.</b> It is the largest single grant here and the strip only reaches
    /// it through a dialog, because it is kept in the layout and survives every restart; adoption is a start
    /// nobody is standing in front of. The instance's own mode is not adopted either: it is already what the
    /// tile runs without an override.</remarks>
    private static string? ModeWorthAdopting(string? mode, AiAgentInstance instance) =>
        SessionSettingOptions.ParseMode(mode) is { } behaviour
        && behaviour != AiBehaviour.BypassPermissions && behaviour != instance.DefaultBehaviour
            ? mode
            : null;

    private static string? EffortWorthAdopting(string? effort, AiAgentInstance instance) =>
        SessionSettingOptions.ParseEffort(effort) == instance.DefaultEffort ? null : effort;
}
