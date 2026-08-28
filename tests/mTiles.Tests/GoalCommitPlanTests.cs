using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a proposed set of commits is allowed to become, and what the person approving it is told.
/// </summary>
/// <remarks>
/// Pure, and pinned here rather than through the tile, for the reason <c>GoalCompletionPolicy</c> is:
/// the interesting half is what happens when the tool's answer does not match the question, and every
/// one of those cases would otherwise need a repository, an AI process and a full run of the phase
/// machine to reach once.
/// </remarks>
public class GoalCommitPlanTests
{
    private static GoalCommitScope Scope(string[] ours, params string[] theirs) => new(ours, theirs);

    private static GoalCommit Commit(string type, string subject, params string[] files) =>
        new() { Type = type, Subject = subject, Files = [..files] };

    /// <summary>
    /// A path the tool named that is not this run's stays out — which is the whole point of holding the
    /// answer against the scope instead of running it.
    /// </summary>
    /// <remarks>
    /// <c>git commit -- path</c> commits the <b>whole file</b>, not the part this run wrote. One
    /// invented path is therefore somebody's unfinished afternoon landing in a commit about something
    /// else, which is the same failure the prompts guard against one layer up.
    /// </remarks>
    [Fact]
    public void A_file_that_is_not_this_runs_is_never_committed()
    {
        var scope = Scope(["src/Cart.cs"], "src/Theirs.cs");

        var sound = GoalCommitPlan.Sound(
            [Commit("feat", "discounts", "src/Cart.cs", "src/Theirs.cs", "src/Invented.cs")], scope);

        var only = Assert.Single(sound);
        Assert.Equal(["src/Cart.cs"], only.Files);
    }

    /// <summary>
    /// Files the tool forgot are committed anyway, in a chore at the end.
    /// </summary>
    /// <remarks>
    /// Losing them silently is the worst outcome available: the run's work is then split between the
    /// history and the working tree with nothing saying which is which, and the user finds out days
    /// later. Asking the tool again would spend a second AI run on the same question.
    /// </remarks>
    [Fact]
    public void Files_the_tool_forgot_are_swept_into_a_final_chore()
    {
        var scope = Scope(["src/Cart.cs", "src/Forgotten.cs"]);

        var sound = GoalCommitPlan.Sound([Commit("feat", "discounts", "src/Cart.cs")], scope);

        Assert.Equal(2, sound.Count);
        Assert.Equal("chore", sound[1].Type);
        Assert.Equal(["src/Forgotten.cs"], sound[1].Files);
    }

    /// <summary>
    /// A file named in two commits is committed once. git would put it in the first and leave the
    /// second with nothing, so the only question is whether the user learns that from a git error or
    /// from a list that already accounts for it.
    /// </summary>
    [Fact]
    public void A_file_named_twice_is_committed_once()
    {
        var scope = Scope(["a.cs", "b.cs"]);

        var sound = GoalCommitPlan.Sound(
            [Commit("feat", "one", "a.cs", "b.cs"), Commit("fix", "two", "b.cs")], scope);

        var only = Assert.Single(sound);
        Assert.Equal(["a.cs", "b.cs"], only.Files);
    }

    /// <summary>
    /// Nothing to commit stays nothing to commit — an empty scope must not produce the sweep-up chore,
    /// which would be a commit over no files at all.
    /// </summary>
    [Fact]
    public void An_empty_scope_produces_no_commits()
    {
        Assert.Empty(GoalCommitPlan.Sound([Commit("feat", "x", "a.cs")], GoalCommitScope.Empty));
        Assert.Empty(GoalCommitPlan.Sound([], GoalCommitScope.Empty));
    }

