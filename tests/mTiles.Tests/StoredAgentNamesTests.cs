using mTiles.Models;
using mTiles.Services.Tiles;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The names an agent-running tile is written down under are on people's disks, so they are pinned
/// here rather than left to read as obvious from the identifiers that carry them.
/// </summary>
/// <remarks>
/// <para>Renaming the constants was safe; renaming the <em>values</em> is not, and nothing else in the
/// suite would notice. <c>TileKindIds.TerminalAgent</c> reads as though it should spell
/// <c>terminal-agent</c> now that the class behind it is <c>TerminalAgentTileKind</c> — and a tidying
/// change to that spelling compiles, passes every other test, and opens every saved
/// <c>workspaces/{id}.json</c> as an empty tile whose emptiness the first save writes back over the
/// user's layout.</para>
/// <para>The same holds for <see cref="AgentStateKeys"/>: a key nobody reads is a tile that comes back
/// on the CLI's factory settings with its conversation lost.</para>
/// </remarks>
public sealed class StoredAgentNamesTests
{
    [Fact]
    public void The_kinds_and_state_keys_keep_the_names_they_were_written_under()
    {
        Assert.Equal("agent", TileKindIds.TerminalAgent);
        Assert.Equal("agent-conversation", TileKindIds.AgentConversation);
        Assert.Equal("agentInstanceId", AgentStateKeys.InstanceIdKey);
        Assert.Equal("agentId", AgentStateKeys.AgentIdKey);
        Assert.Equal("shellName", AgentStateKeys.ShellNameKey);
        Assert.Equal("sessionId", AgentStateKeys.SessionIdKey);
    }
}
