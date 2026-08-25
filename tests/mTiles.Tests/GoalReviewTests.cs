using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Reading a review, and deciding what it means. Both used to be the expression
/// <c>response.Contains("VERDICT: PASS")</c>, and all three ways that was wrong are pinned here.
/// </summary>
public class GoalReviewParsingTests
{
    [Fact]
    public void A_review_that_says_it_cannot_pass_yet_does_not_pass()
    {
        // The substring rule read this as a pass, which is how an unfinished implementation reached the
        // summary and told the user the goal was done.
        var review = GoalResponseParser.ParseReview(
            "I cannot say VERDICT: PASS until the null check is fixed.\n\n" +
            "```json\n{\"goalMet\":false,\"findings\":[{\"severity\":\"error\",\"title\":\"Null check\"}]}\n```");

        Assert.False(review.GoalMet);
        Assert.Equal(1, review.Count(GoalSeverity.Error));
    }

    [Fact]
    public void The_last_json_block_is_the_verdict_not_the_first()
    {
        // A tool asked for JSON at the end routinely shows an example, or the config it is discussing,
        // first. Taking the first block read one of those as the review.
        var review = GoalResponseParser.ParseReview(
            "Here is the file I changed:\n```json\n{\"goalMet\":false}\n```\n\n" +
            "And my verdict:\n```json\n{\"goalMet\":true,\"findings\":[]}\n```");

        Assert.True(review.GoalMet);
    }

    [Fact]
    public void A_block_fenced_with_four_backticks_is_still_read()
    {
        // Which is what a tool does when the block itself contains three — and what this app's own
        // prompt builder does on the way in, so it is not a hypothetical dialect.
        var review = GoalResponseParser.ParseReview(
            "````json\n{\"goalMet\":true,\"findings\":[]}\n````");

        Assert.True(review.WasStructured);
        Assert.True(review.GoalMet);
    }

    [Fact]
    public void Prose_with_no_json_falls_back_to_the_rule_this_tile_had_before()
    {
        // A schema is a request, not a protocol. A tool that ignores it must still be able to finish a
        // goal, so an unstructured answer behaves exactly as it did before any of this existed.
        var passed = GoalResponseParser.ParseReview("Everything checks out. VERDICT: PASS");
        var failed = GoalResponseParser.ParseReview("Two things are broken. VERDICT: FAIL");

        Assert.False(passed.WasStructured);
        Assert.True(passed.GoalMet);
        Assert.False(failed.GoalMet);
        Assert.Empty(failed.Findings);
    }

    [Fact]
    public void The_prompt_asks_for_the_words_the_fallback_reads()
    {
        // The prose fallback exists for a tool that ignores the schema, and it triggers on the words
        // "VERDICT: PASS". While nothing asked for them, such a tool could never say the goal was met:
        // it burned the whole budget and ended every goal unfinished. A fallback whose phrase is never
        // requested is not a fallback.
        var prompt = new GoalPromptBuilder().BuildReview("the goal", "a diff");

        Assert.Contains("VERDICT: PASS", prompt);
        Assert.Contains("VERDICT: FAIL", prompt);
    }

    [Fact]
    public void A_json_block_that_is_not_a_review_is_not_read_as_one()
    {
        // A tool that ends its answer with the settings file it edited has not passed the goal by
        // accident. Neither key present means this is not the verdict, and the prose is.
        var review = GoalResponseParser.ParseReview(
            "All good. VERDICT: PASS\n\n```json\n{\"port\":8080}\n```");

        Assert.False(review.WasStructured);
        Assert.True(review.GoalMet);
    }

    [Fact]
    public void A_block_written_with_windows_line_endings_and_a_word_after_it_is_still_read()
    {
        // $ in multiline mode matches before the \n of a line break but after its \r, so the closing
        // fence of a CRLF answer matched nothing — and the review fell back to the substring rule this
        // class exists to replace, silently, on the only platform most of this app runs on.
        var review = GoalResponseParser.ParseReview(
            "Here is my verdict.\r\n```json\r\n{\"goalMet\":false,\"findings\":[" +
            "{\"severity\":\"error\",\"title\":\"Broken\"}]}\r\n```\r\nHope that helps.\r\n");

        Assert.True(review.WasStructured);
        Assert.Equal(1, review.Count(GoalSeverity.Error));
    }

    [Fact]
    public void A_boolean_the_tool_quoted_is_still_a_boolean()
    {
        // A model shown a schema in prose quotes values as readily as keys. Read as anything but true,
        // an implementation that met its goal spent the whole budget being told it had not.
        Assert.True(GoalResponseParser.ParseReview(
            "```json\n{\"goalMet\":\"true\",\"findings\":[]}\n```").GoalMet);

        Assert.False(GoalResponseParser.ParseClarify(
            "```json\n{\"needsClarification\":\"false\"}\n```").NeedsClarification);
    }

    [Fact]
    public void A_blocker_is_its_own_level_and_is_never_within_tolerance()
    {
        // "It works and it still must not ship" is a different claim from "it is wrong", and forcing it
        // into the other two made the choice bad either way: error claims something is broken when it
        // demonstrably runs, warning invites it to be tolerated.
        var review = GoalResponseParser.ParseReview(
            "```json\n{\"goalMet\":true,\"findings\":[" +
            "{\"severity\":\"blocker\",\"title\":\"Prompt can exceed the Windows command line\"}]}\n```");

        Assert.Equal(1, review.Count(GoalSeverity.Blocker));
        Assert.Equal(0, review.Count(GoalSeverity.Error));

        // No threshold reaches it — not even one raised as far as it will go.
        var permissive = new GoalCompletionCriteria { MaxErrors = 99, MaxWarnings = 99, RequireGoalMet = false };
        Assert.False(GoalCompletionPolicy.IsMet(review, null, permissive));
        Assert.Contains("1 blocker", GoalCompletionPolicy.WhyNotMet(review, null, permissive));
    }

