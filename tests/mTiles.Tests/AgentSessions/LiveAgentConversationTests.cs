using System.Diagnostics;
using mTiles.AgentSessions.Checkpoints;
using mTiles.AgentSessions.Commands;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Hosting;
using mTiles.AgentSessions.Storage;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Agents.Sessions;
using Xunit;
using Xunit.Abstractions;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// Each agent's session against its real CLI: one turn that writes a file, through the launcher, the
/// session, the host and a real checkpoint.
/// </summary>
/// <remarks>
/// <para><b>Opt-in, because it spends somebody's model calls</b> and needs the CLIs installed and signed
/// in. Set <c>MTILES_LIVE_AGENTS</c> to a comma-separated list of agent ids (<c>claude,codex,opencode,
/// pi,agy,grok</c>) and run <c>dotnet test --filter LiveAgentConversationTests</c>. Without it every case
/// passes at once and says it did nothing.</para>
/// <para>This is where a CLI that moved is found out. The mapper tests pin what was measured; this says
/// whether it is still true.</para>
/// </remarks>
public class LiveAgentConversationTests(ITestOutputHelper output)
{
    [LiveAgentTheory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("opencode")]
    [InlineData("pi")]
    [InlineData("agy")]
    [InlineData("grok")]
    public async Task One_turn_that_writes_a_file(string agentId)
    {
        var wanted = (Environment.GetEnvironmentVariable("MTILES_LIVE_AGENTS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!wanted.Contains(agentId, StringComparer.OrdinalIgnoreCase))
        {
            output.WriteLine($"Skipped: MTILES_LIVE_AGENTS does not name {agentId}.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"mtiles-live-{agentId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Git(root, "init -q");
        File.WriteAllText(Path.Combine(root, "README.md"), "live test\n");
        Git(root, "add -A");
        Git(root, "-c user.name=t -c user.email=t@t commit -q -m init");

        var agent = AiAgentCatalog.Find(agentId)!;
        var settings = new AppSettings();
        var instance = AiAgentCatalog.SeedInstanceFor(agent);
        // Auto by default; MTILES_LIVE_BEHAVIOUR=Ask drives the approval path instead, answered below.
        instance.DefaultBehaviour = Enum.TryParse<AiBehaviour>(Environment.GetEnvironmentVariable("MTILES_LIVE_BEHAVIOUR"), out var mode)
            ? mode
            : AiBehaviour.Auto;
        instance.DefaultEffort = AiEffort.Low;
        // A model for the agents whose CLI default does not work on this account — "opencode=openai/gpt-5.6-sol".
        foreach (var pair in (Environment.GetEnvironmentVariable("MTILES_LIVE_MODELS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (pair.Split('=', 2) is [var id, var model] && id.Equals(agentId, StringComparison.OrdinalIgnoreCase))
                instance.Model = model;

        var (launch, problem) = await AgentSessionLauncher.PrepareAsync(settings, agent, instance, root,
            Guid.NewGuid().ToString(), null, CancellationToken.None);
        Assert.True(launch is not null, problem);
        // The suite pretends every agent is installed (AiAgentCatalog.Locate); this one needs the real binary.
        var executable = mTiles.Services.ExecutableFinder.Anywhere(agent.BinaryName);
        Assert.True(executable is not null, $"{agent.BinaryName} is not installed.");
        launch = launch! with { ExecutablePath = executable! };

        var store = new SqliteConversationStore(Path.Combine(root, "..", $"{Path.GetFileName(root)}.db"));
        var host = new AgentConversationHost(
            new ConversationRecord(Guid.NewGuid().ToString(), agent.Id, root, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            store, new GitTurnCheckpoints(root));

        var turnDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Changed += (state, e) =>
        {
            output.WriteLine($"{e.GetType().Name}: {Describe(e)}");
            if (e is ApprovalRequested approval)
                _ = host.ExecuteAsync(new RespondToApproval(approval.RequestId,
                    approval.Options.Any(o => o.Decision == ApprovalDecision.AcceptForSession)
                        ? ApprovalDecision.AcceptForSession
                        : ApprovalDecision.Accept), CancellationToken.None);
            if (e is QuestionsAsked round)
                _ = host.ExecuteAsync(new AnswerQuestions(round.RequestId,
                    round.Questions.ToDictionary(q => q.Id, q => (IReadOnlyList<string>)[q.Options.FirstOrDefault()?.Label ?? "yes"])),
                    CancellationToken.None);
            if (e is CheckpointCaptured { BaseCheckpointId: not null }) turnDone.TrySetResult();
        };

        try
        {
            await host.StartAsync(sink => AgentSessionLauncher.Create(agent, launch!, sink), null, CancellationToken.None);
            Assert.NotEqual(AgentSessionState.Failed, host.State.SessionState);

            await host.ExecuteAsync(new SendMessage(
                "Create a file named hello.txt in the current directory containing exactly the word hi. " +
                "Then reply with the single word done."), CancellationToken.None);

            await turnDone.Task.WaitAsync(TimeSpan.FromMinutes(5));
            var state = host.State;

            foreach (var entry in state.Timeline) output.WriteLine($"  {entry}");
            Assert.True(File.Exists(Path.Combine(root, "hello.txt")), "The agent did not write hello.txt.");
            Assert.Contains(state.Timeline, e => e is WorkGroupEntry group && group.Items.OfType<ToolCallItem>().Any());
            Assert.Contains(state.Timeline, e => e is MessageEntry { Role: MessageRole.Assistant, Text.Length: > 0 });
            Assert.Contains(state.Timeline, e => e is CheckpointEntry checkpoint && checkpoint.Files.Any(f => f.Path == "hello.txt"));
            Assert.NotNull(host.ResumeToken);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>A red square, sixteen pixels a side.</summary>
    private const string RedSquarePng =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAGElEQVR4nGP4z8BAEiJN9aiGUQ0MQ0kDAJD5/wGaM2eTAAAAAElFTkSuQmCC";

    /// <summary>
    /// The session lists what it can switch to, takes a mode change while it runs (or asks for a restart),
    /// and an image sent with a message reaches the model.
    /// </summary>
    /// <remarks><c>MTILES_LIVE_SWITCH_MODEL</c> names a model to switch to for an agent
    /// (<c>claude=haiku;pi=openai-codex/gpt-5.5</c>); without it only the mode is switched.</remarks>
    [LiveAgentTheory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("opencode")]
    [InlineData("pi")]
    [InlineData("agy")]
    [InlineData("grok")]
    public async Task Switching_settings_and_sending_an_image(string agentId)
    {
        var wanted = (Environment.GetEnvironmentVariable("MTILES_LIVE_AGENTS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!wanted.Contains(agentId, StringComparer.OrdinalIgnoreCase))
        {
            output.WriteLine($"Skipped: MTILES_LIVE_AGENTS does not name {agentId}.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"mtiles-live-switch-{agentId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var agent = AiAgentCatalog.Find(agentId)!;
        var instance = AiAgentCatalog.SeedInstanceFor(agent);
        instance.DefaultBehaviour = AiBehaviour.Auto;
        instance.DefaultEffort = AiEffort.Low;
        foreach (var pair in (Environment.GetEnvironmentVariable("MTILES_LIVE_MODELS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (pair.Split('=', 2) is [var id, var model] && id.Equals(agentId, StringComparison.OrdinalIgnoreCase))
                instance.Model = model;
        var switchTo = (Environment.GetEnvironmentVariable("MTILES_LIVE_SWITCH_MODEL") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)).FirstOrDefault(pair => pair.Length == 2 && pair[0] == agentId)?[1];

        var (launch, problem) = await AgentSessionLauncher.PrepareAsync(new AppSettings(), agent, instance, root,
            Guid.NewGuid().ToString(), null, CancellationToken.None);
        Assert.True(launch is not null, problem);
        launch = launch! with { ExecutablePath = mTiles.Services.ExecutableFinder.Anywhere(agent.BinaryName)! };

        await using var host = new AgentConversationHost(
            new ConversationRecord(Guid.NewGuid().ToString(), agent.Id, root, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new SqliteConversationStore(Path.Combine(Path.GetTempPath(), $"{Path.GetFileName(root)}.db")), null);
        var turnDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartAsked = false;
        host.RestartRequested += _ => restartAsked = true;
        host.Changed += (_, e) =>
        {
            output.WriteLine($"{e.GetType().Name}: {Describe(e)}");
            if (e is TurnCompleted) turnDone.TrySetResult();
        };

        await host.StartAsync(sink => AgentSessionLauncher.Create(agent, launch, sink), null, CancellationToken.None);
        var options = host.State.Options;
        Assert.NotNull(options);
        output.WriteLine($"models offered: {options!.Models.Count} ({string.Join(", ", options.Models.Take(5).Select(m => m.Id))})");
        output.WriteLine($"modes offered: {string.Join(", ", options.Modes.Select(m => m.Id))}");

        // Not Plan: a planning agent answers "what colour is this" with a remark about planning.
        var otherMode = options.Modes.Select(m => m.Id)
            .FirstOrDefault(id => id != host.State.Mode && id is not ("ToolDefault" or "Plan"));
        await host.ExecuteAsync(new ChangeSessionSettings(new SessionSettings(Model: switchTo, Mode: otherMode)), CancellationToken.None);
        output.WriteLine($"after the change: model={host.State.Model} mode={host.State.Mode} restartAsked={restartAsked}");
        if (!restartAsked && otherMode is not null) Assert.Equal(otherMode, host.State.Mode);

        if (restartAsked) return; // The owner would start the session again; nothing more to see in this host.

        await host.ExecuteAsync(new SendMessage("What colour is this image? Answer with one word.",
            [new ImageAttachment("image/png", RedSquarePng, "red.png")]), CancellationToken.None);
        await turnDone.Task.WaitAsync(TimeSpan.FromMinutes(4));

        var reply = string.Join(" | ", host.State.Timeline.OfType<MessageEntry>()
            .Where(m => m.Role == MessageRole.Assistant).Select(m => m.Text));
        output.WriteLine($"reply: {reply}");
        if (agentId != "agy") Assert.Matches(@"(?i)\bred\b", reply);
    }

    /// <summary>
    /// A sub-agent launched in the background keeps the conversation busy after the turn that launched it has
    /// ended — and Claude Code answering it by itself afterwards is a turn of its own. codex does not wake when
    /// its sub-agent finishes (measured, 0.156.1), so there the sub-agent's end is the end of the test.
    /// </summary>
    [LiveAgentTheory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task A_background_sub_agent_is_work_after_its_turn(string agentId)
    {
        var wanted = (Environment.GetEnvironmentVariable("MTILES_LIVE_AGENTS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!wanted.Contains(agentId, StringComparer.OrdinalIgnoreCase))
        {
            output.WriteLine($"Skipped: MTILES_LIVE_AGENTS does not name {agentId}.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"mtiles-live-sub-{agentId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Git(root, "init -q");
        File.WriteAllText(Path.Combine(root, "README.md"),
            string.Concat(Enumerable.Range(1, 40).Select(i => $"## Heading {i}\ntext {i}\n")));
        Git(root, "add -A");
        Git(root, "-c user.name=t -c user.email=t@t commit -q -m init");

        var agent = AiAgentCatalog.Find(agentId)!;
        var instance = AiAgentCatalog.SeedInstanceFor(agent);
        instance.DefaultBehaviour = AiBehaviour.BypassPermissions;
        instance.DefaultEffort = AiEffort.Low;
        foreach (var pair in (Environment.GetEnvironmentVariable("MTILES_LIVE_MODELS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (pair.Split('=', 2) is [var id, var model] && id.Equals(agentId, StringComparison.OrdinalIgnoreCase))
                instance.Model = model;

        var (launch, problem) = await AgentSessionLauncher.PrepareAsync(new AppSettings(), agent, instance, root,
            Guid.NewGuid().ToString(), null, CancellationToken.None);
        Assert.True(launch is not null, problem);
        var executable = mTiles.Services.ExecutableFinder.Anywhere(agent.BinaryName);
        Assert.True(executable is not null, $"{agent.BinaryName} is not installed.");
        launch = launch! with { ExecutablePath = executable! };

        var store = new SqliteConversationStore(Path.Combine(root, "..", $"{Path.GetFileName(root)}.db"));
        var host = new AgentConversationHost(
            new ConversationRecord(Guid.NewGuid().ToString(), agent.Id, root, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            store, new GitTurnCheckpoints(root));

        var wakesUp = agentId == "claude";
        var busyWithNoTurn = false;
        var turnsEnded = 0;
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Changed += (state, e) =>
        {
            output.WriteLine($"{e.GetType().Name}: {Describe(e)}");
            if (state is { IsWorking: false, IsBusy: true }) busyWithNoTurn = true;
            if (e is TurnCompleted) Interlocked.Increment(ref turnsEnded);
            if (e is TurnCompleted or SubAgentEnded && turnsEnded >= (wakesUp ? 2 : 1) && !state.IsBusy
                && state.SubAgents.Count > 0) answered.TrySetResult();
        };

        try
        {
            await host.StartAsync(sink => AgentSessionLauncher.Create(agent, launch!, sink), null, CancellationToken.None);
            var launching = wakesUp
                ? "Use the Agent tool with run_in_background set to true (subagent_type general-purpose) to have a " +
                  "background agent read README.md and count the lines starting with '## '."
                : "Spawn one sub-agent (use your spawn_agent tool) and ask it to read README.md and count the " +
                  "lines starting with '## '.";
            await host.ExecuteAsync(new SendMessage(launching +
                " Do not wait for it: right after launching, reply with just the word LAUNCHED and end your turn. " +
                "When the background agent finishes, tell me its count in one sentence."), CancellationToken.None);

            await answered.Task.WaitAsync(TimeSpan.FromMinutes(5));
            var final = host.State;
            Assert.True(busyWithNoTurn, "The conversation was never busy with no turn open.");
            var subAgent = Assert.Single(final.SubAgents);
            Assert.Equal(SubAgentStatus.Completed, subAgent.Status);
            if (wakesUp)
                Assert.Contains(final.Timeline, e => e is MessageEntry { Role: MessageRole.Assistant } m && m.Text.Contains("40"));
            else
                Assert.Contains("40", subAgent.Result);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>
    /// What a sub-agent asks reaches the tile.
    /// </summary>
    /// <remarks>opencode runs a sub-agent as a child session, whose request was filtered as another session's
    /// and never answered — the sub-agent waited for ever under a <c>task</c> row saying "running". Driven
    /// against a config that asks before every edit. Claude Code's sub-agents ask through the one control
    /// channel and need nothing.</remarks>
    [LiveAgentTheory]
    [InlineData("opencode")]
    public async Task A_sub_agent_asks_and_the_tile_hears_it(string agentId)
    {
        var wanted = (Environment.GetEnvironmentVariable("MTILES_LIVE_AGENTS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!wanted.Contains(agentId, StringComparer.OrdinalIgnoreCase))
        {
            output.WriteLine($"Skipped: MTILES_LIVE_AGENTS does not name {agentId}.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"mtiles-live-ask-{agentId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Git(root, "init -q");
        File.WriteAllText(Path.Combine(root, "README.md"), "live test\n");
        Git(root, "add -A");
        Git(root, "-c user.name=t -c user.email=t@t commit -q -m init");

        var agent = AiAgentCatalog.Find(agentId)!;
        var instance = AiAgentCatalog.SeedInstanceFor(agent);
        instance.DefaultBehaviour = AiBehaviour.ToolDefault;
        instance.DefaultEffort = AiEffort.Low;
        foreach (var pair in (Environment.GetEnvironmentVariable("MTILES_LIVE_MODELS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (pair.Split('=', 2) is [var id, var model] && id.Equals(agentId, StringComparison.OrdinalIgnoreCase))
                instance.Model = model;

        var (launch, problem) = await AgentSessionLauncher.PrepareAsync(new AppSettings(), agent, instance, root,
            Guid.NewGuid().ToString(), null, CancellationToken.None);
        Assert.True(launch is not null, problem);
        var executable = mTiles.Services.ExecutableFinder.Anywhere(agent.BinaryName);
        Assert.True(executable is not null, $"{agent.BinaryName} is not installed.");
        var config = Path.Combine(root, "..", $"{Path.GetFileName(root)}.opencode.json");
        File.WriteAllText(config, """{"permission":{"edit":"ask","bash":"ask"}}""");
        launch = launch! with
        {
            ExecutablePath = executable!,
            Environment = new Dictionary<string, string?>(launch.Environment) { ["OPENCODE_CONFIG"] = config },
        };

        var store = new SqliteConversationStore(Path.Combine(root, "..", $"{Path.GetFileName(root)}.db"));
        var host = new AgentConversationHost(
            new ConversationRecord(Guid.NewGuid().ToString(), agent.Id, root, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            store, new GitTurnCheckpoints(root));

        var asked = new List<string>();
        var turnDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Changed += (_, e) =>
        {
            output.WriteLine($"{e.GetType().Name}: {Describe(e)}");
            if (e is ApprovalRequested approval)
            {
                lock (asked) asked.Add($"{approval.Title} {approval.Detail}");
                host.ExecuteAsync(new RespondToApproval(approval.RequestId, ApprovalDecision.Accept), CancellationToken.None)
                    .ContinueWith(t => output.WriteLine($"answer failed: {t.Exception}"), TaskContinuationOptions.OnlyOnFaulted);
            }

            if (e is TurnCompleted) turnDone.TrySetResult();
        };

        try
        {
            await host.StartAsync(sink => AgentSessionLauncher.Create(agent, launch!, sink), null, CancellationToken.None);
            await host.ExecuteAsync(new SendMessage(
                "Use the task tool to delegate to a general subagent the job of creating the file sub.txt in the " +
                "current directory containing exactly the word hi (it must use its edit or write tool). Wait for it, " +
                "then reply DONE."), CancellationToken.None);

            await turnDone.Task.WaitAsync(TimeSpan.FromMinutes(5));
            Assert.True(File.Exists(Path.Combine(root, "sub.txt")), "The sub-agent did not write sub.txt.");
            lock (asked) Assert.Contains(asked, title => title.Contains("sub.txt"));
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static string Describe(AgentEvent e) => e switch
    {
        SubAgentStarted s => $"{s.SubAgentId} {s.Title} background={s.Background}",
        SubAgentProgressed p => $"{p.SubAgentId} {p.Progress}",
        SubAgentEnded s => $"{s.SubAgentId} {s.Outcome} {s.Result}",
        AssistantTextDelta d => d.Delta.Replace("\n", "⏎"),
        AssistantMessageCompleted m => m.Text.Replace("\n", "⏎"),
        ToolStarted t => $"{t.Kind} {t.Title}",
        ToolCompleted t => $"{t.Status} {t.Output?[..Math.Min(80, t.Output.Length)]}",
        ApprovalRequested a => a.Title,
        TurnCompleted t => $"{t.Outcome} {t.Error}",
        SessionStateChanged s => $"{s.State} {s.Detail}",
        SessionConfigured c => $"{c.Model} {c.Mode} {c.ResumeToken}",
        NoticeRaised n => $"{n.Level} {n.Text}",
        CheckpointCaptured c => $"{c.CheckpointId} files={c.Files.Count}",
        _ => "",
    };

    private static void Git(string directory, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        process.WaitForExit();
    }
}
