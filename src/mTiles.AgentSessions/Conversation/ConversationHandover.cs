using System.Text;
using mTiles.AgentSessions.Events;

namespace mTiles.AgentSessions.Conversation;

/// <summary>
/// What one assistant hands the next: a brief, folded out of what this application already recorded.
/// </summary>
/// <remarks>
/// <para><b>Nothing is asked of any CLI.</b> Every section below is already in
/// <see cref="ConversationState"/> or on disk, so a handover costs no turn, works against an agent that
/// has crashed — which is the usual reason somebody switches — and reads the same whichever agent is
/// leaving. A summary written by the outgoing agent is an <i>addition</i> to this, never the thing it
/// depends on.
/// </para>
/// <para><b>It does not pretend to be the same session.</b> The resume token belongs to the CLI that
/// issued it and the other side's context window is unreachable, so the brief says out loud that the work
/// was handed over and that the working tree is the shared state. An agent told it is continuing its own
/// conversation would answer from a memory it does not have.</para>
/// <para><b>Pure, and fitted to a budget given from outside.</b> The budget is a parameter because what a
/// prompt may cost is the caller's question — <c>CommandLineLength</c> and <c>AiProcessRunner.PromptBudget</c>
/// live in the application and this assembly cannot see them — and because a table test fits a brief to two
/// hundred characters to argue the rule rather than to a real command line.</para>
/// <para><b>What is dropped is said, never silently missing.</b> A brief that quietly loses the middle of
/// the work reads as a complete account of a smaller task, which is the one failure worse than a brief
/// that is too long.</para>
/// </remarks>
public static class ConversationHandover
{
    /// <summary>What a brief may cost when nobody says otherwise.</summary>
    /// <remarks>Characters rather than tokens: this assembly has no tokenizer, every agent counts
    /// differently, and the figure is a ceiling rather than a measurement. Roughly three thousand tokens of
    /// English — small beside every context window measured here, and large enough for a plan, a file list
    /// and the last exchange.</remarks>
    public const int DefaultBudget = 12_000;

    /// <summary>The most of the last assistant message worth carrying.</summary>
    /// <remarks>Where it stopped is the shape of the last answer, not its whole text: an agent that printed
    /// a file back would otherwise spend the entire budget saying so.</remarks>
    private const int LastAnswerCap = 2_000;

    /// <summary>The most files to name before saying how many more there were.</summary>
    private const int FileCap = 60;

    /// <summary>Folds a conversation into the brief its next agent is handed.</summary>
    /// <param name="state">The conversation as it stands, which is what the reducer already holds.</param>
    /// <param name="budget">The most characters the brief may take. What is dropped to reach it is the
    /// middle of the work, oldest first; the goal, the plan and where it stopped are what a handover exists
    /// to carry, so a brief can still come out over budget rather than lose one of them.</param>
    public static string Write(ConversationState state, int budget = DefaultBudget)
    {
        var messages = state.Timeline.OfType<MessageEntry>().ToList();
        var goal = messages.FirstOrDefault(m => m.Role == MessageRole.User);

        // Oldest first, and dropped from the front: the newest turns are the ones the next agent is standing
        // in. The goal is never among them — it is the one thing no summary may paraphrase away.
        var asked = messages
            .Where(m => m.Role == MessageRole.User && !ReferenceEquals(m, goal))
            .Select(m => "- " + OneLine(m.Text))
            .ToList();

        var decided = state.Timeline.OfType<QuestionsEntry>()
            .Where(round => round.Answers is { Count: > 0 })
            .SelectMany(Decisions)
            .ToList();

        var dropped = 0;
        while (true)
        {
            var brief = Assemble(state, goal, asked, decided, dropped);
            if (brief.Length <= budget || asked.Count == 0) return brief;

            asked.RemoveAt(0);
            dropped++;
        }
    }