    [Fact]
    public void An_empty_findings_list_is_a_clean_review_not_an_absent_one()
    {
        // The shape of every successful review. Asking about the *count* rather than the key read it as
        // no review at all, dropped it into prose parsing, found no "VERDICT: PASS" in a reply that had
        // been asked for as JSON, and marked the goal unmet — every attempt, for ever.
        var review = GoalResponseParser.ParseReview("```json\n{\"findings\":[]}\n```");

        Assert.True(review.WasStructured);
        Assert.Empty(review.Findings);

        // It still does not *claim* the goal is met — that is a separate question the tool did not
        // answer — but the tile now says so instead of silently spending the budget.
        Assert.True(review.SaidNothingAboutTheGoal);
        Assert.Contains("did not say whether the goal is met", GoalTranscript.Review(review));

        // And the note is advice about a criterion, so it is not given where that criterion is off: it
        // told the user their goal had failed a check nothing was making.
        Assert.DoesNotContain("did not say whether the goal is met",
            GoalTranscript.Review(review, verify: null, goalMetMatters: false));

        // And with the requirement off, a clean review finishes.
        Assert.True(GoalCompletionPolicy.IsMet(review, null,
            new GoalCompletionCriteria { RequireGoalMet = false }));
    }

    [Theory]
    // Stated, at the end of the line, in the spellings tools actually use.
    [InlineData("Everything checks out. VERDICT: PASS", true)]
    [InlineData("VERDICT: PASS", true)]
    [InlineData("Reasoning first.\n\nVERDICT: PASS", true)]
    [InlineData("VERDICT: PASSED", true)]
    [InlineData("**VERDICT:** PASS", true)]
    [InlineData("## VERDICT: PASS", true)]
    [InlineData("Verdict - pass", true)]
    [InlineData("No errors found. VERDICT: PASS", true)]
    [InlineData("Nothing left to do, so VERDICT - PASS.", true)]
    // Refused, in the same spellings.
    [InlineData("VERDICT: FAIL", false)]
    [InlineData("VERDICT: FAILED", false)]
    [InlineData("**VERDICT: FAIL**", false)]
    // The last verdict wins: a reply that reasons its way there states it at the end.
    [InlineData("VERDICT: PASS\n\nOn reflection, VERDICT: FAIL", false)]
    // The sentence this whole feature was built to replace, and the instruction that quotes it. The
    // substring rule read both as a pass; the prompt now asks for exactly this phrase, so tools quote
    // it, discuss it and explain not writing it.
    [InlineData("I cannot say VERDICT: PASS until the null check is fixed.", false)]
    [InlineData("Do not write VERDICT: PASS unless every test runs.", false)]
    [InlineData("There is nothing here about verdicts at all.", false)]
    // Decorated. Models end that line with a tick or a party popper about as often as with nothing, and
    // a rejected verdict reads as "no verdict", which reads as "not met" — so a tool that decorates
    // its answers failed every attempt and burned the whole budget.
    [InlineData("VERDICT: PASS ✅", true)]
    [InlineData("VERDICT: PASS 🎉🎉", true)]
    [InlineData("VERDICT: FAIL ❌", false)]
    // Ordinary reviews that talk about what the code cannot do. The refusal is looked for in the last
    // clause only, and "doesn't" counts only before a verb of stating — without both, a tool that
    // phrases its reviews this way has every attempt read as a failure and burns the whole budget.
    [InlineData("The null check doesn't matter here. VERDICT: PASS", true)]
    [InlineData("Callers cannot reach the branch any more. VERDICT: PASS", true)]
    [InlineData("The parser will not accept an empty file; VERDICT: PASS", true)]
    // The clause break that matters most in practice is the comma, not the full stop: none of these has
    // a full stop in it at all, and every one is an ordinary way to write a passing review. A tool that
    // writes like this failed every attempt and burned the whole budget.
    [InlineData("I can't find anything wrong, so VERDICT: PASS", true)]
    [InlineData("The code is correct and I cannot see any issues, so VERDICT: PASS", true)]
    [InlineData("Nothing that the parser cannot handle — VERDICT: PASS", true)]
    [InlineData("The refactor won't change behaviour, so VERDICT: PASS", true)]
    // Discussed rather than stated. The end anchor catches the ones that trail off into more words;
    // these are the ones whose discussion *finishes* on the verdict, which it could not catch.
    [InlineData("I cannot give a VERDICT: PASS", false)]
    [InlineData("I will not write VERDICT: PASS", false)]
    [InlineData("I am unable to say VERDICT: PASS", false)]
    [InlineData("I don't think I can give a VERDICT: PASS", false)]
    [InlineData("Everything looks fine, but I won't say VERDICT: PASS", false)]
    [InlineData("I cannot say VERDICT: PASS until the null check is fixed", false)]
    [InlineData("Two things are broken. VERDICT: FAIL", false)]
    [InlineData("Nothing here mentions one at all", false)]
    public void A_prose_verdict_is_read_from_a_verdict_that_was_actually_given(string reply, bool passes)
    {
        // This is the fallback for tools that ignored the instruction to answer in JSON — and the
        // prompt now asks for exactly this line, so a tool that has decided not to write it says so in
        // those words. The asymmetry decides every doubtful case: reading a pass as a failure costs one
        // more attempt, reading a refusal as a pass ends the goal over work nobody approved.
        Assert.Equal(passes, GoalWorkflowEngine.IsVerdictPass(reply));
    }

