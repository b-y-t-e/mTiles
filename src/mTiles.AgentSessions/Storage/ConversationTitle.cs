namespace mTiles.AgentSessions.Storage;

/// <summary>
/// What to call a stored conversation in a list.
/// </summary>
/// <remarks>
/// <para>Pure, and here rather than in a view model for the reason <c>ConversationReducer</c> is here: a
/// browser listing the same conversations must call them the same things, or the two viewers disagree about
/// which row is which.</para>
/// <para><b>The user's own first words, never the agent's reply.</b> What a conversation is about is what was
/// asked for; the answer is the agent's account of it and is both longer and written to be read in place.</para>
/// </remarks>
public static class ConversationTitle
{
    /// <summary>How much of the opening a row shows before it is cut.</summary>
    /// <remarks>A conversation's first message is often a paragraph, and a list of paragraphs is not a list.
    /// Cut at a word, and only when what is left is long enough to be a title on its own — trimming
    /// "Fix the build" to "Fix the…" buys nothing.</remarks>
    public const int MaxLength = 72;

    /// <summary>What a conversation with nothing said in it is called.</summary>
    public const string Unused = "Empty conversation";

    public static string For(ConversationSummary summary) => For(summary.Opening);

    public static string For(string? opening)
    {
        if (opening is null) return Unused;

        // The first line and nothing after it: a message often carries a paragraph of context under its
        // request, and an `@` mention or a pasted stack trace is lines of it.
        var text = FirstLine(opening);
        if (text.Length == 0) return Unused;
        return text.Length <= MaxLength ? text : $"{CutAtWord(text)}…";
    }

    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }

        return "";
    }

    /// <summary>The longest prefix within the budget that ends at a word, or the budget when there is no word
    /// break to take — a path or a single long token has none, and cutting it is still better than a row
    /// the width of the message.</summary>
    private static string CutAtWord(string text)
    {
        var cut = text[..MaxLength];
        var space = cut.LastIndexOf(' ');
        return space > MaxLength / 2 ? cut[..space].TrimEnd() : cut.TrimEnd();
    }
}
