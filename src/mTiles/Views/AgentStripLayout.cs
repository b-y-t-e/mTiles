namespace mTiles.Views;

/// <summary>
/// The order in which the Agent tile's top strip gives up its words.
/// </summary>
/// <remarks>
/// <para><b>The conversation goes first.</b> Its title is the conversation's opening words, which the
/// transcript right under the strip is already showing; the list is for switching, and the icon opens it
/// just the same.</para>
/// <para><b>The status goes to a dot next.</b> Its colour already carries the meaning — the word is
/// there to be read once — and the tile header's own mark says the same thing again.</para>
/// <para><b>The agent trims, and goes to its icon last</b>, because which agent holds the conversation is
/// said nowhere else on the tile. Stop is an icon already and never gives way; New conversation is in the tile's header.</para>
/// <para>The arithmetic is <see cref="RowRetreat"/>'s.</para>
/// </remarks>
public static class AgentStripLayout
{
    public const int Agent = 0, Status = 1, Stop = 2, Conversation = 3;

    public static readonly IReadOnlyList<RetreatStep> Steps =
    [
        new RetreatStep.Compact(Conversation),
        new RetreatStep.Compact(Status),
        new RetreatStep.Trim(Agent),
        new RetreatStep.Compact(Agent),
    ];
}
