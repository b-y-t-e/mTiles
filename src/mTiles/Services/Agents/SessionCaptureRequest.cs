namespace mTiles.Services.Agents;

/// <summary>
/// What an agent needs in order to work out which session its tile has just started.
/// </summary>
/// <remarks>
/// <para>A record rather than four more parameters because the members are not independent: they are one
/// question — <em>which conversation belongs to this tile</em> — and an agent that ignores three of them
/// (four of the five do) reads better asking one thing than five.</para>
/// <para><see cref="TileId"/> is what a guessing capture needs and a telling one does not: codex leaves a
/// rollout file behind and the tile has to decide which file is its own, so it must be able to skip the
/// ones its neighbours already hold (<see cref="CapturedSessions"/>). agy answers with an id and needs
/// none of it.</para>
/// </remarks>
/// <param name="ExecutablePath">Where the agent's CLI was found.</param>
/// <param name="WorkingDirectory">The tile's workspace — also what a rollout file records about itself.</param>
/// <param name="StartedAt">When this tile's agent was started, which rules out an older session.</param>
/// <param name="TileId">Who is asking, so a session another tile holds is not taken from it.</param>
/// <param name="Environment">The variables the tile's own session runs with (a <c>null</c> value
/// <b>unsets</b> one), so a capture that <em>creates</em> the conversation creates it against the same
/// provider, account and configuration the tile then resumes. Empty for a capture that only reads a
/// file. Without it, an instance that only works because of an <c>ExtraEnv</c> entry — a proxy, a path
/// to a configuration, a token — had its pre-create fail or land under a different account, and the
/// cost is a conversation that cannot be resumed rather than an error anybody sees.</param>
public sealed record SessionCaptureRequest(
    string ExecutablePath,
    string WorkingDirectory,
    DateTimeOffset StartedAt,
    string TileId,
    IReadOnlyDictionary<string, string?>? Environment = null);
