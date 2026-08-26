using System.Text.RegularExpressions;
using mTiles.Models;

namespace mTiles.Services;

public sealed partial class GoalWorkflowEngine
{
    // Read through a function, not handed over: Criteria is replaced wholesale by every edit in the
    // panel, so a builder given today's instance would hold yesterday's switches for the life of the
    // tile.
    private readonly GoalPromptBuilder _promptBuilder;

    public GoalWorkflowEngine() => _promptBuilder = new GoalPromptBuilder(() => Criteria.Solid);

    /// <summary>
    /// How many times the tool may be asked to clarify before the tile plans anyway.
    /// <para>Not configurable, and not for want of a place to put it: a model that keeps finding one
    /// more thing to ask about is not a setting the user should have to discover and turn down. Three
    /// rounds is more than any goal has needed; past that the answer is to plan with what there is,
    /// which the user can then reject in the usual way.</para>
    /// </summary>
    public const int MaxClarifyRounds = 3;

    public string OriginalGoal { get; set; } = "";

    /// <summary>The questions waiting for an answer, in the order they were asked. Empty whenever the
    /// tile is not waiting on the user — see <c>GoalTileViewModel.ShowQuestions</c>.</summary>
    public List<GoalQuestion> PendingQuestions { get; private set; } = [];

    /// <summary>Replaces the pending set, which is the only way it ever changes: a clarification round
    /// asks a new set or none, and a partial update has no meaning.</summary>
    public void SetPendingQuestions(IEnumerable<GoalQuestion>? questions) =>
        PendingQuestions = questions is null ? [] : [..questions];
    public List<string> ClarificationHistory { get; } = [];
    public string ApprovedPlan { get; set; } = "";
    public string? LastReviewFeedback { get; set; }

    /// <summary>
    /// What the verify command printed the last time it failed, or null.
    /// </summary>
    /// <remarks>
    /// It used to go to the review alone. The reviewer then wrote its opinion of the failure into the
    /// findings, and that opinion — not the compiler's line and column — was all the next
    /// implementation ever saw. Cleared as soon as the command passes, so a fixed build does not haunt
    /// the attempts after it.
    /// </remarks>
    public string? LastVerifyOutput { get; set; }

    /// <summary>
    /// One line per earlier attempt: what it changed and what it decided against.
    /// </summary>
    /// <remarks>
    /// <para>Structured note-taking rather than a persistent tool session, and the reasons are the ones
    /// already written down elsewhere in this file: only two of the four tools can resume a session at
    /// all (see <c>OpenCodeSession</c>), a window carrying five to twenty full diffs recalls worse rather
    /// than better, and a session living in a tool's own database is a session that does not survive what
    /// <c>GoalStateStore</c> exists to survive.</para>
    /// <para>What is actually lost without it is not the code — that is on disk and in every prompt —
    /// but the reasoning: attempt 2 rediscovering the dead end attempt 1 backed out of, and the review
    /// asking for X→Y on one lap and Y→X on the next, which <see cref="LastReviewFingerprint"/> cannot
    /// catch because it only compares consecutive reviews and an oscillation produces two different
    /// ones.</para>
    /// <para>Capped at five entries and trimmed per entry: this is a note, not a transcript. It is
    /// <em>not</em> the first thing dropped when a prompt will not fit, though it was at first — see
    /// <c>GoalPromptBuilder.ComposeImplement</c>: the working tree gives way ahead of it, because the
    /// tool can run <c>git diff</c> for itself and nothing can recover a note about a path an earlier
    /// attempt tried and abandoned.</para>
    /// </remarks>
    public List<string> AttemptLog { get; } = [];

    /// <summary>How many attempts are kept. Five, because that is the default budget: a whole run's
    /// worth, and no more.</summary>
    public const int MaxAttemptLog = 5;

    /// <summary>How much of one attempt's answer is kept. It is a note, not a transcript — and with
    /// the two asked-for lines extracted rather than the first 300 characters taken, it is two sentences
    /// with room for the reason in the second.</summary>
    private const int MaxAttemptNote = 400;

