using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// How a Goal run splits into jobs, and how much thinking each one is bought.
/// </summary>
/// <remarks>
/// A table test because the rule is an opinion about how to spend somebody's money — the same
/// construction <see cref="ChainPolicy"/> and <c>UsagePace</c> get, and for the same reason: it has to
/// be readable and arguable without a tile, a process or a settings file.
/// </remarks>
public class GoalRolesTests
{
    /// <summary>
    /// A phase says which job it is, and the commit is the one job no phase names.
    /// </summary>
    /// <remarks>
    /// The commit plan is asked for during whichever phase the run happens to be in, so derived from
    /// the phase it would be attributed to the planner or — most often — the reviewer, and run at that
    /// slot's effort. That is why its caller names the role instead.
    /// </remarks>
    [Theory]
    [InlineData(GoalPhase.Goal, GoalRole.Planning)]
    [InlineData(GoalPhase.Clarify, GoalRole.Planning)]
    [InlineData(GoalPhase.Plan, GoalRole.Planning)]
    [InlineData(GoalPhase.Summary, GoalRole.Planning)]
    [InlineData(GoalPhase.Implement, GoalRole.Work)]
    [InlineData(GoalPhase.Review, GoalRole.Review)]
    public void A_phase_says_which_job_it_is(GoalPhase phase, GoalRole expected) =>
        Assert.Equal(expected, GoalRoles.For(phase));

    /// <summary>
    /// The default spends the thinking on the planning and the review, and gets on with the work.
    /// </summary>
    /// <remarks>
    /// <para><b>This reverses an earlier decision and the reversal is the point</b> — see
    /// <c>docs/adr/0003-effort-by-role-in-a-goal-run.md</c>. The old rule was one level for everything,
    /// defaulting to <c>high</c>, on the argument that the budget is in attempts so a shallow attempt
    /// costs as much of it as a careful one. What is measured says otherwise for the *implementing*
    /// phase in particular: higher reasoning effort on agentic coding benchmarks saturates and
    /// sometimes reverses, the extra spend going into re-reading and re-editing the same files; and a
    /// cheaper author's mistakes are the ones a reviewer actually catches, while a stronger author
    /// produces internally consistent wrong answers that verifiers wave through.</para>
    /// <para>What the evidence does <em>not</em> support is spending it on the review instead: under a
    /// fixed budget, generation beats verification by a wide margin. So what pays for the review is a
    /// second <em>agent</em> rather than a deeper one — but the level it runs at is still
    /// <c>high</c>, and that is this repository's own measurement rather than an opinion: twenty-one
    /// reviews at <c>medium</c> returned one finding or none with eight empty, against twenty-one at
    /// <c>high</c> where none was empty and the median was four (<c>docs/GOAL.md</c>). An empty review
    /// passes <c>RequireGoalMet</c> on the first attempt and reports an unfixed bug as a met goal.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(GoalEffortPreset.Balanced, GoalRole.Planning, AiEffort.Medium)]
    [InlineData(GoalEffortPreset.Balanced, GoalRole.Work, AiEffort.Low)]
    [InlineData(GoalEffortPreset.Balanced, GoalRole.Review, AiEffort.Medium)]
    // `careful` is the old `balanced`, levels unchanged, one rung up.
    [InlineData(GoalEffortPreset.Careful, GoalRole.Planning, AiEffort.Medium)]
    [InlineData(GoalEffortPreset.Careful, GoalRole.Work, AiEffort.Low)]
    [InlineData(GoalEffortPreset.Careful, GoalRole.Review, AiEffort.High)]
    [InlineData(GoalEffortPreset.Thorough, GoalRole.Planning, AiEffort.High)]
    [InlineData(GoalEffortPreset.Thorough, GoalRole.Work, AiEffort.Medium)]
    [InlineData(GoalEffortPreset.Thorough, GoalRole.Review, AiEffort.High)]
    [InlineData(GoalEffortPreset.Cheap, GoalRole.Planning, AiEffort.Low)]
    [InlineData(GoalEffortPreset.Cheap, GoalRole.Work, AiEffort.Low)]
    [InlineData(GoalEffortPreset.Cheap, GoalRole.Review, AiEffort.Low)]
    public void A_preset_buys_each_job_its_own_amount_of_thinking(
        GoalEffortPreset preset, GoalRole role, AiEffort expected) =>
        Assert.Equal(expected, GoalRoles.EffortFor(preset, role));

