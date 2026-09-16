namespace mTiles.Services.Tiles;

/// <summary>
/// The names a layout writes an agent-running tile's state down under, shared by the
/// <b>Terminal agent</b> tile (<see cref="TerminalAgentTileKind"/>) and the <b>Agent</b> tile
/// (<see cref="AgentConversationTileKind"/>).
/// </summary>
/// <remarks>
/// <para>One set of names, because the two kinds answer the same questions about the same instance and a
/// tile can be turned from one into the other where it stands. They used to be the terminal kind's own
/// constants, which the conversation kind then reached into — so the Agent tile stored its instance under
/// a name belonging to the tile it is not. The name here belongs to neither kind, for the same reason.</para>
/// <para><b>The strings are on people's disks</b> and cannot move: what is renamed here is only what this
/// code calls them.</para>
/// </remarks>
public static class AgentStateKeys
{
    /// <summary>The configured way of running an agent this tile was created from.</summary>
    public const string InstanceIdKey = "agentInstanceId";

    /// <summary>And which agent that was, as a fallback for when the instance has been deleted.</summary>
    public const string AgentIdKey = "agentId";

    /// <summary>
    /// The shell the agent's commands run in, written for the rollback and nothing else.
    /// </summary>
    /// <remarks>An older build reads a terminal agent leaf as a terminal (<c>TileKindIds.ToLegacy</c>), and
    /// a terminal without a shell name opens on whatever that machine's default is. This build never reads
    /// it: the shell a terminal agent tile uses is the default one, decided at every launch.</remarks>
    public const string ShellNameKey = "shellName";

    /// <summary>The conversation to resume, for an agent that names its own — see
    /// <see cref="mTiles.Models.SessionStrategy.CapturedAfterStart"/>. Absent for the other two strategies,
    /// where the tile's own id is the session id and writing it down twice would let the two disagree.</summary>
    public const string SessionIdKey = "sessionId";
}
