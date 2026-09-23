using mTiles.AgentSessions.Events;
using mTiles.Controls;
using mTiles.Services;

namespace mTiles.Views;

/// <summary>How a permission mode and an effort level read as a row of a picker, for both tiles that offer them.</summary>
/// <remarks>One shape for the Goal tile and the Agent tile, so that a row's look — a badge, an icon, the
/// sentence under it — changes in both at once and the two never explain the same mode in different words.</remarks>
public static class SettingPickerRows
{
    /// <summary>A mode named by its label, the way the Goal tile's strip lists them.</summary>
    public static PickerOption Mode(string label) =>
        Row(label, label, AiBehaviours.Description(AiBehaviours.FromLabel(label)));

    /// <summary>An effort named by its label, the way the Goal tile's strip lists them.</summary>
    public static PickerOption Effort(string label) =>
        Row(label, label, AiEfforts.Description(AiEfforts.FromLabel(label)));

    /// <summary>
    /// A Goal effort preset named by its label, with the levels it stands for as its description.
    /// </summary>
    /// <remarks>The description is what lets the Goal tile's strip carry one picker where the feature
    /// has an effort per role: <c>plan medium · work low · review high</c> is visible the moment the
    /// list is opened and takes no width at all the rest of the time.</remarks>
    public static PickerOption EffortPreset(string label) =>
        Row(label, label, GoalRoles.Description(GoalRoles.FromLabel(label)));

    /// <summary>A Goal review gate mode named by its label, with what it does as its description.</summary>
    public static PickerOption GateMode(string label) =>
        Row(label, label, GoalReviewGatePolicy.Description(GoalReviewGatePolicy.FromLabel(label)));

    /// <summary>A mode as a session offers it; the vocabulary's sentence where the agent gave none.</summary>
    public static PickerOption Mode(SessionOption option) =>
        Row(option.Id, option.Label, option.Description ?? AiBehaviours.DescriptionOf(option.Id));

    /// <summary>An effort as a session offers it; the vocabulary's sentence where the agent gave none.</summary>
    public static PickerOption Effort(SessionOption option) =>
        Row(option.Id, option.Label, option.Description ?? AiEfforts.DescriptionOf(option.Id));

    private static PickerOption Row(string id, string title, string? description) =>
        new() { Id = id, Title = title, Description = description };
}
