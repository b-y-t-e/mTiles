namespace mTiles.Models;

/// <summary>
/// One commit the tool proposed at the end of a run: a conventional-commit type, a subject, and the
/// files it covers.
/// </summary>
/// <remarks>
/// <para>A proposal, not an instruction. The tool decides how to divide the work into commits, because
/// that is a judgement about meaning — which change is a feature and which is the chore that made it
/// possible — and nothing in this application can make it. What it may not decide is <b>which files</b>
/// are eligible: <c>GoalCommitter</c> holds every path in this list against the set it worked out from
/// the goal's own baseline, and drops anything else. A model that names a path outside that set is not
/// disobeying so much as guessing, and the cost of the guess would be the user's parallel work swept
/// into a commit that claims to be about something else.</para>
/// <para>Not persisted. It exists between the tool answering and the commits being made, and after that
/// what happened is in the transcript and in <c>git log</c> — which are both better records of it than
/// a copy in a goal file that could disagree with them.</para>
/// </remarks>
public sealed class GoalCommit
{
    /// <summary>
    /// The conventional-commit type — <c>feat</c>, <c>fix</c>, <c>chore</c>, <c>refactor</c>,
    /// <c>test</c>, <c>docs</c>.
    /// </summary>
    /// <remarks>
    /// Kept as the tool wrote it rather than matched against a closed list. The set is a convention
    /// with local dialects (<c>perf</c>, <c>build</c>, <c>ci</c>, <c>style</c>, and whatever a given
    /// project has settled on), and a tile that rewrote an unrecognised one to <c>chore</c> would be
    /// overruling the repository's own habits from the outside. It is only ever a prefix on a message.
    /// </remarks>
    public string Type { get; set; } = "chore";

    /// <summary>What the commit did, as the line after the prefix.</summary>
    public string Subject { get; set; } = "";

    /// <summary>
    /// The paths this commit covers, relative to the repository root.
    /// <para>Filtered before anything is run — see the remarks on this type.</para>
    /// </summary>
    public List<string> Files
    {
        get => _files;
        set => _files = value is null ? [] : [..value.Where(f => !string.IsNullOrWhiteSpace(f))];
    }
    private List<string> _files = [];

    /// <summary>The message as git will see it. Falls back to a type alone with no subject, because a
    /// commit with an empty message is refused and a poor message is not.</summary>
    public string Message =>
        Subject.Length == 0 ? $"{Type}: changes from a goal run" : $"{Type}: {Subject}";
}