    /// <summary>
    /// The dialog says what is still outstanding, because this is the moment it matters.
    /// </summary>
    /// <remarks>
    /// The offer needs no blockers and no errors — not a clean review — so a run can reach here with
    /// warnings unfixed. That is allowed, and a commit is exactly when somebody should decide whether
    /// it is all right. By then the transcript has been scrolled past.
    /// </remarks>
    [Fact]
    public void The_dialog_names_the_findings_nobody_fixed_and_the_files_left_alone()
    {
        var scope = Scope(["src/Cart.cs"], "src/Theirs.cs");
        var sound = GoalCommitPlan.Sound([Commit("feat", "apply discounts", "src/Cart.cs")], scope);

        var asked = GoalCommitPlan.Describe(sound, scope, warnings: 3, suggestions: 1);

        Assert.Contains("feat: apply discounts", asked);
        Assert.Contains("3 warnings", asked);
        Assert.Contains("1 suggestion", asked);
        Assert.Contains("src/Theirs.cs", asked);
        Assert.Contains("already changed before this goal started", asked);
        Assert.EndsWith("Commit?", asked);

        // A clean review says nothing about findings rather than saying there are none of them: four
        // zeroes is the shape the status strip was deliberately kept out of, for the same reason.
        var clean = GoalCommitPlan.Describe(sound, Scope(["src/Cart.cs"]), 0, 0);
        Assert.DoesNotContain("unfixed", clean);
        Assert.DoesNotContain("already changed", clean);
    }

    /// <summary>
    /// What the transcript records afterwards names only the commits that were actually made, and how
    /// to take them back.
    /// </summary>
    /// <remarks>
    /// The count matters because <c>GoalCommitter</c> stops at the first refusal — a pre-commit hook
    /// that rejects the second will reject the third — so a plan of three can leave one behind, and
    /// listing all three would tell the user their history holds something it does not.
    /// </remarks>
    [Fact]
    public void The_record_lists_what_was_committed_and_how_to_undo_exactly_that()
    {
        IReadOnlyList<GoalCommit> planned =
            [Commit("feat", "one", "a.cs"), Commit("fix", "two", "b.cs"), Commit("docs", "three", "c.cs")];

        var said = GoalCommitPlan.Made(planned, made: 2, GoalCommitScope.Empty);

        Assert.Contains("feat: one", said);
        Assert.Contains("fix: two", said);
        Assert.DoesNotContain("docs: three", said);
        Assert.Contains("git reset --soft HEAD~2", said);

        Assert.Equal("Nothing was committed.", GoalCommitPlan.Made(planned, 0, GoalCommitScope.Empty));
    }

    /// <summary>
    /// The plan is read out of the tool's json, and an entry that would commit nothing is dropped
    /// rather than repaired: git refuses an empty commit anyway, and dropping it here means the user
    /// hears about files nothing claimed instead of about a git error.
    /// </summary>
    [Fact]
    public void A_commit_plan_is_read_from_the_tools_json_and_entries_with_no_files_are_dropped()
    {
        var parsed = GoalResponseParser.ParseCommitPlan("""
            Here is how I would split it.

            ```json
            {"commits":[
              {"type":"Feat","subject":"apply discounts","files":["src/Cart.cs"]},
              {"type":"chore","subject":"nothing at all","files":[]}
            ]}
            ```
            """);

        var only = Assert.Single(parsed);

        // Lower-cased, because `Feat:` beside forty `feat:` ones is something a person then fixes by
        // hand.
        Assert.Equal("feat", only.Type);
        Assert.Equal("feat: apply discounts", only.Message);
    }

    /// <summary>
    /// A path the tool spelled with backslashes is the same path.
    /// </summary>
    /// <remarks>
    /// The scope comes from <c>git diff --name-only</c>, and git speaks forward slashes on every
    /// platform. A model writing about a Windows checkout is under no such rule. Compared as bytes,
    /// <c>src\Cart.cs</c> matched nothing, so a perfectly good plan was thrown away as invented and
    /// the run's work went into the sweeping chore with no grouping at all — which reads as the tool
    /// having ignored its instructions.
    /// </remarks>
    [Fact]
    public void A_path_the_tool_spelled_the_windows_way_is_the_path_it_named()
    {
        var scope = Scope(["src/Cart.cs"]);

        var only = Assert.Single(GoalCommitPlan.Sound(
            [Commit("feat", "discounts", "src\\Cart.cs")], scope));

        Assert.Equal("feat", only.Type);
        Assert.Equal(["src/Cart.cs"], only.Files);
    }