    [Fact]
    public void A_change_past_the_end_of_the_clipped_diff_is_still_a_change()
    {
        // The block that goes in the prompt is cut to fit a command line: 6 000 characters of diff and
        // 1 000 of untracked names. Two reads of a large working tree are therefore identical whenever
        // the change falls past the cut — which is the ordinary case on the resume-after-a-big-
        // implementation that the no-change stop exists for. Compared on that text, the run ended after
        // one attempt saying the implementation had changed nothing: confident, specific and false.
        var huge = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"+ line {i} of a large diff"));

        var before = Snapshot(huge + "\n+ the last line before", "");
        var after = Snapshot(huge + "\n+ the last line after", "");

        Assert.Equal(before.Text, after.Text);          // the prompt cannot tell them apart
        Assert.False(after.ProvablyUnchangedFrom(before));   // the stop must
        Assert.True(after.ProvablyUnchangedFrom(after));
    }

    [Fact]
    public void Nothing_can_be_proved_about_a_tree_nobody_read()
    {
        // Asked only in order to stop a run, so anything short of proof answers no.
        Assert.False(WorktreeSnapshot.Unreadable.ProvablyUnchangedFrom(WorktreeSnapshot.Unreadable));
        Assert.False(new WorktreeSnapshot("x", true).ProvablyUnchangedFrom(new WorktreeSnapshot("x", true)));
    }

    private static WorktreeSnapshot Snapshot(string diff, string untracked) =>
        new(GoalDiffContext.Compose(diff, untracked),
            Readable: true,
            WorktreeSnapshot.Digest(string.Join("\u0000", diff, untracked, "")));

    [Fact]
    public void The_attempt_log_keeps_the_newest_notes_and_forgets_the_oldest()
    {
        var engine = new GoalWorkflowEngine();

        for (var i = 1; i <= GoalWorkflowEngine.MaxAttemptLog + 2; i++)
            engine.RecordAttempt(i, $"did thing {i}");

        Assert.Equal(GoalWorkflowEngine.MaxAttemptLog, engine.AttemptLog.Count);

        // What the last attempt decided is what the next one needs; the first attempt's note is the one
        // that can go.
        Assert.DoesNotContain(engine.AttemptLog, e => e.Contains("did thing 1"));
        Assert.Contains(engine.AttemptLog, e => e.Contains("did thing 7"));
    }

    [Fact]
    public void An_attempt_that_said_nothing_is_not_filed()
    {
        var engine = new GoalWorkflowEngine();

        engine.RecordAttempt(1, "   ");
        engine.RecordAttempt(2, null);

        // An entry saying nothing costs prompt budget and teaches nothing.
        Assert.Empty(engine.AttemptLog);

        // And one attempt cannot fill the note by itself.
        engine.RecordAttempt(3, new string('x', 5_000));
        Assert.True(engine.AttemptLog[0].Length < 500);
    }

    [Fact]
    public void The_note_keeps_the_two_lines_the_prompt_asked_for_and_not_the_preamble()
    {
        // The whole mechanism asks an attempt to *finish* with two lines. Filing the first 300
        // characters filed the opposite end — the preamble — and cut off both, every time. An
        // agent's answer is never 300 characters long, so this was not an edge case: it was the case.
        var answer =
            "I'll start by reading Cart.cs to see how totals are calculated.\n"
            + new string('.', 2_000) + "\n"
            + "Changed src/Cart.cs; discounts now apply before totalling.\n"
            + "Rejected: caching the totals — the basket is rebuilt per request, so it would never hit.";

        var engine = new GoalWorkflowEngine();
        engine.RecordAttempt(1, answer);

        var note = Assert.Single(engine.AttemptLog);
        Assert.Contains("discounts now apply before totalling", note);
        Assert.Contains("Rejected: caching the totals", note);
        Assert.DoesNotContain("I'll start by reading", note);
    }

    [Fact]
    public void An_answer_without_those_lines_is_remembered_by_its_end_not_its_beginning()
    {
        // A tool that ignores the instruction still ends nearer to what it did than it begins.
        var engine = new GoalWorkflowEngine();
        engine.RecordAttempt(1, "First I looked around. " + new string('.', 1_000) + " Finally I edited Cart.cs.");

        Assert.Contains("Finally I edited Cart.cs", Assert.Single(engine.AttemptLog));
    }

    [Fact]
    public void A_new_goal_forgets_what_the_last_one_tried()
    {
        var engine = new GoalWorkflowEngine();
        engine.RecordAttempt(1, "did a thing");
        engine.LastVerifyOutput = "error CS1503";

        engine.StartNewGoal("something else");

        Assert.Empty(engine.AttemptLog);
        Assert.Null(engine.LastVerifyOutput);
    }

    [Fact]
    public void The_severities_keep_the_order_the_saved_counts_are_stored_in()
    {
        // GoalTileState.LastReviewCounts is an array, one entry per severity, in Enum.GetValues order —
        // and it is written to disk. Reordering the enum, or inserting a member anywhere but the end,
        // silently relabels every count in every goal file that already exists: yesterday's blockers
        // come back as errors. Nothing else would fail if that happened, so this is the thing that does.
        Assert.Equal(
            [GoalSeverity.Blocker, GoalSeverity.Error, GoalSeverity.Warning, GoalSeverity.Suggestion],
            Enum.GetValues<GoalSeverity>());

        // The values themselves too: the array is indexed by position, but the severities are also
        // ordered by cast in GoalTranscript.Ordered, and the two agree only while both are untouched.
        Assert.Equal(0, (int)GoalSeverity.Blocker);
        Assert.Equal(3, (int)GoalSeverity.Suggestion);
    }

    [Fact]
    public void A_verification_that_never_finished_is_not_a_verification_that_passed()
    {
        var clean = GoalResponseParser.ParseReview("```json\n{\"goalMet\":true,\"findings\":[]}\n```");
        var criteria = new GoalCompletionCriteria();

        // A command that could not be *started* is forgiven on purpose — that is the machine's fault,
        // and failing a goal over a missing shell blames the work for the tooling.
        Assert.True(GoalCompletionPolicy.IsMet(clean, VerifyOutcome.NotRun("no shell"), criteria));

        // One that was killed for running too long is not the same thing: it is very often the change
        // that was just made, and it produced no answer at all about whether the goal is met.
        var killed = VerifyOutcome.Timeout("it was still running after 30 minutes and was stopped");
        Assert.False(GoalCompletionPolicy.IsMet(clean, killed, criteria));
        Assert.Contains("never finished", GoalCompletionPolicy.WhyNotMet(clean, killed, criteria));
    }

    [Fact]
    public void An_unfenced_block_is_machinery_too_and_does_not_reach_the_transcript()
    {
        // The parser reads an unfenced object by its outermost braces, because a tool told to "answer
        // with JSON only" tends to send exactly that. So a reply of prose followed by a bare {…} parsed
        // perfectly well — and then had the whole thing, JSON included, printed as the tool's own
        // words, in the transcript and in the prompt that reads it back on the next round.
        var clarify = GoalResponseParser.ParseClarify(
            "The goal is clear enough; I am assuming the cart is the only caller.\n" +
            "{\"needsClarification\":false}");

        Assert.True(clarify.WasStructured);

        var aside = GoalTranscript.Aside(clarify);
        Assert.Contains("assuming the cart is the only caller", aside);
        Assert.DoesNotContain("needsClarification", aside);

        // Same route, same rule: with no questions this is what the transcript shows.
        Assert.DoesNotContain("needsClarification", GoalTranscript.Questions(clarify));
    }

    [Fact]
    public void A_fence_longer_than_three_backticks_leaves_none_of_them_in_the_prose()
    {
        // LastIndexOf finds the last three of a four-backtick fence, so the fourth used to survive at
        // the front of the very prose the trim exists to clean up.
        var review = GoalResponseParser.ParseReview(
            "````json\n{\"goalMet\":false,\"findings\":[]}\n````\n\nNothing calls the new method.");

        var text = GoalTranscript.Review(review);
        Assert.Contains("Nothing calls the new method", text);
        Assert.DoesNotContain("`Nothing", text);
    }

    [Fact]
    public void A_review_with_no_findings_keeps_the_reason_it_gave()
    {
        // "Goal not met · nothing found" was the entire account of a failed attempt. The argument
        // against reprinting the prose only holds where there is a finding list to duplicate.
        var review = GoalResponseParser.ParseReview(
            "The change never runs: nothing calls the new method.\n\n" +
            "```json\n{\"goalMet\":false,\"findings\":[]}\n```");

        Assert.Contains("nothing calls the new method", GoalTranscript.Review(review));
    }

    [Fact]
    public void A_reason_written_after_the_json_block_is_kept_too()
    {
        // The prompt asks for the block last, so prose above it is the ordinary case — but a tool that
        // explains itself afterwards would otherwise have the explanation dropped, in exactly the case
        // where it is the only account there is.
        var review = GoalResponseParser.ParseReview(
            "```json\n{\"goalMet\":false,\"findings\":[]}\n```\n\n" +
            "The change never runs: nothing calls the new method.");

        Assert.Contains("nothing calls the new method", GoalTranscript.Review(review));
    }

    [Fact]
    public void A_key_is_a_key_whatever_case_the_tool_wrote_it_in()
    {
        // TryGetProperty is case-sensitive, and a model shown a schema in prose writes GoalMet as
        // readily as goalMet — it is .NET's own serialiser default and what a tool modelling the answer
        // on a C# class produces. One capital letter dropped the entire review into the prose path,
        // where it had no findings and no verdict and blocked the goal for ever.
        var review = GoalResponseParser.ParseReview(
            "```json\n{\"GoalMet\":true,\"Findings\":[{\"Severity\":\"blocker\",\"Title\":\"X\"}]}\n```");

        Assert.True(review.WasStructured);
        Assert.True(review.GoalMet);
        Assert.Equal(1, review.Count(GoalSeverity.Blocker));
    }

    [Fact]
    public void An_unrecognised_severity_is_a_warning_rather_than_a_suggestion()
    {
        // Guessing downwards would let a label nobody has examined through a "no errors, no warnings"
        // gate, which is the one thing the gate exists to stop.
        var review = GoalResponseParser.ParseReview(
            "```json\n{\"goalMet\":true,\"findings\":[{\"severity\":\"catastrophe\",\"title\":\"?\"}]}\n```");

        Assert.Equal(1, review.Count(GoalSeverity.Warning));
    }

    [Fact]
    public void A_five_level_scale_is_not_handed_the_level_nobody_can_tune()
    {
        // Blocker has no threshold at all — it is the level for "it works and it must not stand". A
        // tool with its own scale writes "critical" for something very broken, which is an Error here,
        // and Error is the level the user can raise a tolerance for.
        var review = GoalResponseParser.ParseReview(
            "```json\n{\"goalMet\":false,\"findings\":[" +
            "{\"severity\":\"critical\",\"title\":\"Very broken\"}," +
            "{\"severity\":\"blocker\",\"title\":\"Works and cannot ship\"}]}\n```");

        Assert.Equal(1, review.Count(GoalSeverity.Error));
        Assert.Equal(1, review.Count(GoalSeverity.Blocker));
    }

    [Fact]
    public void A_finding_with_nothing_in_it_is_dropped()
    {
        // It would otherwise be counted against a completion criterion while saying nothing at all.
        var review = GoalResponseParser.ParseReview(
            "```json\n{\"goalMet\":true,\"findings\":[{\"severity\":\"error\"},{\"severity\":\"error\",\"title\":\"Real\"}]}\n```");

        Assert.Equal(1, review.Count(GoalSeverity.Error));
    }

    [Fact]
    public void Two_reviews_of_the_same_defect_have_the_same_fingerprint_however_the_detail_is_worded()
    {
        var first = GoalResponseParser.ParseReview(
            "```json\n{\"goalMet\":false,\"findings\":[{\"severity\":\"error\",\"file\":\"a.cs\",\"title\":\"Broken\",\"detail\":\"Because of X.\"}]}\n```");
        var second = GoalResponseParser.ParseReview(
            "```json\n{\"goalMet\":false,\"findings\":[{\"severity\":\"error\",\"file\":\"a.cs\",\"title\":\"Broken\",\"detail\":\"This happens as X occurs.\"}]}\n```");

        // The detail is prose and differs on every run. If it counted, no two reviews would ever match
        // and the no-progress stop would never fire.
        Assert.Equal(first.Fingerprint(), second.Fingerprint());
    }
}

