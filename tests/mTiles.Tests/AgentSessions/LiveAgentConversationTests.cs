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
    [Theory]
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
    [Theory]
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

    private static string Describe(AgentEvent e) => e switch
    {
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