    /// <summary>The brief a handover still owes the agent it was handed to, or null when none is owed.</summary>
    /// <remarks>
    /// <para><b>Read back out of the conversation rather than remembered.</b> The seam is in the store the
    /// moment the work moves, and the brief is on it, so the debt survives the tile being closed, the
    /// application being shut down and a send that threw — none of which a field on a view model does. A
    /// handover whose start never reached a live session would otherwise leave a transcript saying the work
    /// was handed over to an agent that was never told anything about it.</para>
    /// <para><b>What says it has been delivered is the agent having spoken.</b> The brief is sent unrecorded,
    /// so nothing of the user's stands after the seam — what follows it is whatever the arriving agent did
    /// with it. A notice is the one thing that does not count: a launch that failed appends one, and read as
    /// delivery it would spend the brief on a session that never started.</para>
    /// </remarks>
    public static string? BriefOwedIn(ConversationState state)
    {
        for (var i = state.Timeline.Count - 1; i >= 0; i--)
            switch (state.Timeline[i])
            {
                case HandoverEntry handover: return handover.Brief is { Length: > 0 } brief ? brief : null;
                case NoticeEntry: continue;
                default: return null;
            }

        return null;
    }

    private static string Assemble(ConversationState state, MessageEntry? goal, List<string> asked,
        List<string> decided, int dropped)
    {
        var brief = new StringBuilder();
        brief.Append(Preamble);

        if (goal is not null)
        {
            brief.Append("\n## What was asked for\n\n");
            brief.Append(Quoted(goal.Text)).Append('\n');
        }

        if (asked.Count > 0)
        {
            brief.Append("\n## What was asked for after that\n\n");
            foreach (var line in asked) brief.Append(line).Append('\n');
        }

        if (dropped > 0)
            brief.Append('\n')
                .Append($"_{dropped} earlier {(dropped == 1 ? "message was" : "messages were")} left out of this brief to fit._\n");

        if (decided.Count > 0)
        {
            brief.Append("\n## What was decided\n\n");
            foreach (var line in decided) brief.Append(line).Append('\n');
        }

        if (state.Plan is { Steps.Count: > 0 } plan)
        {
            brief.Append("\n## The plan as it stands\n\n");
            if (plan.Explanation is { Length: > 0 } why) brief.Append(OneLine(why)).Append("\n\n");
            foreach (var step in plan.Steps) brief.Append(Step(step)).Append('\n');
        }

        AppendFiles(brief, state);
        AppendWhereItStopped(brief, state);
        brief.Append(Closing);
        return brief.ToString();
    }

    /// <summary>What the brief says about itself, before anything of the work.</summary>
    /// <remarks><b>It says "this is not a task" first, and it has to.</b> A brief that opens by describing
    /// a piece of unfinished work reads as an instruction to finish it, and every agent measured did
    /// exactly that: arriving on a handover, it went straight to running commands and proposing the next
    /// commit before anybody had asked it for anything. The user is handing the work over so that they can
    /// then say what to do with it, which is a different thing from asking for it to be carried on.
    /// </remarks>
    private const string Preamble = """
        # Handover

        **This is context, not a request. Do not do any work in response to it.**

        The work described below was begun with another assistant, in a session you cannot see, and has
        been handed to you. This brief and the working tree are everything there is: that assistant's
        reasoning, its reads of the tree and whatever it was in the middle of are gone.

        """;

    /// <summary>The last thing the brief says, which is what to do with it.</summary>
    /// <remarks><b>At the end because that is where an instruction is obeyed.</b> The preamble says the
    /// same thing, and saying it twice is deliberate: what sits between the two is a description of
    /// unfinished work, which is the most instruction-shaped thing a model can be handed. The example
    /// answer is there because "acknowledge" on its own was answered with a summary of the brief, which is
    /// a turn spent saying back what the user has just read.</remarks>
    private const string Closing = """

        ## What to do now

        Nothing. Do not run commands, read files, change anything or propose a next step — the work has
        only been handed to you, not asked for. Answer with one short line saying you have the context and
        are ready, in the language the messages above are written in — "Context loaded, ready to work." —
        and then wait for what is asked next.

        """;