/// <summary>The clarification round, which can now end without a question being asked.</summary>
public class GoalClarifyParsingTests
{
    [Fact]
    public void A_tool_that_has_nothing_to_ask_is_believed()
    {
        var clarify = GoalResponseParser.ParseClarify("```json\n{\"needsClarification\":false}\n```");

        Assert.True(clarify.WasStructured);
        Assert.False(clarify.NeedsClarification);
    }

    [Fact]
    public void Questions_win_over_a_flag_that_contradicts_them()
    {
        // Skipping to the plan here would leave three questions on screen that nobody can answer.
        var clarify = GoalResponseParser.ParseClarify(
            "```json\n{\"needsClarification\":false,\"questions\":[{\"question\":\"Which file?\"}]}\n```");

        Assert.True(clarify.NeedsClarification);
        Assert.Single(clarify.Questions);
    }

    [Fact]
    public void A_round_that_asks_nothing_is_not_a_question_however_it_labels_itself()
    {
        // There is no way to answer this. The tile used to print the raw JSON as the question, file it
        // in the clarification history, hand it to the planner, and then wait for a reply to it.
        var clarify = GoalResponseParser.ParseClarify(
            "```json\n{\"needsClarification\":true,\"questions\":[]}\n```");

        Assert.False(clarify.NeedsClarification);
    }

    [Fact]
    public void Prose_with_a_stray_block_after_it_is_shown_without_the_block()
    {
        // The prose is the question; the block is machinery. Printing both put JSON in front of the
        // user and into the prompt that reads this back on the next round.
        var clarify = GoalResponseParser.ParseClarify(
            "Which config file holds the port?\n\n```json\n{\"note\":\"n/a\"}\n```");

        var shown = GoalTranscript.Questions(clarify);

        Assert.Equal("Which config file holds the port?", shown);
        Assert.DoesNotContain("```", shown);
    }

