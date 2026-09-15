using mTiles.Services.Agents;

namespace mTiles.ViewModels;

/// <summary>
/// Tile content that holds an AI agent, however it is drawn.
/// </summary>
/// <remarks>
/// One property, the same bargain <see cref="IProcessTile"/> makes: what the workspace's agent-facing
/// files need to know is only <em>which</em> agents are in here, never whether one is a TUI in a
/// terminal or a conversation drawn by this application.
/// <para>It exists because those two are not one type: asking for <c>AgentTileViewModel</c> left a
/// workspace holding a conversation on codex without <c>.agents/skills</c> and never asked about its
/// <c>CLAUDE.md</c> — an agent working in the project with neither its instructions nor its
/// databases.</para>
/// </remarks>
public interface IAgentTile : ITile
{
    IAiAgent Agent { get; }
}