    /// <summary>The line the implement prompt asks every attempt to finish with.</summary>
    private const string RejectedPrefix = "Rejected:";

    /// <summary>Files what an attempt said it did. Blank answers are not filed: an entry saying nothing
    /// costs prompt budget and teaches nothing.</summary>
    public void RecordAttempt(int attempt, string? answer)
    {
        var note = Note(answer ?? "");
        if (note.Length == 0) return;

        AttemptLog.Add($"Attempt {attempt}: {note}");

        // The oldest goes, not the newest: what the last attempt decided is what the next one needs.
        while (AttemptLog.Count > MaxAttemptLog)
            AttemptLog.RemoveAt(0);
    }

    /// <summary>
    /// The two lines the implement prompt asks for: what changed, and what was rejected.
    /// </summary>
    /// <remarks>
    /// <para>This used to take the first 300 characters of the answer, which is the opposite end from
    /// the one the prompt asks about. The instruction is "<em>finish</em> with one line saying what you
    /// changed, then one line starting Rejected:", and an agent's answer is rarely under 300 characters,
    /// so what was filed was the preamble — "I'll start by reading Cart.cs" — and the two lines the
    /// whole mechanism exists for were cut off every single time. The notes recorded everything except
    /// what was asked for.</para>
    /// <para>The tests did not see it because they fed short strings and one long run of a single
    /// repeated character, where the head and the tail are indistinguishable.</para>
    /// <para>Falls back to the <b>tail</b>, never the head: an answer without the asked-for lines still
    /// ends nearer to what it did than it begins.</para>
    /// </remarks>
    private static string Note(string answer)
    {
        var lines = answer.ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0) return "";

        var rejectedAt = lines.FindLastIndex(
            l => l.StartsWith(RejectedPrefix, StringComparison.OrdinalIgnoreCase));

        // What changed is the line before the rejection, or simply the last line where there is none.
        var changedAt = rejectedAt > 0 ? rejectedAt - 1 : rejectedAt < 0 ? lines.Count - 1 : -1;

        var kept = new List<string>(2);
        if (changedAt >= 0) kept.Add(lines[changedAt]);
        if (rejectedAt >= 0) kept.Add(lines[rejectedAt]);

        // Never empty: the early return covers a blank answer, and every path above puts at least one
        // line in. The fallback that used to sit here for "nothing usable was found" could not run.
        var note = string.Join(" ", kept);

        // Still too long: keep the end, which is the half the prompt asked about.
        if (note.Length > MaxAttemptNote)
            note = "…" + note[^MaxAttemptNote..].TrimStart();