    [Fact]
    public void Prose_is_a_question_and_is_waited_on()
    {
        var clarify = GoalResponseParser.ParseClarify("Which config file holds the port?");

        Assert.False(clarify.WasStructured);
        Assert.True(clarify.NeedsClarification);
        Assert.Equal("Which config file holds the port?", clarify.RawText);
    }

    [Fact]
    public void The_composer_is_filled_with_the_numbering_and_nothing_else()
    {
        var clarify = GoalResponseParser.ParseClarify(
            "```json\n{\"needsClarification\":true,\"questions\":[" +
            "{\"question\":\"Which file?\",\"options\":[\"appsettings.json\",\"launchSettings.json\"]}," +
            "{\"question\":\"Sync or async?\"}]}\n```");

        // Answering in place is the point: a blank box under numbered questions asks the user to
        // reproduce the numbering, and the ones who do not leave answers nothing can be matched to.
        //
        // The options are NOT filled in. Doing that made Enter mean "send the tool's own guess back as
        // my answer", from a box the user may not have read. They are printed under the question,
        // where they are an offer rather than a default.
        Assert.Equal("1. \n2. ", GoalTranscript.AnswerSkeleton(clarify));
        Assert.Contains("appsettings.json", GoalTranscript.Questions(clarify));
    }

