namespace mTiles.Models;

/// <summary>
/// What a review said, once it has been read rather than searched.
/// <para>Reviews used to be one string, and the only thing ever asked of it was whether it contained
/// the characters <c>VERDICT: PASS</c> anywhere. Three things followed from that: "I cannot say
/// VERDICT: PASS until the null check is fixed" passed, "VERDICT: PASSED" failed, and the whole review
/// — nits included — went back into the next implement prompt as one undifferentiated blob.</para>
/// </summary>
public sealed class GoalReviewResult : IGoalParsedBlock
{
    /// <summary>Whether the change does what the goal asked for. Separate from the findings on
    /// purpose: a clean implementation of the wrong thing has no findings and is not done.</summary>
    public bool GoalMet { get; set; }

    public List<GoalFinding> Findings
    {
        get => _findings;
        set => _findings = value ?? [];
    }
    private List<GoalFinding> _findings = [];

    /// <summary>
    /// False when the tool answered in prose and the structure had to be guessed.
    /// <para>Not a failure. The JSON block is a request, not a protocol the tool signed up to, and a
    /// model that ignores it must still be able to finish a goal — so an unstructured review falls back
    /// to the old <c>VERDICT: PASS</c> rule and to an empty finding list, which is exactly how this tile
    /// behaved before any of this existed.</para>
    /// </summary>
    public bool WasStructured { get; set; }

    /// <summary>
    /// The tool sent findings but never said whether the goal was reached.
    /// <para>Worth surfacing rather than absorbing. With <c>RequireGoalMet</c> on — the default — a
    /// review that omits the flag can never finish the goal, however clean it is, and the run then
    /// spends its whole budget on a criterion the tool has not addressed. Saying so once gives the user
    /// the two ways out: turn the requirement off, or use a tool that answers the question.</para>
    /// </summary>
    public bool SaidNothingAboutTheGoal { get; set; }

    /// <summary>The review as the tool wrote it. Kept whole: it is what goes in the transcript when
    /// there was no structure to render, and what the next implement prompt falls back to.</summary>
    public string RawText
    {
        get => _rawText;
        set => _rawText = value ?? "";
    }
    private string _rawText = "";

    public int Count(GoalSeverity severity) => Findings.Count(f => f.Severity == severity);

    /// <summary>
    /// What this review found, reduced to something two reviews can be compared by.
    /// <para>Severity, file and title — not the detail, which is prose and differs on every run for the
    /// same defect. Two consecutive reviews with the same fingerprint mean the implementation is going
    /// round in a circle, and the remaining attempts will be spent proving it.</para>
    /// </summary>
    public string Fingerprint() =>
        Findings.Count == 0
            ? $"clean:{GoalMet}"
            : string.Join("|", Findings
                .Select(f => $"{f.Severity}:{f.File}:{f.Title}".ToLowerInvariant())
                .OrderBy(x => x, StringComparer.Ordinal));
}
