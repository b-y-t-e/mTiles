using mTiles.Models;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>
/// One agent a conversation can be pointed at, as the strip's chooser offers it.
/// </summary>
/// <param name="Instance">The configured way of running an agent.</param>
/// <param name="AgentName">Which CLI that is, since an instance is named by whoever configured it.</param>
/// <param name="IsPickable">Whether picking it now would do anything.</param>
/// <param name="Reason">Why it cannot be picked, or null.</param>
/// <param name="Note">What picking it would do that the label does not say — today, that it hands the work
/// over rather than continuing the session. Shown beside the CLI's name, because a pickable entry's reason
/// is not drawn anywhere: the picker shows one only for an entry it has refused.</param>
/// <remarks>Refused entries are offered, dimmed and carry their reason rather than being left out: a list
/// that silently loses the agent somebody is looking for answers nothing, while one that says "this
/// conversation is held with Claude Code" says what to do about it. Dimmed rather than disabled, because a
/// disabled item is out of the hit test and the sentence would go unread with it; the refusal is the view
/// model's, which puts the chooser back on what is running.</remarks>
public sealed record AgentInstanceOption(AiAgentInstance Instance, string AgentName, bool IsPickable,
    string? Reason, string? Note = null)
{
    /// <summary>The instance's own name, with the CLI behind it — two instances of one agent are two accounts.</summary>
    public string Label => $"{Instance.Name} · {AgentName}";

    /// <summary>What the row says under its name.</summary>
    public string Detail => Note is { Length: > 0 } note ? $"{AgentName} · {note}" : AgentName;
}