    /// <summary>
    /// The one rule <see cref="GoalTranscript.IsBlankAnswer"/> exists for: an answer is nothing only
    /// when nothing but the composer's own numbering came back.
    /// <para>The composer is prefilled with the skeleton, so pressing Enter sends a real string — and
    /// each one that gets through costs one clarification round out of three on a message the tool can
    /// make nothing of. The other side of the rule matters just as much: "3." is a perfectly good reply
    /// to "how many retries?", and stripping any leading number made it look like the third line of an
    /// untouched skeleton and refused it. So a marker is dropped only where its number matches the
    /// line's own position, which is why a skeleton with a line deleted out of the middle is still
    /// nothing at all while a lone number is an answer.</para>
    /// </summary>
    [Theory]
    // The skeleton, in every shape the composer or an edit can leave it in.
    [InlineData("1.\n2.\n3.", true)]
    [InlineData("1)\n2)", true)]
    [InlineData("  1.   \n\n 2. ", true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    // A line deleted out of the middle: the numbers no longer match their positions, and it is still
    // nothing at all.
    [InlineData("1.\n3.", true)]
    [InlineData("2)\n3)", true)]
    // A lone number is an answer, not the tail of a skeleton.
    [InlineData("3.", false)]
    [InlineData("2)", false)]
    // Anything answered at all counts, whichever line it was written on.
    [InlineData("1. appsettings.json\n2.", false)]
    [InlineData("1.\n2. yes", false)]
    [InlineData("2. yes", false)]
    [InlineData("no, do it differently", false)]
    public void An_answer_is_blank_only_when_nothing_but_the_numbering_came_back(string text, bool blank)
    {
        Assert.Equal(blank, GoalTranscript.IsBlankAnswer(text));
    }

    [Fact]
    public void An_answer_that_is_nothing_but_an_unreadable_block_arrives_without_its_fences()
    {
        // The one case the fallback was written for and did not cover: no prose to fall back to, so it
        // returned the raw text — which is the fences, straight into the transcript and into the prompt
        // that reads this back on the next round.
        var clarify = GoalResponseParser.ParseClarify(
            "```json\n{\"questions\": [ this is not json ]}\n```");

        var shown = GoalTranscript.Questions(clarify);

        Assert.DoesNotContain("```", shown);
        Assert.Contains("this is not json", shown);
    }

    [Fact]
    public void The_questions_and_the_skeleton_number_themselves_the_same_way()
    {
        // An answer is matched to its question by eye. "1)" above and "1." below is one more thing for
        // the reader to reconcile, in the one place where the whole point is that they line up.
        var clarify = GoalResponseParser.ParseClarify(
            "```json\n{\"questions\":[{\"question\":\"Which file?\"}]}\n```");

        Assert.StartsWith("1. ", GoalTranscript.Questions(clarify));
        Assert.StartsWith("1. ", GoalTranscript.AnswerSkeleton(clarify));
    }

    [Fact]
    public void A_detected_goal_arrives_without_its_wrapping()
    {
        Assert.Equal("Make pairings survive a restart.",
            GoalResponseParser.ParseDetectedGoal("```\nMake pairings survive a restart.\n```"));

        Assert.Equal("Make pairings survive a restart.",
            GoalResponseParser.ParseDetectedGoal("  Make pairings survive a restart.  "));
    }
}

/// <summary>When a goal is allowed to call itself done.</summary>
public class GoalCompletionPolicyTests
{
    private static GoalReviewResult Review(bool met, params (GoalSeverity Severity, string Title)[] findings) => new()
    {
        GoalMet = met,
        WasStructured = true,
        Findings = findings.Select(f => new GoalFinding { Severity = f.Severity, Title = f.Title }).ToList(),
    };

    [Fact]
    public void Suggestions_never_block()
    {
        var criteria = new GoalCompletionCriteria();
        var review = Review(true,
            (GoalSeverity.Suggestion, "a"), (GoalSeverity.Suggestion, "b"), (GoalSeverity.Suggestion, "c"));

        Assert.True(GoalCompletionPolicy.IsMet(review, null, criteria));
    }

    [Fact]
    public void One_warning_blocks_by_default_and_stops_blocking_when_it_is_allowed()
    {
        var review = Review(true, (GoalSeverity.Warning, "a"));

        Assert.False(GoalCompletionPolicy.IsMet(review, null, new GoalCompletionCriteria()));
        Assert.True(GoalCompletionPolicy.IsMet(review, null, new GoalCompletionCriteria { MaxWarnings = 1 }));
    }

    [Fact]
    public void A_clean_review_of_the_wrong_thing_is_not_the_goal_met()
    {
        // The whole argument for goalMet being its own field rather than a fourth severity.
        var review = Review(met: false);

        Assert.False(GoalCompletionPolicy.IsMet(review, null, new GoalCompletionCriteria()));
        Assert.True(GoalCompletionPolicy.IsMet(review, null,
            new GoalCompletionCriteria { RequireGoalMet = false }));
    }

    [Fact]
    public void Turning_off_the_goal_requirement_does_not_disarm_a_tool_that_answers_in_prose()
    {
        // Findings only exist for a structured review. With them empty every count below passes on any
        // answer at all, so this setting took every gate down at once in front of a tool that ignores
        // the schema — and a goal finished on its first attempt over a review that said it had failed.
        var prose = GoalResponseParser.ParseReview("Two things are broken. VERDICT: FAIL");
        var relaxed = new GoalCompletionCriteria { RequireGoalMet = false };

        Assert.False(GoalCompletionPolicy.IsMet(prose, null, relaxed));
        Assert.Contains("goal is not met", GoalCompletionPolicy.WhyNotMet(prose, null, relaxed));

        // It still relaxes what it says it relaxes, where there is something else left to judge by.
        var structured = GoalResponseParser.ParseReview(
            "```json\n{\"goalMet\":false,\"findings\":[]}\n```");
        Assert.True(GoalCompletionPolicy.IsMet(structured, null, relaxed));
    }

    [Fact]
    public void A_review_that_never_addressed_the_goal_is_not_told_it_said_no()
    {
        // Different things to do about them: a tool that answers the question and answers no, and a
        // tool that never addressed it.
        var silent = GoalResponseParser.ParseReview("```json\n{\"findings\":[]}\n```");

        Assert.Contains("did not say whether",
            GoalCompletionPolicy.WhyNotMet(silent, null, new GoalCompletionCriteria()));
    }

    [Fact]
    public void A_failing_verify_command_outranks_a_review_that_says_everything_is_fine()
    {
        var review = Review(met: true);
        var failed = new VerifyOutcome(Ran: true, ExitCode: 1, Output: "error CS0103");

        Assert.False(GoalCompletionPolicy.IsMet(review, failed, new GoalCompletionCriteria()));
        Assert.Contains("exited 1",
            GoalCompletionPolicy.WhyNotMet(review, failed, new GoalCompletionCriteria()));
    }

    [Fact]
    public void An_unstructured_review_never_trips_the_no_progress_stop()
    {
        // Its fingerprint is the same two words every lap, so the check would have cut every run by a
        // tool that ignores the schema down to two attempts — and blamed it on findings it never read.
        var prose = GoalResponseParser.ParseReview("VERDICT: FAIL");

        Assert.False(GoalCompletionPolicy.RepeatsPrevious(prose, prose.Fingerprint(),
            new GoalCompletionCriteria()));
    }

    [Fact]
    public void The_budget_summary_does_not_claim_the_goal_was_completed()
    {
        var text = GoalCompletionPolicy.Summarise(GoalStopReason.BudgetSpent, 5);

        Assert.DoesNotContain("completed", text);
        Assert.Contains("without meeting", text);
    }

    [Fact]
    public void The_budget_summary_counts_the_attempts_that_happened_not_the_budget()
    {
        // The two are the same number until the budget moves. Lowering it from five to two after four
        // attempts had run reported "stopped after 2 attempts" over a transcript containing four.
        Assert.Contains("4 attempts", GoalCompletionPolicy.Summarise(GoalStopReason.BudgetSpent, 4));
    }

    [Fact]
    public void The_summary_reports_the_attempts_the_run_actually_had()
    {
        // The panel stores what was typed and a file can hold anything. Reading the raw number here had
        // a run of fifty report itself as "stopped after 999 attempts".
        var absurd = new GoalCompletionCriteria { MaxIterations = 999 };

        Assert.Equal(50, GoalCompletionPolicy.Attempts(absurd));

        // And the other end: zero attempts would finish a goal the moment its plan was approved.
        Assert.Equal(1, GoalCompletionPolicy.Attempts(new GoalCompletionCriteria { MaxIterations = 0 }));
    }
}

/// <summary>What a review looks like once it is written into the transcript.</summary>
public class GoalTranscriptTests
{
    [Fact]
    public void Only_the_defects_go_back_to_the_tool()
    {
        // The whole review used to. A suggestion competed with an error for the tool's attention and
        // for the prompt's own size budget, and an attempt could be spent renaming a variable.
        var review = GoalResponseParser.ParseReview(
            "```json\n{\"goalMet\":false,\"findings\":[" +
            "{\"severity\":\"error\",\"file\":\"a.cs\",\"title\":\"Crashes\"}," +
            "{\"severity\":\"suggestion\",\"title\":\"Rename x\"}]}\n```");

        var feedback = GoalTranscript.Feedback(review);

        Assert.Contains("Crashes", feedback);
        Assert.DoesNotContain("Rename x", feedback);
    }

    [Fact]
    public void Errors_come_before_warnings_before_suggestions()
    {
        var review = GoalResponseParser.ParseReview(
            "```json\n{\"goalMet\":false,\"findings\":[" +
            "{\"severity\":\"suggestion\",\"title\":\"Last\"}," +
            "{\"severity\":\"error\",\"title\":\"First\"}]}\n```");

        var text = GoalTranscript.Review(review);

        Assert.True(text.IndexOf("First", StringComparison.Ordinal)
                    < text.IndexOf("Last", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unstructured_review_is_shown_exactly_as_the_tool_wrote_it()
    {
        var review = GoalResponseParser.ParseReview("It all looks fine to me. VERDICT: PASS");

        Assert.Equal("It all looks fine to me. VERDICT: PASS", GoalTranscript.Review(review));
    }

    [Fact]
    public void A_verify_run_is_reported_beside_the_counts()
    {
        var review = GoalResponseParser.ParseReview("```json\n{\"goalMet\":true,\"findings\":[]}\n```");

        Assert.Contains("verify exited 2",
            GoalTranscript.Review(review, new VerifyOutcome(true, 2, "boom")));
    }
}

/// <summary>What survives when a prompt has to be made smaller than it wants to be.</summary>
public class GoalPromptFittingTests
{
    private static string Fitted(string tree, int budget) =>
        new GoalPromptBuilder().BuildReview("the goal", tree, verifyOutput: null, budget: budget);

    [Fact]
    public void The_untracked_file_names_survive_every_cut_the_diff_does_not()
    {
        // A new file's name is a line, and no form of diff will ever show it — it is the one part of
        // the tree the tool cannot get any other way. It was being lost twice over: the tree block was
        // re-capped on *every* build at a ceiling the assembled string always exceeded, and whatever
        // cuts the block cuts it from the end, which is where the names used to sit.
        var tree = GoalDiffContext.Compose(
            new string('d', 20_000).Replace("dddddddddd", "dddddddddd\n"),
            "src/BrandNew.cs");

        Assert.NotNull(tree);
        Assert.Contains("src/BrandNew.cs", new GoalPromptBuilder().BuildReview("the goal", tree));

        // The claim, across every budget that can carry a working tree at all: wherever the block is
        // present, the names are in it. Below that the block is dropped whole, which is the honest
        // degradation — a diff with the new files quietly missing is worse than no diff.
        foreach (var budget in (int[])[8_000, 6_000, 4_000, 3_000])
        {
            var prompt = Fitted(tree, budget);
            if (prompt.Contains("Current state of the working tree"))
                Assert.Contains("src/BrandNew.cs", prompt);
        }

        Assert.Contains("src/BrandNew.cs", Fitted(tree, 4_000));
    }

    [Fact]
    public void Every_prompt_fits_the_tightest_command_line_this_app_will_ever_use()
    {
        // The numbers a tool is really handed, not made-up ones: Tightest() is what PromptBudget answers
        // before a tool has been resolved, and it is the .cmd shim's limit — which is what npm
        // installs, so three of the four seeded profiles go through it. Quoted() is how the argument
        // grows on the way onto the command line. A prompt over this is refused by the guard and the
        // goal cannot move, so the fitting either works at these values or it does not work at all.
        if (CommandLineLength.Tightest() is not { } budget) return;   // no such limit off Windows

        var tree = GoalDiffContext.Compose(new string('d', 200_000), "src/BrandNew.cs");
        var builder = new GoalPromptBuilder();
        var huge = new string('g', 50_000);

        foreach (var prompt in (string[])[
            builder.BuildReview(huge, tree, new string('v', 50_000), budget),
            builder.BuildImplement(
                new GoalPromptBuilder.ImplementContext(
                    huge, huge, huge, VerifyOutput: huge, GitDiff: tree,
                    AttemptLog: [huge, huge], Attempt: 3, Attempts: 5),
                budget),
            builder.BuildDetectGoal(tree!, budget),
        ])
            Assert.True(CommandLineLength.Quoted(prompt) <= budget,
                $"a prompt of {CommandLineLength.Quoted(prompt)} would be refused by the guard");

        // Trimmed, not gutted, at the size this tile really builds: the goal, the quality rules, a
        // verify command's output, seven thousand characters of working tree, the severity rules and an
        // example come to around twelve thousand characters against the 8 191 a .cmd shim allows. The
        // run that overflows is the one the fitting exists for — a resume after a large implementation
        // in a workspace with a verify command — and what comes out still has to say what it is asking
        // for and still has to carry the goal.
        var unfitted = builder.BuildReview(new string('g', 5_000), new string('d', 20_000), new string('v', 5_000));
        Assert.True(CommandLineLength.Quoted(unfitted) > budget,
            "this proves nothing unless the unfitted prompt really is too long");

        var fitted = builder.BuildReview(
            new string('g', 5_000), new string('d', 20_000), new string('v', 5_000), budget);

        Assert.True(CommandLineLength.Quoted(fitted) <= budget);
        Assert.Contains("goalMet", fitted);
        Assert.Contains("blocker", fitted);
        Assert.Contains("gggg", fitted);
    }

    [Fact]
    public void A_note_that_the_tree_could_not_be_read_is_never_the_part_that_gets_cut()
    {
        // Silence here is indistinguishable from a clean tree, and a tool told nothing has changed
        // writes over work it cannot see. It is one line; it is never what gives way.
        var tree = GoalDiffContext.Compose(new string('d', 20_000), null, "git exploded");

        Assert.Contains("git exploded", Fitted(tree!, 2_000));
    }

    [Fact]
    public void The_conversation_keeps_its_newest_turns_rather_than_its_oldest()
    {
        // Everything else here keeps its head and drops its tail, which is right for a diff and exactly
        // wrong for a conversation: the newest turns are what the next round has to act on.
        var turns = Enumerable.Range(1, 400).Select(i => $"User: answer number {i}").ToList();

        var prompt = new GoalPromptBuilder().BuildPlan("the goal", turns);

        Assert.Contains("answer number 400", prompt);
        Assert.DoesNotContain("answer number 1 ", prompt);
        Assert.Contains("earlier turns omitted", prompt);
    }
}

/// <summary>The one dialog in this feature that is a security barrier rather than a convenience.</summary>
public class VerifyCommandDialogTests
{
    [Fact]
    public void A_command_nobody_could_read_in_full_is_refused_rather_than_shortened()
    {
        // The rule the dialog rests on: a command that will not fit in the question cannot be consented
        // to, so it is never asked about. Showing half of it and collecting a yes is worse than either.
        Assert.True(CommandDisplay.ForDialog(new string('x', 10_000)).Length
                    > CommandDisplay.MaxConsentable);

        Assert.True(CommandDisplay.ForDialog("dotnet build; dotnet test --filter Goal").Length
                    <= CommandDisplay.MaxConsentable,
            "a real verify command has to fit comfortably, or the limit is the wrong limit");
    }

    [Fact]
    public void Text_that_can_lie_about_itself_is_made_visible_first()
    {
        // A right-to-left override reverses what follows it on screen, so "rm -rf /" can be made to
        // read as something harmless; a zero-width space hides inside a word and splits it for the
        // shell but not for the eye. Both are standard ways of writing one thing and displaying
        // another, in the one dialog whose whole job is showing what will run.
        var deceptive = "echo \u202Esafe\u200B; rm -rf /";

        var shown = CommandDisplay.ForDialog(deceptive);

        Assert.DoesNotContain('\u202E', shown);
        Assert.DoesNotContain('\u200B', shown);

        // Replaced, not deleted: a command that had something in it must not come out looking innocent.
        Assert.Contains("␦", shown);
        Assert.Contains("rm -rf /", shown);
    }

    [Fact]
    public void Legitimate_text_outside_the_basic_plane_is_left_alone()
    {
        // Everything astral is stored as a surrogate pair, and asking each half whether it is a
        // surrogate says yes to both — so every emoji in a perfectly honest command came out as two
        // blobs, in the one dialog whose job is showing what will run.
        var honest = "dotnet test --filter \"Zażółć gęślą jaźń 🚀 漢字\"";

        Assert.Equal(honest, CommandDisplay.ForDialog(honest));
    }

    [Fact]
    public void A_command_full_of_newlines_cannot_push_itself_out_of_the_dialog()
    {
        // The string comes out of a file a branch can carry in from anywhere, and this box is what the
        // user is asked to approve. A hundred blank lines push the part that matters off the bottom of
        // it, which is exactly what somebody would write to get a yes out of a person not reading
        // carefully.
        var hidden = "echo safe" + new string('\n', 100) + "rm -rf /";

        var shown = CommandDisplay.ForDialog(hidden);

        Assert.DoesNotContain("\n", shown);
        Assert.Contains("echo safe", shown);
        Assert.Contains("rm -rf /", shown);
    }

    [Fact]
    public void Nothing_in_the_question_is_hidden_behind_an_ellipsis()
    {
        // Cutting the display at 200 characters looked tidy and was the same hole as the newlines it
        // was written to close: everything after the ellipsis is hidden, so the payload simply moves
        // past it. A command too long to show is refused instead — see CommandDisplay.MaxConsentable.
        var long_ = "echo safe" + new string(';', 600) + "rm -rf /";

        var shown = CommandDisplay.ForDialog(long_);

        Assert.DoesNotContain("…", shown);
        Assert.Contains("rm -rf /", shown);
        Assert.True(shown.Length > CommandDisplay.MaxConsentable / 2,
            "the point of this test is a command long enough to have been truncated before");
    }

    [Fact]
    public void A_field_showing_a_command_raw_says_so_when_that_is_not_what_will_run()
    {
        // A text box cannot sanitise what it is editing, and editing that box is what makes a command
        // "chosen" — so the panel says what the dialog would have shown instead of leaving the reader
        // to decide from the deceptive version.
        Assert.True(CommandDisplay.RendersHonestly("dotnet build"));
        Assert.True(CommandDisplay.RendersHonestly("dotnet build; dotnet test"));

        Assert.False(CommandDisplay.RendersHonestly("echo safe\u202Erm -rf /"));
        Assert.False(CommandDisplay.RendersHonestly("echo safe\nrm -rf /"));
        Assert.False(CommandDisplay.RendersHonestly("echo safe" + new string('\u00A0', 40) + "rm -rf /"));
    }

    [Fact]
    public void A_command_padded_with_exotic_spaces_cannot_push_itself_out_of_the_dialog_either()
    {
        // The same trick as the newlines, with the box turned on its side and a character the collapse
        // did not know about: `[ ]{2,}` matches the ASCII space and nothing else, so five hundred
        // non-breaking spaces went through untouched and pushed the second half off the right edge.
        var hidden = "echo safe" + new string('\u00A0', 500) + "rm -rf /";
        var wide = "echo safe" + new string('\u3000', 200) + "rm -rf /";

        foreach (var command in (string[])[hidden, wide])
        {
            var shown = CommandDisplay.ForDialog(command);

            Assert.Equal("echo safe rm -rf /", shown);
            Assert.True(CommandDisplay.CanBeConsentedTo(command));
        }
    }

    [Fact]
    public void The_two_line_breaks_that_are_not_control_characters_break_lines_here_too()
    {
        // U+2028 and U+2029 are separators rather than control or format characters, so the pass that
        // marks deceptive characters lets them through — correctly, because they are line breaks and
        // the next step splits on lines. This pins that they do end up split, whichever step does it.
        var hidden = "echo safe\u2028rm -rf /\u2029echo done";

        var shown = CommandDisplay.ForDialog(hidden);

        Assert.DoesNotContain("\u2028", shown);
        Assert.DoesNotContain("\u2029", shown);
        Assert.Contains("rm -rf /", shown);
        Assert.Contains("echo done", shown);
    }
}

/// <summary>The criteria object itself — small, copied on every save and load, and easy to extend
/// without noticing.</summary>
public class GoalCompletionCriteriaTests
{
    [Fact]
    public void Copy_carries_every_property_there_is()
    {
        // Walked rather than listed. Copy() is called on every save and every load, so a property added
        // later and left out of it would not fail anything — it would quietly reset itself each time
        // the tile was reopened, which is the kind of bug that gets blamed on the user.
        var original = new GoalCompletionCriteria();
        var properties = typeof(GoalCompletionCriteria)
            .GetProperties()
            .Where(p => p.CanWrite)
            .ToList();

        Assert.NotEmpty(properties);

        foreach (var property in properties)
            property.SetValue(original, Distinctive(property.PropertyType));

        var copy = original.Copy();

        foreach (var property in properties)
            Assert.Equal(property.GetValue(original), property.GetValue(copy));
    }

    /// <summary>A value no default of this type would be, so a property Copy forgets fails loudly.</summary>
    private static object Distinctive(Type type) =>
        type == typeof(int) ? 37
        : type == typeof(bool) ? false
        : type == typeof(string) ? "a distinctive value"
        : throw new NotSupportedException(
            $"GoalCompletionCriteria grew a {type.Name} property; teach this test what an unusual one " +
            "looks like.");
}

/// <summary>The verify command's output, cut to something a prompt can carry.</summary>
public class VerifyCommandRunnerTests
{
    [Fact]
    public void Both_ends_of_a_long_output_survive_and_the_middle_does_not()
    {
        // A failing build prints its first error near the top and repeats itself; a test runner puts
        // its summary at the bottom. Keeping only the head would lose half of what matters.
        var output = "FIRST LINE\n" + string.Join("\n", Enumerable.Repeat("noise", 2_000)) + "\nLAST LINE";

        var clipped = VerifyCommandRunner.Clip(output);

        Assert.Contains("FIRST LINE", clipped);
        Assert.Contains("LAST LINE", clipped);
        Assert.Contains("truncated", clipped);
        Assert.True(clipped.Length < output.Length);
    }

    [Fact]
    public void The_clip_fits_the_budget_it_claims_so_nothing_cuts_it_again()
    {
        // The marker used to be added on top of the budget, putting the result some twenty characters
        // over — and the prompt block that carries it is capped at exactly that number, so those
        // characters came off the end. The end is the tail, which is the half this keeps a tail for.
        var clipped = VerifyCommandRunner.Clip(
            "FIRST\n" + string.Join("\n", Enumerable.Repeat("noise", 2_000)) + "\nLAST LINE");

        Assert.True(clipped.Length <= VerifyCommandRunner.MaxOutputChars);
        Assert.Contains("LAST LINE", clipped);
    }

    [Fact]
    public void A_short_output_is_left_alone()
    {
        Assert.Equal("Build succeeded.", VerifyCommandRunner.Clip("Build succeeded."));
    }
}
