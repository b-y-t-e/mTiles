using System.Text.Json;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The permission mode as a value that is written to a file, shown in a combo box and handed to
/// somebody else's CLI — three contracts, each with its own way of going quietly wrong.
/// </summary>
public class GoalPermissionModeTests
{
    /// <summary>
    /// A mode this build has never heard of must not cost the user their settings file.
    /// </summary>
    /// <remarks>
    /// The route in is a downgrade: pick <c>bypassPermissions</c>, let Velopack roll back a version,
    /// and the name in the file is unknown to the running build. Without the tolerant converter that is
    /// a <c>JsonException</c>, which <c>SettingsService.Load</c> rightly reads as a damaged file and
    /// sets aside — taking the shell profiles, the AI tool paths and the DPAPI-encrypted database
    /// passwords with it, over one word describing one tile's appetite for unattended edits.
    /// </remarks>
    [Theory]
    [InlineData("\"somethingFromTheFuture\"")]
    [InlineData("99")]
    [InlineData("null")]
    [InlineData("{}")]
    public void An_unknown_permission_mode_reads_as_the_default_rather_than_throwing(string written)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            $$"""{ "GoalPermissionMode": {{written}}, "GitPath": "kept" }""", JsonDefaults.SettingsOptions)!;

        Assert.Equal(AiBehaviour.Auto, settings.GoalPermissionMode);

        // And the rest of the file survives, which is the whole point of not throwing.
        Assert.Equal("kept", settings.GitPath);
    }

    /// <summary>
    /// An agent instance's own two settings are read as tolerantly, because they are the same two types
    /// in the same file and the AI page writes the whole vocabulary into them.
    /// </summary>
    /// <remarks>The behaviour falls to <see cref="AiBehaviour.ToolDefault"/> rather than to the file's
    /// <c>Auto</c>: an answer that cannot be read must never come back more permissive than the one it
    /// replaced.</remarks>
    [Theory]
    [InlineData("\"somethingFromTheFuture\"")]
    [InlineData("99")]
    [InlineData("null")]
    public void An_unknown_instance_behaviour_or_effort_costs_nothing_else(string written)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            $$"""
              {
                "AiAgentInstances": [
                  { "Name": "Mine", "DefaultBehaviour": {{written}}, "DefaultEffort": {{written}} }
                ],
                "GitPath": "kept"
              }
              """, JsonDefaults.SettingsOptions)!;

        var instance = Assert.Single(settings.AiAgentInstances);
        Assert.Equal("Mine", instance.Name);
        Assert.Equal(AiBehaviour.ToolDefault, instance.DefaultBehaviour);
        Assert.Equal(AiEffort.High, instance.DefaultEffort);
        Assert.Equal("kept", settings.GitPath);
    }

    [Fact]
    public void A_mode_this_build_does_know_is_read_as_itself()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{ "GoalPermissionMode": "BypassPermissions" }""", JsonDefaults.SettingsOptions)!;

        Assert.Equal(AiBehaviour.BypassPermissions, settings.GoalPermissionMode);
    }

    /// <summary>
    /// Every label maps back to the mode it came from.
    /// </summary>
    /// <remarks>
    /// The combo box binds by <em>string</em>, and <c>FromLabel</c> falls back to <c>Auto</c> without
    /// complaining. A typo in one label would therefore degrade that choice silently: pick "bypass",
    /// get "auto", with nothing anywhere saying so — and the one mode where the difference matters most
    /// is the one whose label is least like its name.
    /// </remarks>
    [Fact]
    public void Every_label_round_trips_to_its_own_mode()
    {
        foreach (var mode in AiBehaviours.All)
            Assert.Equal(mode, AiBehaviours.FromLabel(AiBehaviours.Label(mode)));
    }

    /// <summary>
    /// The strip offers only the modes a run with nobody watching can carry out.
    /// </summary>
    /// <remarks>
    /// The three that are missing each fail in their own way headlessly: <c>ask</c> denies every tool
    /// call, <c>accept edits</c> denies every one that is not an edit while looking like it is working,
    /// and <c>plan</c> leaves the implement phase unable to write a file while the loop spends its
    /// attempts on "the last attempt changed no files". They are in the vocabulary because an
    /// interactive session and the read-only phases use them; they are not choices to put in front of
    /// somebody configuring a goal loop.
    /// </remarks>
    [Fact]
    public void The_strip_does_not_offer_a_mode_a_headless_run_cannot_carry_out()
    {
        Assert.DoesNotContain(AiBehaviour.Ask, AiBehaviours.Headless);
        Assert.DoesNotContain(AiBehaviour.AcceptEdits, AiBehaviours.Headless);
        Assert.DoesNotContain(AiBehaviour.Plan, AiBehaviours.Headless);

        Assert.Contains(AiBehaviour.Auto, AiBehaviours.Headless);
        Assert.Contains(AiBehaviour.ToolDefault, AiBehaviours.Headless);

        Assert.Equal(AiBehaviours.Headless.Select(AiBehaviours.Label), AiBehaviours.HeadlessLabels);
    }

    /// <summary>
    /// The flags are somebody else's CLI contract, and they belong to the agent that speaks them.
    /// </summary>
    /// <remarks>Asked of <c>ClaudeAgent</c> rather than of <see cref="AiBehaviours"/>, which is where
    /// these spellings used to live under a neutral name — and that is precisely how a second agent
    /// came to be given the first agent's flags. <c>ToolDefault</c> passes no flag at all, which is not
    /// the same as passing an empty one.</remarks>
    [Fact]
    public void The_flags_are_the_ones_the_tool_accepts()
    {
        var claude = new ClaudeAgent();

        Assert.Equal(["--permission-mode", "auto"],
            claude.BehaviourArgs(AiBehaviour.Auto, TestUsage.Implementing));
        Assert.Equal(["--permission-mode", "acceptEdits"],
            claude.BehaviourArgs(AiBehaviour.AcceptEdits, TestUsage.Implementing));
        Assert.Equal(["--permission-mode", "bypassPermissions"],
            claude.BehaviourArgs(AiBehaviour.BypassPermissions, TestUsage.Implementing));
        Assert.Empty(claude.BehaviourArgs(AiBehaviour.ToolDefault, TestUsage.Implementing));
    }

    [Theory]
    // What an older Claude Code prints for a mode it has never heard of. Every run of the tile fails
    // this way, on the default setting, and the old message named no cause at all.
    [InlineData("error: option '--permission-mode <mode>' argument 'auto' is invalid. Allowed choices are default, acceptEdits.")]
    [InlineData("Unknown value for --permission-mode")]
    [InlineData("usage: claude [options]\n  --permission-mode <mode>")]
    public void A_rejected_mode_is_recognised(string output) =>
        Assert.True(AiBehaviours.LooksLikeRejectedMode(output, "--permission-mode", "--effort"));

    /// <summary>
    /// A tool that refuses one flag prints the usage for all of them, and only one of them was refused.
    /// </summary>
    /// <remarks>
    /// The whole reason the match is per line. Both matchers used to scan the whole of stdout for
    /// "this flag appears" and "a word like unknown appears", so a full usage dump — which lists every
    /// option the tool has — satisfied both, and whichever was asked first took the blame. The advice
    /// then named a setting that had nothing to do with the failure while the one that did went
    /// unmentioned.
    /// </remarks>
    [Fact]
    public void A_usage_dump_blames_the_flag_the_error_line_names_and_no_other()
    {
        const string refusedEffort = """
            error: unknown option '--effort'
            usage: claude [options]
              --permission-mode <mode>   Permission mode to use for the session
              --effort <level>           Effort level for the current session
            """;

        Assert.True(AiEfforts.LooksLikeRejectedEffort(refusedEffort, "--effort", "--permission-mode"));
        Assert.False(AiBehaviours.LooksLikeRejectedMode(refusedEffort, "--permission-mode", "--effort"));

        const string refusedMode = """
            error: unknown option '--permission-mode'
            usage: claude [options]
              --permission-mode <mode>   Permission mode to use for the session
              --effort <level>           Effort level for the current session
            """;

        Assert.True(AiBehaviours.LooksLikeRejectedMode(refusedMode, "--permission-mode", "--effort"));
        Assert.False(AiEfforts.LooksLikeRejectedEffort(refusedMode, "--effort", "--permission-mode"));
    }

    [Theory]
    // The failure was about the work. Naming the permission mode here would send somebody to a setting
    // that cannot help, which is worse than the generic sentence.
    [InlineData("Error: ENOENT: no such file or directory, open 'src/Cart.cs'")]
    [InlineData("Claude requested permissions to use Edit, but you haven't granted it yet.")]
    [InlineData("")]
    [InlineData(null)]
    public void An_ordinary_failure_is_not_blamed_on_the_permission_mode(string? output) =>
        Assert.False(AiBehaviours.LooksLikeRejectedMode(output, "--permission-mode", "--effort"));

    /// <summary>
    /// On the tile's <em>default</em> criteria the review is not read-only, and that is the documented
    /// exception rather than a gap.
    /// </summary>
    /// <remarks><c>RequireBuild</c> and <c>RequireTestsPass</c> both default to on, and a build writes
    /// into <c>obj/</c> and <c>bin/</c> — so a review told to establish them cannot be sandboxed to
    /// reading. Pinned because the claim reads the other way round at a glance: what keeps that agent
    /// off the source is the review prompt and <c>GoalBaseline</c>, not the sandbox, and turning both
    /// criteria off is what makes it read-only.</remarks>
    [Fact]
    public void The_default_criteria_let_the_review_run_the_project_and_therefore_write()
    {
        var criteria = new GoalCompletionCriteria();
        Assert.True(criteria.RequireBuild || criteria.RequireTestsPass);

        var withDefaults = AiUsage.Headless(GoalPhase.Review,
            criteria.RequireBuild || criteria.RequireTestsPass);

        Assert.True(withDefaults.RunsProjectCommands);
        Assert.False(withDefaults.MayOnlyRead);
        Assert.Equal(["--sandbox", "workspace-write"],
            new CodexAgent().BehaviourArgs(AiBehaviour.Auto, withDefaults));

        // And with nothing to establish, the same phase is held to reading — by the agent, not by a
        // sentence in a prompt.
        var readingOnly = AiUsage.Headless(GoalPhase.Review);
        Assert.True(readingOnly.MayOnlyRead);
        Assert.Equal(["--sandbox", "read-only"],
            new CodexAgent().BehaviourArgs(AiBehaviour.BypassPermissions, readingOnly));
    }
}
