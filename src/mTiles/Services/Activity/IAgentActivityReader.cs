using mTiles.Models;

namespace mTiles.Services.Activity;

/// <summary>
/// What one CLI's own signals mean.
/// </summary>
/// <remarks>
/// <para><b>This is the whole of the layering boundary, and it points one way.</b>
/// <c>Services/Activity/</c> knows how signals are gathered, ranked and smoothed and learns nothing
/// about agents; <c>Services/Agents/</c> knows what a title or a line of its own UI means and learns
/// nothing about arbitration. The same bargain <c>WorkspaceAgentFiles</c> makes with
/// <c>IAiAgent.SkillsDirectory</c>: the agent says what its signal is, somebody else decides what to do
/// with it.</para>
/// <para>Implemented by <c>AiAgent</c>, so every agent has it and the ones with nothing measured about
/// them answer <see cref="TileActivity.Unknown"/> to both — which costs nothing, because the tile then
/// falls back to raw output, the behaviour it has always had.</para>
/// </remarks>
public interface IAgentActivityReader
{
    /// <summary>What the terminal title this CLI set means, or Unknown when it is not one this agent
    /// recognises.</summary>
    TileActivity ReadTitle(string title);

    /// <summary>
    /// What the text this CLI has painted recently means, or Unknown.
    /// </summary>
    /// <param name="recent">Printable text from the child's own output with escape sequences taken
    /// out, most recent last. <b>Not a snapshot of the screen</b> — see
    /// <see cref="RecentOutputSource"/> for why the difference matters and what it costs.</param>
    /// <param name="detail">A sentence for the tooltip, when the match has one to give.</param>
    TileActivity ReadRecentOutput(string recent, out string? detail);
}
