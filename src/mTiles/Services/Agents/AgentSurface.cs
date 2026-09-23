namespace mTiles.Services.Agents;

/// <summary>
/// How a CLI is being run right now: as its own terminal UI, or as a structured session this application
/// drives.
/// </summary>
/// <remarks>
/// <para>A parameter to the capability questions whose answer differs between the two, for the reason
/// <see cref="Models.AiUsage"/> is one — and a second question rather than a member of that one, because
/// they ask different things. <c>AiUsage</c> separates the tile's own session from a goal run; both tiles
/// hold the agent for somebody who is watching, and both therefore fit their flags as
/// <see cref="Models.AiUsage.Interactive"/>. What this separates is the *program* on the other end: a
/// terminal agent tile runs the CLI's own interface, while an Agent tile drives
/// <c>claude -p --output-format stream-json</c>, a JSON-RPC child or an ACP peer — a different surface of
/// the same binary, with its own behaviour and its own documentation.</para>
/// <para>Two members and no more: they are the two tile kinds that hold an agent, and a third would be a
/// third tile.</para>
/// </remarks>
public enum AgentSurface
{
    /// <summary>The CLI's own interface, in a terminal — a terminal agent tile.</summary>
    Terminal,

    /// <summary>The headless or protocol session an Agent tile drives and draws itself.</summary>
    Structured,
}
