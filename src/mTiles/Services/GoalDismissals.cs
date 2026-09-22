using System.Text;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// What a goal has been told not to fix, and what follows from that.
/// </summary>
/// <remarks>
/// <para>A finding unticked at the review gate is a decision about the <em>goal</em>, not about the one
/// review it was made in: the reviewer is run again from scratch on the next lap and has no memory of
/// what anybody unticked. So a dismissal has to survive its own review, and it has to do so twice over
/// — once here, where it is subtracted from what the run is judged against, and once in the review
/// prompt, where the reviewer is asked not to raise it again.</para>
/// <para><b>Matching by text is the weak half and is treated as such.</b> <see cref="Contains"/>
/// catches a reviewer that writes the same finding in the same words, the cheap case and not the usual
/// one —
/// prose about code is rewritten on every run. What actually carries a dismissal across laps is the
/// block in the review prompt: the model reading it is the only thing here that can tell "the null
/// check on line 762" from "this can dereference null" and decide they are one finding. The identity
/// here is a literal-repeat guard under that, not the mechanism.</para>
/// </remarks>
internal static class GoalDismissals
{
    /// <summary>Whether this finding is one the user has said is not to be fixed.</summary>
    /// <remarks>By <see cref="GoalFinding.Defect"/>, which leaves the line out, and that is the whole
    /// point of the match rather than a shortcut in it: a dismissal outlives the review it was made in
    /// and is compared against the findings of every later one, with an implementation in between
    /// moving the lines under every defect it did not fix. The reasoning, and what the looser identity
    /// costs, are in the remarks on that property.</remarks>
    public static bool Contains(IEnumerable<GoalFinding> dismissed, GoalFinding finding) =>
        dismissed.Any(d => d.Defect == finding.Defect);

    /// <summary>
    /// The review as it stands once what the user has dismissed is taken out of it.
    /// </summary>
    /// <remarks>
    /// <para>The one place the subtraction happens, and everything that judges a review is handed the
    /// result of it: the completion criteria, the sentence saying why they were not met, the feedback
    /// that goes back to the tool, and the fingerprint the no-progress stop compares. Written twice
    /// they would disagree, and the shape of that disagreement is a run that can never finish — a
    /// dismissed error kept out of the prompt, so the tool never touches it, and counted by the
    /// criteria, so the goal is refused for it on every lap until the budget is gone.</para>
    /// <para><b>The findings and nothing else.</b> <c>goalMet</c> is the reviewer's answer to a
    /// different question and the review prompt says so in as many words — it is asked to judge the
    /// two separately — so a <c>goalMet:false</c> can rest on something no finding states: half a
    /// feature, a case the goal named and the diff does not cover. There is no tick beside that, and
    /// there must not be one reached from here: carrying the verdict over with the findings is how a
    /// warning unticked about a variable name finishes a goal whose own review said the work was half
    /// done, and — with <c>CommitWhenDone</c> — commits it. A run whose every stated defect is
    /// dismissed and whose verdict still says no therefore stops as going round in a circle, which is
    /// the right answer: the reviewer is saying something the list never said.</para>
    /// <para>A copy, never the review itself: the original is what the transcript draws, and a reader
    /// has to be able to see what was dismissed as well as what was not.</para>
    /// </remarks>
    public static GoalReviewResult Accepted(GoalReviewResult review, IReadOnlyList<GoalFinding> dismissed)
    {
        if (dismissed.Count == 0) return review;

        return new GoalReviewResult
        {
            GoalMet = review.GoalMet,
            WasStructured = review.WasStructured,
            SaidNothingAboutTheGoal = review.SaidNothingAboutTheGoal,
            RawText = review.RawText,
            Findings = [..review.Findings.Where(f => !Contains(dismissed, f))],
        };
    }

    /// <summary>
    /// What the reviewer is told about the findings it is not to raise again, or empty when there are
    /// none.
    /// </summary>
    /// <remarks>
    /// <para>Written as the user's own decision rather than as a rule about the code, because those are
    /// different instructions and only one of them is true: the defect may well be real, and a reviewer
    /// told it is not will start arguing with the file in front of it. What it is told is that somebody
    /// looked at it and said no.</para>
    /// <para>Severity, place and title, and the detail left out. The detail is the longest part of a
    /// finding and the least useful for recognising one again; a run that dismisses a dozen would
    /// otherwise spend a page of the prompt on the things it is <em>not</em> asking about.</para>
    /// </remarks>
    public static string PromptBlock(IReadOnlyList<GoalFinding> dismissed)
    {
        if (dismissed.Count == 0) return "";

        var sb = new StringBuilder();
        foreach (var f in dismissed)
        {
            sb.Append("- ").Append(f.Severity.ToString().ToLowerInvariant());
            if (f.File.Length > 0) sb.Append(' ').Append(f.File);
            if (f.Line is { } line) sb.Append(':').Append(line);
            sb.Append(": ").Append(f.Title).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }
}