    /// <summary>
    /// A plan in which nothing survived is refused rather than swept into one commit of everything.
    /// </summary>
    /// <remarks>
    /// The sweep exists to finish a usable plan, not to replace a missing one. Applied to an empty
    /// plan — an answer that could not be read — or to one naming only files this run had no right to
    /// touch, it turned the whole of the run's work into a single commit under a subject nobody wrote,
    /// and did it while the caller's "the tool did not come back with a usable set of commits" branch
    /// sat unreachable. Both cases are here because they arrive at the same place by different roads.
    /// </remarks>
    [Fact]
    public void A_plan_that_claimed_nothing_is_refused_rather_than_swept()
    {
        var scope = Scope(["src/Cart.cs", "src/Prices.cs"]);

        Assert.Empty(GoalCommitPlan.Sound([], scope));
        Assert.Empty(GoalCommitPlan.Sound([Commit("feat", "invented", "src/NotOurs.cs")], scope));

        // And the sweep still runs the moment one line of the plan is usable.
        Assert.Equal(2, GoalCommitPlan.Sound([Commit("feat", "prices", "src/Prices.cs")], scope).Count);
    }

    /// <summary>
    /// An answer with no plan in it commits nothing, and there is deliberately no prose fallback.
    /// </summary>
    /// <remarks>
    /// Unlike the review, which must be given a verdict or the run cannot continue, a commit plan that
    /// cannot be read can simply not be made — the work stays in the working tree exactly as it was.
    /// Guessing a grouping out of prose would be inventing commit messages for somebody's repository.
    /// </remarks>
    [Fact]
    public void An_unreadable_answer_commits_nothing()
    {
        Assert.Empty(GoalResponseParser.ParseCommitPlan("I think three commits would be sensible."));
        Assert.Empty(GoalResponseParser.ParseCommitPlan("```json\n{\"other\":1}\n```"));
        Assert.Empty(GoalResponseParser.ParseCommitPlan(null));
    }

    // ── The upper end of the scope ──────────────────────

    /// <summary>
    /// A run with no closing snapshot says so, before the list rather than after it.
    /// </summary>
    /// <remarks>
    /// The last text the user reads before agreeing, and the only place they are told the scope has no
    /// upper end — so what follows is "everything that changed since the goal started", another Goal
    /// tile's finished work included. It goes before the list because it changes what the list means.
    /// </remarks>
    [Fact]
    public void An_unbounded_scope_warns_before_it_lists_anything()
    {
        var scope = new GoalCommitScope(["src/Cart.cs"], [], Bounded: false);
        var sound = GoalCommitPlan.Sound([Commit("feat", "apply discounts", "src/Cart.cs")], scope);

        var asked = GoalCommitPlan.Describe(sound, scope, 0, 0);

        Assert.Contains("Nothing recorded how the tree looked when this run finished", asked);
        Assert.True(
            asked.IndexOf("Nothing recorded", StringComparison.Ordinal)
            < asked.IndexOf("feat: apply discounts", StringComparison.Ordinal),
            "the warning came after the list it changes the meaning of");
    }

    /// <summary>A bounded run says nothing about it, rather than saying there is nothing to say.</summary>
    [Fact]
    public void A_bounded_scope_says_nothing_about_its_upper_end() =>
        Assert.DoesNotContain("Nothing recorded",
            GoalCommitPlan.Describe(
                GoalCommitPlan.Sound([Commit("feat", "x", "src/Cart.cs")], Scope(["src/Cart.cs"])),
                Scope(["src/Cart.cs"]), 0, 0));

    /// <summary>
    /// Files written again after the run finished are named, under their own reason.
    /// </summary>
    /// <remarks>
    /// The other end of the rule the pre-existing list covers, and the one a workspace with three Goal
    /// tiles meets: this run wrote the file and somebody has written it since, so what would go in is
    /// not only this run's work. A different sentence, because a different thing happened.
    /// </remarks>
    [Fact]
    public void Files_changed_after_the_run_are_named_under_their_own_reason()
    {
        var scope = new GoalCommitScope(
            ["src/Cart.cs"], [], TouchedSince: ["src/Later.cs"]);
        var sound = GoalCommitPlan.Sound([Commit("feat", "apply discounts", "src/Cart.cs")], scope);

        var asked = GoalCommitPlan.Describe(sound, scope, 0, 0);

        Assert.Contains("src/Later.cs", asked);
        Assert.Contains("changed after this run finished", asked);
        Assert.DoesNotContain("already changed before this goal started", asked);
    }

