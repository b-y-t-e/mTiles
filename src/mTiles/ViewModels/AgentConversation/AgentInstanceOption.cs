using mTiles.Models;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>
/// One agent a conversation can be pointed at, as the strip's chooser offers it.
/// </summary>
/// <param name="Instance">The configured way of running an agent.</param>
/// <param name="AgentName">Which CLI that is, since an instance is named by whoever configured it.</param>
/// <param name="IsPickable">Whether picking it now would do anything.</param>
/// <param name="Reason">Why it cannot be picked, or null.</param>
/// <remarks>Refused entries are offered, dimmed and carry their reason rather than being left out: a list
/// that silently loses the agent somebody is looking for answers nothing, while one that says "this
/// conversation is held with Claude Code" says what to do about it. Dimmed rather than disabled, because a
/// disabled item is out of the hit test and the sentence would go unread with it; the refusal is the view
/// model's, which puts the chooser back on what is running.</remarks>
public sealed record AgentInstanceOption(AiAgentInstance Instance, string AgentName, bool IsPickable, string? Reason)
{
    /// <summary>The instance's own name, with the CLI behind it — two instances of one agent are two accounts.</summary>
    public string Label => $"{Instance.Name} · {AgentName}";
}
