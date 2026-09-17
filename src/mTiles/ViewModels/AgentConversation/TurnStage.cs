using mTiles.AgentSessions.Conversation;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>
/// What the agent is doing right now, in the few words the waiting row has for it.
/// </summary>
/// <remarks>
/// <para>The dots said a turn was running and nothing else. They answered <i>is it alive</i>, which is
/// worth answering, and left the question anybody actually has — <i>what is it doing</i> — to a transcript
/// the reader has to go back up and read. t3code's timeline names the running tool on that row, and it is
/// the same information one property away from where the dots already are.</para>
/// <para><b>It works for every agent because it is read from the conversation, not from a CLI.</b>
/// <c>ToolStarted</c> is part of the shared event contract and all six mappers emit it — ACP, Claude,
/// codex, opencode, pi, agy — so nothing here learns which one is talking. That is the whole argument for
/// the events being the contract.</para>
/// <para><b>The last running call, not the first.</b> An agent may have several in flight, and what the
/// reader wants is the one that has just started — the earlier ones are what it has been doing for the
/// last minute and is plainly still doing.</para>
/// <para>Empty rather than a word like "Working": the row already says that, with dots that move. A
/// second label restating it would be the tile talking about itself.</para>
/// </remarks>
public static class TurnStage
{
    /// <summary>How much of a tool's own title the row will carry before it is cut.</summary>
    /// <remarks>A tool title is a command line, and a whole one would push the clock off a narrow tile —
    /// which is the one part of this row that must always be readable, since it is what says the tile has
    /// not stalled.</remarks>
    public const int MaxLength = 38;

    /// <summary>The running tool's title, or empty when nothing names itself.</summary>
    public static string For(ConversationState? state)
    {
        if (state is null) return "";

        var running = state.Timeline
            .OfType<WorkGroupEntry>()
            .SelectMany(group => group.Items.OfType<ToolCallItem>())
            .LastOrDefault(tool => tool.State == ToolCallState.Running);

        return running is null ? "" : Cut(running.Title.Trim());
    }

    private static string Cut(string title)
    {
        // A title is often one line of a command; anything after the first newline is detail the row has
        // no room for and the transcript below already shows.
        var firstLine = title.AsSpan();
        var newline = firstLine.IndexOfAny('\r', '\n');
        if (newline >= 0) firstLine = firstLine[..newline];

        return firstLine.Length <= MaxLength
            ? firstLine.ToString().TrimEnd()
            : string.Concat(firstLine[..MaxLength].ToString().TrimEnd(), "…");
    }
}
