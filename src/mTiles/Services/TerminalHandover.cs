using System.Text;
using mTiles.Services.Agents.SessionLogs;

namespace mTiles.Services;

/// <summary>
/// The brief a terminal agent tile hands to the agent it is switching to, folded out of the outgoing CLI's
/// own transcript.
/// </summary>
/// <remarks>
/// <para><b>The Agent tile's <c>ConversationHandover</c> has a record of its own to fold; this has only
/// the CLI's.</b> A terminal agent tile draws nothing itself — the conversation is the TUI's — so what is
/// said is read out of the store the CLI writes (<see cref="IAgentSessionLog.ReadTranscriptAsync"/>): the
/// messages, never the tool calls, which the working tree already carries and <c>git diff</c> shows better.
/// </para>
/// <para><b>The first request verbatim, then as much of the end as fits</b>, oldest dropped first and
/// <b>the number dropped said out loud</b> — a brief that quietly loses the middle reads as a complete
/// account of a smaller task, the rule <c>ConversationHandover</c> keeps.</para>
/// <para><b>Delivered as a file, pointed at by one line typed into the new agent.</b> The brief runs to
/// pages; a line of that length typed into a TUI is a paste some of them fold and some of them cut, while
/// every one of them can read a file in its own working tree. It goes under <c>.mtiles/handover/</c>, which
/// carries a <c>.gitignore</c> of its own — <c>.mtiles/</c> is ignored only where a git tile has said so, and a
/// brief is the whole conversation — and only the newest <see cref="Kept"/> are kept.</para>
/// </remarks>
public static class TerminalHandover
{
    /// <summary>How much of the conversation the brief carries, in characters.</summary>
    public const int Budget = 24_000;

    /// <summary>The longest any one message may run before it is cut.</summary>
    public const int MessageCap = 3_000;

    /// <summary>The directory under the workspace the briefs are written to.</summary>
    public static string DirectoryIn(string workspaceDir) =>
        Path.Combine(workspaceDir, ".mtiles", "handover");

    /// <summary>How many briefs a workspace keeps; older ones are deleted when a new one is written.</summary>
    public const int Kept = 5;

    /// <summary>The brief's file name: sortable by time, and carrying the tile's id so two tiles switched in
    /// the same second never write — and point their agents at — the same file.</summary>
    /// <remarks>Both ids go through <see cref="SafePathComponent"/>: the tile's is read out of a hand-editable
    /// layout, and the name is both a path and part of the line typed into the agent.</remarks>
    public static string FileName(DateTime now, string agentId, string tileId) =>
        $"{now:yyyyMMdd-HHmmss}-{SafePathComponent.Of(agentId)}-{SafePathComponent.Of(tileId)}.md";

    /// <summary>Writes a brief under <see cref="DirectoryIn"/>, keeps the directory ignored by git and
    /// deletes all but the newest <see cref="Kept"/>.</summary>
    /// <remarks>Owner-only (<see cref="PrivateFile"/>), the rule the goal logs keep: a brief is the whole
    /// conversation, and on a shared machine the umask would hand it to every other user.</remarks>
    public static Task SaveAsync(string workspaceDir, string fileName, string brief) => Task.Run(() =>
    {
        var directory = DirectoryIn(workspaceDir);
        PrivateFile.CreateDirectory(directory);
        var ignore = Path.Combine(directory, ".gitignore");
        if (!File.Exists(ignore)) File.WriteAllText(ignore, "*\n");
        PrivateFile.WriteAllText(Path.Combine(directory, fileName), brief);
        DeleteAllButNewest(directory);
    });

    private static void DeleteAllButNewest(string directory)
    {
        foreach (var old in new DirectoryInfo(directory).GetFiles("*.md")
                     .OrderByDescending(file => file.Name, StringComparer.Ordinal).Skip(Kept))
            old.Delete();
    }

    /// <summary>What the switch question adds, naming both answers.</summary>
    public const string Question =
        "Carry the context over and the new agent is given a brief of this conversation — read from the " +
        "CLI's own transcript — as its first message. Without it, it starts knowing nothing of the work so far.";

    /// <summary>What the tile says when the context was asked for and nothing of it could be read.</summary>
    public const string NothingToCarry =
        "Nothing of the previous conversation could be read, so the agent started without it.";

    /// <summary>The brief, or null when the transcript holds nothing anybody said.</summary>
    /// <param name="from">What the outgoing agent is called, as its row in Settings names it.</param>
    /// <param name="turns">Its transcript, oldest first.</param>
    public static string? Write(string from, IReadOnlyList<TranscriptTurn> turns)
    {
        var firstAsk = turns.FirstOrDefault(turn => turn.FromUser);
        if (firstAsk is null) return null;

        var rest = turns.SkipWhile(turn => !ReferenceEquals(turn, firstAsk)).Skip(1).ToList();
        var kept = new List<string>();
        var used = 0;
        for (var i = rest.Count - 1; i >= 0; i--)
        {
            var entry = Entry(from, rest[i]);
            if (used + entry.Length > Budget) break;
            kept.Insert(0, entry);
            used += entry.Length;
        }
        var dropped = rest.Count - kept.Count;

        var brief = new StringBuilder();
        brief.Append($"# Handover from {from}\n\n");
        brief.Append($"You are taking over work that {from} was doing in this repository. What follows is what was ");
        brief.Append("said in that conversation, read from its own transcript. The working tree is as it was left: ");
        brief.Append("run `git status` and `git diff` to see what has been changed. Do not redo what is already ");
        brief.Append("done — read the end of the conversation to see where it stopped, then carry on from there, ");
        brief.Append("and ask if something is unclear.\n\n");
        brief.Append("## What was asked for\n\n");
        brief.Append(Capped(firstAsk.Text)).Append("\n\n");

        if (rest.Count > 0)
        {
            brief.Append("## The conversation\n\n");
            if (dropped > 0)
                brief.Append($"_{dropped} earlier message{(dropped == 1 ? " was" : "s were")} left out to keep this short._\n\n");
            foreach (var entry in kept) brief.Append(entry);
        }

        return brief.ToString().TrimEnd() + "\n";
    }

    /// <summary>The one line typed into the arriving agent, pointing it at the brief.</summary>
    /// <param name="relativePath">The brief's path, relative to the workspace, with forward slashes.</param>
    public static string Prompt(string relativePath) =>
        $"Read {relativePath} - it is a brief of work you are taking over from another agent. " +
        "Carry on from where it stopped.";

    private static string Entry(string from, TranscriptTurn turn) =>
        $"**{(turn.FromUser ? "User" : from)}:**\n\n{Capped(turn.Text)}\n\n";

    private static string Capped(string text) =>
        text.Length <= MessageCap ? text : text[..MessageCap] + " […]";
}