    /// <summary>
    /// What the turns of this conversation changed on disk, in the order they were first touched.
    /// </summary>
    /// <remarks>A turn whose files were put back is left out: its changes are not in the tree, and named
    /// here they would send the next agent looking for edits that no longer exist.</remarks>
    private static void AppendFiles(StringBuilder brief, ConversationState state)
    {
        var changes = new Dictionary<string, (FileChangeKind Kind, int Additions, int Deletions)>(
            StringComparer.Ordinal);
        foreach (var turn in state.Timeline.OfType<CheckpointEntry>().Where(entry => !entry.Restored))
            foreach (var file in turn.Files)
                changes[file.Path] = changes.TryGetValue(file.Path, out var running)
                    // The first thing that happened to a file is what it is: created and then edited twice
                    // is still a new file to whoever reads the tree now.
                    ? (running.Kind, running.Additions + file.Additions, running.Deletions + file.Deletions)
                    : (file.Kind, file.Additions, file.Deletions);

        if (changes.Count == 0) return;

        brief.Append("\n## What has changed on disk\n\n");
        brief.Append("The previous assistant's edits, already in the working tree:\n\n");
        foreach (var (path, change) in changes.Take(FileCap))
            brief.Append($"- `{path}` — {Kind(change.Kind)}, +{change.Additions}/-{change.Deletions}\n");

        if (changes.Count > FileCap) brief.Append($"- _and {changes.Count - FileCap} more files_\n");
    }

    private static void AppendWhereItStopped(StringBuilder brief, ConversationState state)
    {
        var last = state.Timeline.LastOrDefault(entry =>
            entry is MessageEntry { Role: MessageRole.Assistant } or NoticeEntry { Level: NoticeLevel.Error });
        if (last is null) return;

        brief.Append("\n## Where it stopped\n\n");
        brief.Append(last switch
        {
            MessageEntry message => Quoted(Capped(message.Text, LastAnswerCap)),
            NoticeEntry notice => "The session ended with an error: " + OneLine(notice.Text),
            _ => "",
        });
        brief.Append('\n');
    }

    private static IEnumerable<string> Decisions(QuestionsEntry round) =>
        round.Questions
            .Where(question => round.Answers!.ContainsKey(question.Id))
            .Select(question =>
                $"- {OneLine(question.Text)} -> **{string.Join(", ", round.Answers![question.Id])}**");

    private static string Step(PlanStep step) => step.Status switch
    {
        PlanStepStatus.Completed => $"- [x] {OneLine(step.Text)}",
        PlanStepStatus.InProgress => $"- [ ] {OneLine(step.Text)} _(in progress when the work was handed over)_",
        _ => $"- [ ] {OneLine(step.Text)}",
    };

    private static string Kind(FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added => "added",
        FileChangeKind.Deleted => "deleted",
        FileChangeKind.Renamed => "renamed",
        _ => "modified",
    };

    /// <summary>Somebody's own words, as a block that cannot end the brief's structure.</summary>
    /// <remarks>A message can carry a <c>#</c> at the start of a line, or a fence, and the brief is read as
    /// Markdown by whatever it is handed to: a heading of theirs landing among ours turns their words into
    /// our instructions. Quoting costs two characters a line and closes that.</remarks>
    private static string Quoted(string text)
    {
        var trimmed = text.Replace("\r\n", "\n").Trim();
        return trimmed.Length == 0 ? trimmed : "> " + trimmed.Replace("\n", "\n> ");
    }

    /// <summary>The same words on one line, for a list item — where a blockquote cannot go.</summary>
    private static string OneLine(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Capped(string text, int cap) =>
        text.Length <= cap ? text : text[..cap].TrimEnd() + "\n\n...(the rest of that answer was left out)";
}