    /// <summary>
    /// The commit is cheap under every preset, and there is nowhere to say otherwise.
    /// </summary>
    /// <remarks>
    /// Deciding which of the changed files belong together and writing a subject line for them is
    /// mechanical work over a list this application has already computed. Being a constant rather than
    /// a fourth row in each preset is what makes that true by construction: no preset, and no field on
    /// any screen, can set it wrong.
    /// </remarks>
    [Fact]
    public void The_commit_is_always_cheap_and_no_preset_can_change_it()
    {
        foreach (var preset in GoalRoles.All.Where(p => p != GoalEffortPreset.ToolDefault))
            Assert.Equal(AiEffort.Low, GoalRoles.EffortFor(preset, GoalRole.Commit));

        // The one exception, and it is the preset's whole meaning: "pass no flag" has to reach every
        // run, or a machine whose CLI predates the flag still fails on the commit alone.
        Assert.Equal(AiEffort.ToolDefault,
            GoalRoles.EffortFor(GoalEffortPreset.ToolDefault, GoalRole.Commit));

        // And the commit is not one of the jobs a preset row describes: a row nothing the user does
        // here can change is a row that should not be in front of them.
        Assert.DoesNotContain(GoalRole.Commit, GoalRoles.Described);
    }

    /// <summary>Every preset passes no flag at all when that is what was asked for.</summary>
    [Theory]
    [InlineData(GoalRole.Planning)]
    [InlineData(GoalRole.Work)]
    [InlineData(GoalRole.Review)]
    public void The_default_preset_passes_no_flag_for_any_job(GoalRole role) =>
        Assert.Equal(AiEffort.ToolDefault, GoalRoles.EffortFor(GoalEffortPreset.ToolDefault, role));

    /// <summary>
    /// A word round-trips, and every row says what its one word stands for.
    /// </summary>
    /// <remarks>The description is the whole reason the strip can carry one picker where the feature
    /// has three levels: a row that did not spell them out would be a word nobody could act on.
    /// </remarks>
    [Fact]
    public void Each_preset_is_one_word_that_spells_its_levels_out()
    {
        foreach (var preset in GoalRoles.All)
        {
            Assert.Equal(preset, GoalRoles.FromLabel(GoalRoles.Label(preset)));
            Assert.False(string.IsNullOrWhiteSpace(GoalRoles.Description(preset)));
        }

        Assert.Equal("plan medium · work low · review medium",
            GoalRoles.Description(GoalEffortPreset.Balanced));

        // The rung above it is the old `balanced` under its new name, so the two rows differ in the
        // review and nowhere else — which is what makes one word enough to choose between them.
        Assert.Equal("plan medium · work low · review high",
            GoalRoles.Description(GoalEffortPreset.Careful));

        // Unrecognised is the default rather than an exception while a tile is being built — the rule
        // AiEfforts.FromLabel follows, and for the same reason.
        Assert.Equal(GoalEffortPreset.Balanced, GoalRoles.FromLabel("something else entirely"));
        Assert.Equal(GoalEffortPreset.Balanced, GoalRoles.FromLabel(null));
    }

    /// <summary>
    /// A single level set before presets existed becomes the preset it meant — except the old default,
    /// which meant nothing.
    /// </summary>
    /// <remarks>
    /// <para><c>high</c> was what a settings file carried for having been written at all, so it is
    /// indistinguishable from a file nobody touched and reads as the new default rather than as
    /// somebody asking for high everywhere. That is the rule
    /// <c>SettingsService.AdoptEmbeddedFont</c> already follows.</para>
    /// <para>Everything else was typed on purpose and is honoured in the direction it was typed.
    /// Nobody's runs get dearer than what they asked for; only the people who never chose get the new,
    /// cheaper default.</para>
    /// </remarks>
    [Theory]
    [InlineData(AiEffort.Low, GoalEffortPreset.Cheap)]
    [InlineData(AiEffort.Medium, GoalEffortPreset.Balanced)]
    [InlineData(AiEffort.High, GoalEffortPreset.Balanced)]
    [InlineData(AiEffort.XHigh, GoalEffortPreset.Thorough)]
    [InlineData(AiEffort.Max, GoalEffortPreset.Thorough)]
    [InlineData(AiEffort.ToolDefault, GoalEffortPreset.ToolDefault)]
    public void An_old_single_level_becomes_the_preset_it_meant(
        AiEffort stored, GoalEffortPreset expected) =>
        Assert.Equal(expected, GoalRoles.FromLegacyEffort(stored));

    /// <summary>A settings file that has never been touched runs balanced, and carries no old level.
    /// </summary>
    [Fact]
    public void A_fresh_settings_file_is_balanced()
    {
        var settings = new AppSettings();

        Assert.Equal(GoalEffortPreset.Balanced, settings.GoalEffortPreset);

        // Null rather than a level, so "never said" and "said high" stay different answers — which is
        // the whole of the migration above.
        Assert.Null(settings.LegacyGoalEffort);
    }
}
