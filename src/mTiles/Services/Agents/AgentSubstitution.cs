namespace mTiles.Services.Agents;

/// <summary>
/// What a tile was configured as, when that is not what it could be built as.
/// </summary>
/// <remarks>
/// <para>An agent instance can be deleted, repointed at another agent, or made unavailable in Settings
/// long after a layout naming it was written. The tile still opens — an empty tile where a conversation
/// used to be is the worse answer — but it opens on <em>something else</em>, and that has two
/// consequences this record exists to carry.</para>
/// <para><b>It is said out loud, once</b> (<see cref="Notice"/> → the tile's launch notice): a Codex tile
/// that quietly comes back as Claude is a different agent working in somebody's repository, and the only
/// place that could be noticed is the tile itself.</para>
/// <para><b>And the original choice is kept</b> (<see cref="RequestedInstanceId"/> /
/// <see cref="RequestedAgentId"/>, written back by <c>AgentTileKind.Save</c>): the layout is saved for
/// any reason at all — a splitter dragged — so writing the substitute's ids would make a fallback
/// permanent within seconds, and re-adding the instance in Settings would no longer bring the tile
/// back.</para>
/// </remarks>
/// <param name="RequestedInstanceId">The instance the layout named, kept so it can be honoured again.</param>
/// <param name="RequestedAgentId">And which agent that was.</param>
/// <param name="Notice">What to tell the user, in one sentence.</param>
public sealed record AgentSubstitution(string RequestedInstanceId, string RequestedAgentId, string Notice);