        return note;
    }

    public int IterationCount { get; set; }

    /// <summary>Why the last run of the loop stopped, or null where none has finished in this goal yet.
    /// Read by the Summary to decide whether Continue has anything to offer, and persisted so that the
    /// answer survives a restart.</summary>
    public GoalStopReason? LastStopReason { get; set; }

    /// <summary>
    /// What the attempts field said before Continue raised it, or null where Continue has not been used
    /// on this goal.
    /// </summary>
    /// <remarks>
    /// Criteria are deliberately not reset by <see cref="StartNewGoal"/> — they are how this tile
    /// works, not part of the goal being worked on. That is right for a number the user typed and wrong
    /// for one a button wrote: continuing a goal twice left the next goal in that tile starting with a
    /// ceiling of twenty that nobody had chosen. The field keeps telling the truth about the run in
    /// front of it, and the value the user set comes back with the next goal.
    /// </remarks>
    public int? AttemptsBeforeExtension { get; set; }
    public GoalPhase CurrentPhase { get; set; } = GoalPhase.Goal;
    public bool IsPaused { get; set; }

    /// <summary>
    /// What the user set on the tile. Never null, and never zero attempts: a criteria object that says
    /// zero would put a goal into Summary the moment its plan was approved, having done nothing.
    /// </summary>
    public GoalCompletionCriteria Criteria
    {
        get => _criteria;
        set => _criteria = value ?? new GoalCompletionCriteria();
    }
    private GoalCompletionCriteria _criteria = new();

    /// <summary>
    /// Attempts this run may take. Bounded away from the field it comes from: the panel shows what the
    /// user typed, and a file can say anything at all.
    /// <para>Through <see cref="GoalCompletionPolicy.Attempts"/> rather than clamping here, so the loop
    /// and the sentence that summarises it cannot disagree — the summary read the raw number and
    /// reported "stopped after 999 attempts" over a run of fifty.</para>
    /// </summary>
    public int MaxIter => GoalCompletionPolicy.Attempts(Criteria);

    /// <summary>Clarification rounds already spent on this goal. Reset with the goal, not with the
    /// tile, and persisted, so a restart does not renew them.</summary>
    public int ClarifyRounds { get; set; }

    /// <summary>What the previous review found, reduced to something comparable — see
    /// <see cref="GoalReviewResult.Fingerprint"/>. Null before the first review of this goal.</summary>
    public string? LastReviewFingerprint { get; set; }

    /// <summary>What the last review counted, per <see cref="GoalSeverity"/> in order. Empty before the
    /// first review of this goal.</summary>
    public int[] LastReviewCounts { get; set; } = [];

    /// <param name="budget">How many characters the chosen tool can be handed on a command line, or
    /// null when there is no such limit — see <see cref="AiProcessRunner.PromptBudget"/>. Passed all the
    /// way down rather than looked up in the builder, which is pure and knows nothing about tools.</param>
    public string BuildClarifyPrompt(int? budget = null) =>
        _promptBuilder.BuildClarify(OriginalGoal, ClarificationHistory, budget);

    /// <inheritdoc cref="BuildClarifyPrompt"/>
    public string BuildPlanPrompt(int? budget = null) =>
        _promptBuilder.BuildPlan(OriginalGoal, ClarificationHistory, budget);

    /// <inheritdoc cref="BuildClarifyPrompt"/>
    public string BuildImplementPrompt(string? gitDiff, int? budget = null) =>
        _promptBuilder.BuildImplement(
            new GoalPromptBuilder.ImplementContext(
                OriginalGoal,
                ApprovedPlan,
                LastReviewFeedback,
                LastVerifyOutput,
                gitDiff,
                AttemptLog,
                IterationCount,
                MaxIter),
            budget);

    /// <inheritdoc cref="BuildClarifyPrompt"/>
    public string BuildReviewPrompt(string? gitDiff, string? verifyOutput = null, int? budget = null) =>
        _promptBuilder.BuildReview(OriginalGoal, gitDiff, verifyOutput, budget);

    /// <inheritdoc cref="BuildClarifyPrompt"/>
    public string BuildDetectGoalPrompt(string gitDiff, int? budget = null) =>
        _promptBuilder.BuildDetectGoal(gitDiff, budget);

    public void StartNewGoal(string goal)
    {
        OriginalGoal = goal;
        ClarificationHistory.Clear();
        ApprovedPlan = "";
        ProposedPlan = null;
        LastReviewFeedback = null;
        LastVerifyOutput = null;
        LastReviewFingerprint = null;
        LastReviewCounts = [];
        AttemptLog.Clear();
        IterationCount = 0;
        LastStopReason = null;
        ClarifyRounds = 0;
        PendingQuestions = [];
        IsPaused = false;
        CurrentPhase = GoalPhase.Goal;

        // Criteria are deliberately not reset: they are how this tile works, not part of the goal
        // being worked on, and a user who set a verify command once should not have to set it again
        // for the next goal in the same tile.
        //
        // With one exception, and it is the exception that proves the rule: the attempts Continue added
        // were not chosen by hand. Left in place they were inherited by the next goal in this tile — and
        // by the one after that, doubling each time somebody continued twice.
        if (AttemptsBeforeExtension is { } chosen)
            Criteria.MaxIterations = chosen;
        AttemptsBeforeExtension = null;
    }

    /// <summary>
    /// Files one turn of the clarification conversation, labelled with who said it.
    /// <para>The labels are the point. This list is joined into the next Clarify prompt and into the
    /// Plan prompt, and it used to hold the user's answers <em>alone</em> — which was survivable while
    /// the questions were prose and the answers were prose, and stopped being survivable the moment
    /// answers became numbered: the next round was handed <c>"1. appsettings.json"</c> with no way of
    /// knowing what question 1 had been. The numbering exists to tie an answer to its question, and
    /// dropping the questions untied it again.</para>
    /// </summary>
    public void RecordClarification(string text, bool fromUser = true) =>
        ClarificationHistory.Add(Label(fromUser) + text);

    private static string Label(bool fromUser) => fromUser ? "User: " : "Tool asked: ";

    private static string Labelled(string turn) =>
        turn.StartsWith(Label(true), StringComparison.Ordinal)
        || turn.StartsWith(Label(false), StringComparison.Ordinal)
            ? turn
            : Label(true) + turn;

    /// <summary>The plan as the tool last proposed it, waiting to be approved or argued with.</summary>
    public string? ProposedPlan { get; set; }

    /// <summary>
    /// Remembers what the tool has just proposed, or forgets the last one when a new planning run
    /// begins.
    /// <para>Forgetting is the half that matters. Rejecting a plan sends the tile back to Clarify and
    /// then forward to Plan again; without clearing, a second planning run that produced nothing left
    /// the <em>rejected</em> plan standing, and "ok" approved the plan the user had just turned down.
    /// </para>
    /// </summary>
    public void RecordProposedPlan(string? planText) => ProposedPlan = planText;

    /// <summary>
    /// Adopts the plan the tool proposed, or answers false when there is not one.
    /// <para>It used to be dug out of the transcript — the last assistant message, whatever that was.
    /// Once an empty or failed run could leave the Plan phase paused with no answer in it, typing "ok"
    /// approved the <em>clarifying questions</em> as the plan, or approved an empty string and started
    /// implementing with no plan at all, in silence.</para>
    /// </summary>
    public bool ApprovePlan()
    {
        if (ProposedPlan is not { Length: > 0 }) return false;

        ApprovedPlan = ProposedPlan;
        LastReviewFeedback = null;

        // The findings of the *previous* plan's reviews go with it. Left standing, the first review of
        // the new plan could match the last review of the old one — easily, since a rejected plan and
        // its replacement are usually about the same defect — and the no-progress stop would end the
        // run after a single attempt, reporting that two reviews had agreed when only one had happened.
        LastReviewFingerprint = null;
        LastReviewCounts = [];

        IterationCount = 0;
        return true;
    }

    public void RecordReviewFeedback(string feedback) =>
        LastReviewFeedback = feedback;

    public void ClearReviewFeedback() =>
        LastReviewFeedback = null;

    /// <summary>
    /// Whether a review written in prose says the goal was reached.
    /// <para>Read from the <b>last line that mentions a verdict</b>, not from anywhere in the text. The
    /// substring rule is the thing this whole feature was built to replace — "I cannot say VERDICT:
    /// PASS until the null check is fixed" passed it — and it survived here as the prose fallback,
    /// where it matters more than it did before: the review prompt now asks for exactly this line, so
    /// tools quote it, discuss it, and explain when they are not going to write it.</para>
    /// <para>The last one wins, because a reply that reasons its way to a verdict states it at the end,
    /// and anything before that is the reasoning.</para>
    /// </summary>
    public static bool IsVerdictPass(string reviewResponse)
    {
        foreach (var line in reviewResponse.ReplaceLineEndings("\n").Split('\n').Reverse())
        {
            // Markdown emphasis stripped first: a tool told to write a line gives it a heading or bold
            // just as readily, and "**VERDICT:** PASS" means what it says.
            var cleaned = VerdictNoise().Replace(line, " ").Trim();
            if (cleaned.Length == 0) continue;

            if (VerdictLine().Match(cleaned) is { Success: true } m)
            {
                // "I cannot give a VERDICT: PASS" ends in the verdict and is the opposite of one. The
                // end anchor was chosen to tell a stated verdict from a discussed one, and it does —
                // right up to the sentence whose discussion *finishes* on the words. This is reachable
                // rather than theoretical: the prompt now asks for exactly this line, so a tool that has
                // decided not to write it says so in those words.
                //
                // False rather than carrying on to earlier lines: a tool explaining that it will not
                // give a verdict has given its last word, and everything above it is the reasoning.
                if (RefusedVerdict().IsMatch(LastClause(cleaned[..m.Index]))) return false;

                return m.Groups["outcome"].Value.StartsWith("pass", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Nothing said. Not met — the one thing this must never do is call an unexamined review a pass.
        return false;
    }

    /// <summary>Markdown and decoration that carries no meaning here.</summary>
    [GeneratedRegex(@"[*_`#>\[\]]+")]
    private static partial Regex VerdictNoise();

    /// <summary>
    /// A verdict as tools actually write it, anchored to the <b>end</b> of the line.
    /// <para>The end is where a verdict goes, and that is what tells "Everything checks out. VERDICT:
    /// PASS" from "I cannot say VERDICT: PASS until the null check is fixed" — the second of which is
    /// the sentence this whole feature was built to stop passing, and which the old substring rule
    /// accepted.</para>
    /// <para>The word itself is matched loosely on purpose: <c>PASSED</c>, a dash instead of a colon,
    /// any amount of space. Tightening that bought nothing and cost the two spellings tools use most —
    /// this is a fallback for tools that already ignored one instruction, so it cannot afford to be
    /// pedantic about the next.</para>
    /// </summary>
    /// <para>What may follow the word is anything that is not a letter or a digit, not just a full stop:
    /// models end that line with a tick, a party popper or three exclamation marks about as often as with
    /// nothing at all, and <c>[.!]*</c> rejected every one of them. A rejected verdict is read as "no
    /// verdict", which is read as "not met" — so a tool that decorates its answers failed every attempt
    /// and burned the whole budget. Letters are still not allowed after it, which is what keeps
    /// "VERDICT: PASS until the null check is fixed" out.</para>
    /// <para>Written as a negated class rather than <c>[\s\p{P}\p{S}]</c>, which was the obvious spelling
    /// and admitted ✅ while rejecting 🎉. .NET regexes match UTF-16 code units, so a character outside
    /// the basic plane arrives as two surrogates whose category is <c>Cs</c> and not <c>So</c> — the same
    /// trap that had <c>CommandDisplay</c> mangling every emoji until it counted runes instead of
    /// chars.</para>
    [GeneratedRegex(@"\bverdict\s*[:\-]?\s*(?<outcome>pass\w*|fail\w*)[^\p{L}\p{N}]*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex VerdictLine();

    /// <summary>
    /// Somebody declining to state a verdict, in the words they decline in.
    /// </summary>
    /// <remarks>
    /// <para>Narrow twice over, and both narrowings are load-bearing. The cues negate the <em>act of
    /// stating</em> rather than the findings, and <c>don't</c> only counts when a verb of stating
    /// follows it: an unqualified one matched "The null check doesn't matter here. VERDICT: PASS",
    /// which is an ordinary review.</para>
    /// <para>And only the <b>last clause</b> is searched — see <see cref="LastClause"/>. Anywhere in
    /// the line was far too much: everything a review says before the full stop is about the code, and
    /// a review that mentions what the code cannot do is the normal case, not the exception.</para>
    /// <para>Both exist because of what a false refusal costs. It is not one attempt: a tool phrases
    /// its reviews the same way every time, so a misread turns every attempt into a failure and burns
    /// the whole budget without the goal ever being judged. Against that, a false <em>pass</em> ends
    /// the goal over work nobody approved — so the rule stays tight on both sides rather than being
    /// tuned towards either.</para>
    /// </remarks>
    [GeneratedRegex(@"\b(?:cannot|can ?not|can't|won'?t|will not|unable|refus\w*|(?:do|does|did)n'?t\s+(?:give|say|issue|write|provide|think|believe)|not\s+(?:give|say|issue|write|provide))\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex RefusedVerdict();

    /// <summary>
    /// What is left of a line after its last clause break — the clause the verdict is actually part of.
    /// </summary>
    /// <remarks>
    /// <para>Commas and dashes count, not only sentence endings, and that was the whole of a real gap:
    /// "I can't find anything wrong, so VERDICT: PASS" and "Nothing that the parser cannot handle —
    /// VERDICT: PASS" have no full stop in them at all, so the refusal cue was read out of a clause that
    /// was about the <em>code</em>. Those are ordinary review sentences, and a tool that writes them
    /// fails every attempt and burns the whole budget — the cost this rule is written to avoid.</para>
    /// <para>It does widen the door: "I can't, VERDICT: PASS" now passes. That sentence is contrived,
    /// and the four above are not.</para>
    /// </remarks>
    private static string LastClause(string beforeVerdict)
    {
        var lastBreak = beforeVerdict.LastIndexOfAny(['.', '!', '?', ';', ',', '—', '–']);
        return lastBreak < 0 ? beforeVerdict : beforeVerdict[(lastBreak + 1)..];
    }

    public static bool IsApproval(string text)
    {
        var normalized = text.Trim().TrimEnd('.', '!').ToLowerInvariant();
        return normalized is "ok" or "okay" or "yes" or "tak" or "go" or "approve"
            or "approved" or "start" or "do it" or "lgtm" or "ship it" or "proceed";
    }

    /// <summary>
    /// The phases in which the tool is doing the work rather than waiting for the user — the ones a
    /// tile can be interrupted in the middle of, and the ones Resume knows how to pick up.
    /// </summary>
    public static bool IsMidRun(GoalPhase phase) => phase is GoalPhase.Implement or GoalPhase.Review;

    /// <summary>
    /// Whether a saved state was written while the tool was working, and so came back to a tile with
    /// nothing running in it.
    /// <para>The phase alone cannot answer this for Clarify and Plan, because one value covers both
    /// <em>asking</em> the tool and <em>waiting</em> for the user's answer: adding them to
    /// <see cref="IsMidRun"/> would have every tile left waiting for an answer come back claiming to be
    /// interrupted, and Resume would ask the same questions again.</para>
    /// <para>The <b>conversation</b> tells them apart — and only the conversation. The tile's own notes
    /// are skipped, because they are asides about the tile rather than turns in the exchange: a note
    /// saying the answer was blank, or that the saved tool is gone, or what the verify command is, can
    /// land last at any moment and says nothing about whose turn it is. Counting them was worse than it
    /// sounds, because <c>LoadState</c> writes some of them on every load: each restart appended
    /// another, each appended one made the next restart read an interrupted Clarify, and Resume spent a
    /// clarification round on it — a tile left alone long enough talked itself out of its own budget.
    /// </para>
    /// <para>What is left is the exchange, and the rule over it is simply: a run that was cut off leaves
    /// the <em>user's</em> message as the last thing either party said. An assistant message last means
    /// the answer arrived and the tile is waiting, which is not an interruption. Nothing at all from
    /// either party — reachable only from a damaged file — is treated as interrupted, since there is
    /// certainly no answer in it.</para>
    /// </summary>
    public static bool WasInterrupted(GoalTileState state)
    {
        if (IsMidRun(state.CurrentPhase)) return true;
        if (state.CurrentPhase is not (GoalPhase.Clarify or GoalPhase.Plan)) return false;

        // Questions on screen are a tile waiting on the user, not a run that was cut off. "The tool
        // spoke last" used to be the whole signal, and it worked while the questions were a message in
        // the transcript. Once they became a panel of their own the last turn was the user's goal, and
        // every tile waiting for an answer came back from a restart calling itself interrupted —
        // offering Resume, which asks the tool the same round again over questions already on screen.
        if (state.PendingQuestions.Count > 0) return false;

        var lastTurn = state.Messages.LastOrDefault(m => m.Role != GoalMessageRole.System);
        return lastTurn is not { Role: GoalMessageRole.Assistant };
    }

    public string GetPhaseLabel() => IsPaused
        // True of a pause the user asked for and of a run the application was closed in the middle
        // of — deliberately, because telling them apart would cost a flag in the saved state to say
        // something the user can already see: the work stopped, and Resume starts it again.
        ? IsMidRun(CurrentPhase)
            ? $"Stopped during {CurrentPhase.ToString().ToLowerInvariant()}. Click Resume to continue."
            : "Paused. Click Resume to continue."
        : CurrentPhase switch
        {
            GoalPhase.Goal => "Waiting for goal...",
            // Status, not instruction. Both of these used to spell out what to do, and both are now
            // said better a few pixels away by a button with a word on it and a placeholder in the box
            // above it — three copies of one sentence, of which this was the one that could not change
            // when the box did. The paused labels below still name Resume, and deliberately: that
            // button is an icon, so nothing else on screen says the word.
            GoalPhase.Clarify => "Waiting for your answers.",
            GoalPhase.Plan => "Waiting for your approval.",
            GoalPhase.Summary => "Done. Type a new goal, or start a fresh one with +.",
            _ => $"Resumed at {CurrentPhase} phase."
        };

    public GoalTileState ToState(List<GoalMessage> messages, string toolName) => new()
    {
        OriginalGoal = OriginalGoal,
        ClarificationHistory = [..ClarificationHistory],
        ApprovedPlan = ApprovedPlan,
        ProposedPlan = ProposedPlan,
        CurrentPhase = CurrentPhase,
        SelectedToolName = toolName,
        IterationCount = IterationCount,
        LastStopReason = LastStopReason,
        AttemptsBeforeExtension = AttemptsBeforeExtension,
        ClarifyRounds = ClarifyRounds,
        PendingQuestions = [..PendingQuestions],
        IsPaused = IsPaused,
        LastReviewFeedback = LastReviewFeedback,
        LastVerifyOutput = LastVerifyOutput,
        AttemptLog = [..AttemptLog],
        LastReviewFingerprint = LastReviewFingerprint,
        LastReviewCounts = [..LastReviewCounts],
        Criteria = Criteria.Copy(),

        // Filtered here rather than at the call site so no caller can forget: a note about this session
        // is true of the tile in front of the user, not of the goal, and writing one down turns it into
        // a line that comes back for ever.
        Messages = [..messages.Where(m => !m.AboutThisSession)]
    };

    public void LoadFrom(GoalTileState state)
    {
        OriginalGoal = state.OriginalGoal;
        ClarificationHistory.Clear();

        // Labelled on the way in, because a file written before the labels existed holds bare answers,
        // and the next prompt would be handed a conversation where some turns say who is speaking and
        // some do not — which is worse than none of them doing so. What the old format stored was the
        // user's answers, so that is what an unlabelled line is.
        ClarificationHistory.AddRange(state.ClarificationHistory.Select(Labelled));
        ApprovedPlan = state.ApprovedPlan;
        ProposedPlan = state.ProposedPlan;
        CurrentPhase = state.CurrentPhase;
        IterationCount = state.IterationCount;
        LastStopReason = state.LastStopReason;
        AttemptsBeforeExtension = state.AttemptsBeforeExtension;
        ClarifyRounds = state.ClarifyRounds;
        PendingQuestions = [..state.PendingQuestions];
        LastReviewFeedback = state.LastReviewFeedback;
        LastVerifyOutput = state.LastVerifyOutput;
        AttemptLog.Clear();
        AttemptLog.AddRange(state.AttemptLog);
        LastReviewFingerprint = state.LastReviewFingerprint;
        LastReviewCounts = [..state.LastReviewCounts];
        Criteria = state.Criteria.Copy();

        // A run that was interrupted is a pause nobody asked for. The rule lives here rather than in
        // the view model because it is a fact about a loaded state, not about a tile: whoever loads one
        // gets it, and there is no second caller left to forget it.
        IsPaused = state.IsPaused || WasInterrupted(state);
    }
}