    /// <summary>Both lists at once are two sentences, not one merged one.</summary>
    [Fact]
    public void The_two_reasons_a_file_is_held_back_are_kept_apart()
    {
        var scope = new GoalCommitScope(
            ["src/Cart.cs"], ["src/Theirs.cs"], TouchedSince: ["src/Later.cs"]);
        var sound = GoalCommitPlan.Sound([Commit("feat", "apply discounts", "src/Cart.cs")], scope);

        var asked = GoalCommitPlan.Describe(sound, scope, 0, 0);

        Assert.Contains("already changed before this goal started", asked);
        Assert.Contains("changed after this run finished", asked);
    }

    /// <summary>What was written afterwards is repeated in the record, not only in the offer.</summary>
    /// <remarks>
    /// The transcript is what is left once the dialog is gone, and "this file may carry somebody else's
    /// work" is the part worth still being able to read tomorrow.
    /// </remarks>
    [Fact]
    public void The_record_repeats_what_was_changed_after_the_run()
    {
        var planned = new[] { Commit("feat", "apply discounts", "src/Cart.cs") };

        var said = GoalCommitPlan.Made(planned, made: 1,
            new GoalCommitScope(["src/Cart.cs"], [], TouchedSince: ["src/Later.cs"]));

        Assert.Contains("src/Later.cs", said);
        Assert.Contains("changed after this run finished", said);
    }

    /// <summary>
    /// Nothing to commit says which files were held back and why.
    /// </summary>
    /// <remarks>
    /// The sentence on its own reads as a claim about the user's work — as if the tool had done nothing
    /// — when what has usually happened is that every file it touched is also somebody else's. The
    /// closing snapshot makes this the ordinary outcome of two Goal tiles over one workspace, which is
    /// the case it was added for.
    /// </remarks>
    [Fact]
    public void Nothing_to_commit_names_what_was_held_back_and_why()
    {
        var said = GoalCommitPlan.Nothing(
            new GoalCommitScope([], ["src/Theirs.cs"], TouchedSince: ["src/Later.cs"]));

        Assert.Contains("nothing here this run can claim", said);
        Assert.Contains("src/Theirs.cs", said);
        Assert.Contains("already changed before this goal started", said);
        Assert.Contains("src/Later.cs", said);
        Assert.Contains("changed after this run finished", said);
    }

    /// <summary>With nothing held back either, it stays the one sentence.</summary>
    [Fact]
    public void Nothing_at_all_is_still_one_sentence() =>
        Assert.Equal(
            "There is nothing here this run can claim as its own to commit.",
            GoalCommitPlan.Nothing(GoalCommitScope.Empty));

    /// <summary>
    /// A detected goal with no closing snapshot says one thing, not two contradictory ones.
    /// </summary>
    /// <remarks>
    /// The two warnings were written independently and both fired in this state: one said the list was
    /// the tree "when this run finished", the next said nothing had recorded how the tree looked when
    /// it finished. The first is precisely what is unknown here, so it gives way — and the surviving
    /// sentence still carries the part the detect path needs, that the goal was about work already in
    /// the tree.
    /// </remarks>
    [Fact]
    public void A_detected_goal_with_no_upper_end_does_not_claim_to_know_where_it_ended()
    {
        var scope = new GoalCommitScope(["src/Cart.cs"], [], Bounded: false);
        var sound = GoalCommitPlan.Sound([Commit("feat", "apply discounts", "src/Cart.cs")], scope);

        var asked = GoalCommitPlan.Describe(sound, scope, 0, 0, existingWork: true);

        Assert.DoesNotContain("uncommitted when this run finished", asked);
        Assert.Contains("worked out from the changes already in the tree", asked);
        Assert.Contains("everything uncommitted right now", asked);
        Assert.Contains("cannot be told apart", asked);
    }

    /// <summary>With an end recorded it says the bounded thing, and only that.</summary>
    [Fact]
    public void A_detected_goal_that_knows_where_it_ended_says_so_once()
    {
        var scope = new GoalCommitScope(["src/Cart.cs"], []);
        var sound = GoalCommitPlan.Sound([Commit("feat", "apply discounts", "src/Cart.cs")], scope);

        var asked = GoalCommitPlan.Describe(sound, scope, 0, 0, existingWork: true);

        Assert.Contains("uncommitted when this run finished", asked);
        Assert.DoesNotContain("Nothing recorded", asked);
        Assert.DoesNotContain("right now", asked);
    }
}