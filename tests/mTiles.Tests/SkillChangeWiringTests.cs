using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Threading;
using mTiles.AgentSessions;
using mTiles.AgentSessions.Events;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Agents.Sessions;
using mTiles.Services.Providers;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using mTiles.ViewModels.AgentConversation;
using Terminal.Avalonia;
using Terminal.Pty;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The wire between a workspace's skills and the agents already running in it, driven end to end.
/// </summary>
/// <remarks>
/// <para><b>Why the policy's own table test does not cover this.</b> <see cref="SkillChangePolicy"/> is
/// pure and argued there; what it cannot say is whether anything ever asks it. Every link here is one a
/// later change can cut without a single assertion failing: the two kinds handing
/// <see cref="TileContext.AgentFiles"/> to the tile they build, each tile subscribing to
/// <see cref="WorkspaceAgentFiles.SkillsChanged"/>, the Agent tile actually starting its agent again, and
/// <c>HasRunningSession</c> answering for a session rather than for a control that exists. Cut, the
/// feature goes back to being the silence it was written to end — and silence is what no unit of it
/// reports.</para>
/// <para>Both tiles are built through their <see cref="ITileKind"/> rather than by calling the constructor
/// with an <c>agentFiles</c> of the test's own, because the kind is one of the links under test: a tile
/// handed the object by hand passes while the application's own path hands it nothing.</para>
/// </remarks>
public sealed class SkillChangeWiringTests
{
    private const string Skill = "mtiles-database";

    /// <summary>
    /// A terminal agent whose CLI does not follow the change is told to restart — once it is running.
    /// </summary>
    /// <remarks>The two halves are one test because what separates them is the whole of
    /// <c>HasRunningSession</c>: a tile that has not launched has read nothing yet, so the skill is on disk
    /// before its first read and there is nothing to say. A tile whose control exists but whose shell has
    /// not started is that same case, which is why the notice is asserted absent after
    /// <c>AttachControl</c> and present only once the session is running.</remarks>
    [Fact]
    public void A_terminal_agent_is_told_to_restart_only_once_its_shell_is_running() => OnUiThread(async () =>
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var context = new TileContext(directory.Path, settings.Service);
        var files = context.AgentFiles;
        // One of the five that find out at their next start: an agent following the change itself is told
        // nothing, so it would prove nothing about the wire.
        var agent = AiAgentCatalog.All.First(a => !a.WatchesSkillsDirectory(AgentSurface.Terminal));
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == agent.Id);

        var tile = (TerminalAgentTileViewModel)((ITileKind)new TerminalAgentTileKind()).Create(
            context, new JsonObject { [AgentStateKeys.InstanceIdKey] = instance.Id });
        using var control = new TerminalControl { PtyFactory = options => new FakePty(options) };
        try
        {
            // The first offer of a session is the baseline and never a change — see WorkspaceAgentFiles.
            files.WriteSkill(Skill, "one");

            tile.AttachControl(control);
            files.WriteSkill(Skill, "two");
            await Settle();
            Assert.DoesNotContain(SkillChangePolicy.Notice, tile.LaunchNotice);

            control.Start(new PtyOptions { Command = "fake-shell", Arguments = ["-l"] });
            files.WriteSkill(Skill, "three");
            await Settle();
            Assert.Contains(SkillChangePolicy.Notice, tile.LaunchNotice);
        }
        finally { tile.Dispose(); }
    });

    /// <summary>An idle Agent tile starts its agent again by itself, and one that has not started at all is
    /// left alone.</summary>
    /// <remarks>
    /// <para>The restart is counted at <see cref="IAgentSessionStarter.PrepareAsync"/>: the last thing that
    /// happens before a CLI would be spawned, and the first that cannot be reached without the tile having
    /// decided to start one.</para>
    /// <para>Claude Code on purpose. It is the one agent that does follow a skill change in its own terminal
    /// interface, and the session an Agent tile drives is not that interface — asked with the terminal's
    /// answer, the commonest tile in the application would do nothing at all. So the agent that makes the two
    /// surfaces differ is the one to drive this with.</para>
    /// </remarks>
    [Fact]
    public void An_idle_agent_tile_restarts_itself_when_the_skills_move() => OnUiThread(async () =>
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var context = new TileContext(directory.Path, settings.Service);
        var files = context.AgentFiles;
        var starter = new CountingStarter();
        var claude = AiAgentCatalog.Find("claude")!;
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == claude.Id);

        var tile = (AgentConversationTileViewModel)((ITileKind)new AgentConversationTileKind(
                TestTiles.ConversationStore(), starter))
            .Create(context with { TileId = () => Guid.NewGuid().ToString() },
                new JsonObject { [AgentStateKeys.InstanceIdKey] = instance.Id });
        try
        {
            files.WriteSkill(Skill, "one");

            files.WriteSkill(Skill, "two");
            await Settle();
            Assert.Equal(0, starter.Prepared);

            tile.EnsureStarted();
            await WaitUntil(() => starter.Prepared == 1, "the tile started its agent");

            files.WriteSkill(Skill, "three");
            await WaitUntil(() => starter.Prepared == 2, "the skill change restarted the agent");
        }
        finally { tile.Dispose(); }
    });

    /// <summary>An Agent tile that cannot be restarted says so once, on its own bar, and stops saying it when
    /// the restart happens.</summary>
    /// <remarks>
    /// <para><b>Once</b>, because the sentence used to be a <c>NoticeRaised</c> — a stored event — so three
    /// databases ticked while the agent worked left three identical lines in <c>conversations.db</c>, back
    /// again every time the conversation was opened.</para>
    /// <para><b>And it comes down</b>, which the transcript could not do at all: a bar still asking for a
    /// restart after the restart is a request nobody can satisfy. The terminal agent tile had both halves
    /// and this one had neither.</para>
    /// <para>Kept unsent work is what makes the tile refuse the automatic restart — the cheapest of the two
    /// states that do, and the one a test can put a tile into without a turn in flight.</para>
    /// </remarks>
    [Fact]
    public void An_agent_tile_asks_for_a_restart_once_and_stops_asking_when_it_happens() => OnUiThread(async () =>
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var context = new TileContext(directory.Path, settings.Service);
        var files = context.AgentFiles;
        var starter = new CountingStarter();
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");

        var tile = (AgentConversationTileViewModel)((ITileKind)new AgentConversationTileKind(
                TestTiles.ConversationStore(), starter))
            .Create(context with { TileId = () => Guid.NewGuid().ToString() },
                new JsonObject { [AgentStateKeys.InstanceIdKey] = instance.Id });
        try
        {
            files.WriteSkill(Skill, "one");
            tile.EnsureStarted();
            await WaitUntil(() => starter.Prepared == 1, "the tile started its agent");

            // A message typed and not sent would go with the process, so the tile asks instead of acting.
            tile.Draft = "half a sentence";
            files.WriteSkill(Skill, "two");
            files.WriteSkill(Skill, "three");
            await Settle();

            Assert.Equal(1, Occurrences(tile.LaunchNotice, SkillChangePolicy.Notice));
            Assert.Equal(1, starter.Prepared);

            // The restart the notice asked for — here the policy's own, once nothing is left to lose.
            tile.Draft = "";
            files.WriteSkill(Skill, "four");
            await WaitUntil(() => starter.Prepared == 2, "the skill change restarted the agent");
            await WaitUntil(() => !tile.LaunchNotice.Contains(SkillChangePolicy.Notice),
                "the notice came down with the restart");
        }
        finally { tile.Dispose(); }
    });

    /// <summary>A run of changes is one restart, not one restart each.</summary>
    /// <remarks>
    /// <para>Every database added in the Database tile and every RW toggle writes the skill again, so the
    /// ordinary gesture — granting three databases — arrives here as three changes. Acted on one at a time
    /// the tile tore the agent down and started it again three times, of which only the last described what
    /// the user meant to grant; on agy one failed resume alone is measured at 98 s and 282k tokens.</para>
    /// <para>The second half is the one a count alone would miss: the wait past the quiet window proves no
    /// further start is still on its way, rather than that the test looked before the others arrived.</para>
    /// </remarks>
    [Fact]
    public void A_run_of_skill_changes_restarts_the_agent_once() => OnUiThread(async () =>
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var context = new TileContext(directory.Path, settings.Service);
        var files = context.AgentFiles;
        var starter = new CountingStarter();
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");

        var tile = (AgentConversationTileViewModel)((ITileKind)new AgentConversationTileKind(
                TestTiles.ConversationStore(), starter))
            .Create(context with { TileId = () => Guid.NewGuid().ToString() },
                new JsonObject { [AgentStateKeys.InstanceIdKey] = instance.Id });
        try
        {
            files.WriteSkill(Skill, "one");
            tile.EnsureStarted();
            await WaitUntil(() => starter.Prepared == 1, "the tile started its agent");

            files.WriteSkill(Skill, "two");
            files.WriteSkill(Skill, "three");
            files.WriteSkill(Skill, "four");
            await WaitUntil(() => starter.Prepared == 2, "the run of changes restarted the agent");

            await Idle(SkillChangePolicy.QuietWindow + SkillChangePolicy.QuietWindow);
            Assert.Equal(2, starter.Prepared);
        }
        finally { tile.Dispose(); }
    });

    /// <summary>A change that lands while the agent is being started again is answered by one more start.
    /// </summary>
    /// <remarks>
    /// <para>The window is real and it is seconds wide: a restart disposes of the CLI and then resolves a
    /// model, which can be a call to a provider, before anything is spawned. Throughout it the tile holds no
    /// host at all, so "has the agent started" read off the host answered no — and no is the one answer that
    /// costs nothing and says nothing: the change was dropped without a restart and without a notice, while
    /// the process coming up read the skills directory as it was before the write.</para>
    /// <para>The second database of the two the user ticked is exactly this case, which is why the count is
    /// three: the first start, the one the first change asked for, and the one the change made inside it
    /// asked for.</para>
    /// </remarks>
    [Fact]
    public void A_change_during_a_restart_is_answered_by_one_more() => OnUiThread(async () =>
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var context = new TileContext(directory.Path, settings.Service);
        var files = context.AgentFiles;
        var starter = new CountingStarter();
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");

        var tile = (AgentConversationTileViewModel)((ITileKind)new AgentConversationTileKind(
                TestTiles.ConversationStore(), starter))
            .Create(context with { TileId = () => Guid.NewGuid().ToString() },
                new JsonObject { [AgentStateKeys.InstanceIdKey] = instance.Id });
        try
        {
            files.WriteSkill(Skill, "one");
            tile.EnsureStarted();
            await WaitUntil(() => starter.Prepared == 1, "the tile started its agent");

            // Held inside the start, which is where the old host is already gone and the new session does
            // not exist yet — the window a change used to fall into unanswered.
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            starter.HoldNextStart = held;
            files.WriteSkill(Skill, "two");
            await WaitUntil(() => starter.Prepared == 2, "the first change restarted the agent");

            files.WriteSkill(Skill, "three");
            await Settle();
            held.SetResult();

            await WaitUntil(() => starter.Prepared == 3, "the change made during the restart was answered");
        }
        finally { tile.Dispose(); }
    });

    /// <summary>A terminal agent's notice comes down at the process, never at the launch that was refused.
    /// </summary>
    /// <remarks>
    /// <para>A launch is claimed before the launcher has read <c>LaunchProblem</c> and before a launch that
    /// waited finds out whether it is still the tile's. Answered at the claim, pressing Restart shell on a
    /// tile whose instance no longer resolves took the one line asking for that restart off the bar while
    /// the old CLI went on running with the skills it read at its own start — the request satisfied on
    /// screen and nowhere else.</para>
    /// <para>Both halves in one test, because what separates them is only where the answer is given: the
    /// same tile, the same notice, one launch refused and one that reaches a process.</para>
    /// </remarks>
    [Fact]
    public void A_refused_launch_leaves_the_notice_standing_and_a_real_one_takes_it_down() => OnUiThread(async () =>
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var context = new TileContext(directory.Path, settings.Service);
        // Not one that follows the change itself, and not one whose session id is captured by running the
        // CLI: this is about the launcher, and a capture would make it about what is installed.
        var agent = AiAgentCatalog.All.First(a => !a.WatchesSkillsDirectory(AgentSurface.Terminal)
                                                  && a.SessionStrategy != SessionStrategy.CapturedAfterStart);
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == agent.Id);

        var tile = (TerminalAgentTileViewModel)((ITileKind)new TerminalAgentTileKind()).Create(
            context, new JsonObject { [AgentStateKeys.InstanceIdKey] = instance.Id });
        using var control = new TerminalControl { PtyFactory = options => new FakePty(options) };
        try
        {
            tile.AttachControl(control);
            tile.LaunchNotice = LaunchNotices.With("", SkillChangePolicy.Notice);

            // The model the user asked for is whatever a provider has loaded, and the provider is gone.
            instance.Model = AiModelChoice.FirstLoaded;
            instance.ApiAccountId = "";
            TileLauncher.Launch(control, tile);
            await WaitUntil(() => tile.HasLaunchProblem, "the launch was refused");
            Assert.Contains(SkillChangePolicy.Notice, tile.LaunchNotice);

            instance.Model = "";
            TileLauncher.Launch(control, tile);
            await WaitUntil(() => !tile.LaunchNotice.Contains(SkillChangePolicy.Notice),
                "the launch that reached a process took the notice down");
        }
        finally { tile.Dispose(); }
    });

    /// <summary>Lets the dispatcher run for a while without waiting for anything in particular.</summary>
    private static async Task Idle(TimeSpan howLong)
    {
        var deadline = Environment.TickCount64 + (long)howLong.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    private static int Occurrences(string standing, string line) =>
        standing.Split('\n').Count(each => each == line);

    /// <summary>A tile whose agent never started is left alone, however far the start got.</summary>
    /// <remarks>A host is opened before the CLI is prepared and stays open when the preparation answers a
    /// problem — a provider that has been deleted, a model nothing could resolve — so a tile in that state
    /// has an agent object and no agent. Asked whether it is "running" by the existence of the host, it
    /// restarted on every tick of a database: a resolution that can be a call to the provider, made again
    /// to land on the same sentence. Nothing has read the skill, so there is nothing for a restart to
    /// achieve.</remarks>
    [Fact]
    public void An_agent_tile_that_could_not_start_is_not_restarted() => OnUiThread(async () =>
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var context = new TileContext(directory.Path, settings.Service);
        var files = context.AgentFiles;
        var starter = new CountingStarter { Problem = "This instance could not be resolved." };
        var claude = AiAgentCatalog.Find("claude")!;
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == claude.Id);

        var tile = (AgentConversationTileViewModel)((ITileKind)new AgentConversationTileKind(
                TestTiles.ConversationStore(), starter))
            .Create(context with { TileId = () => Guid.NewGuid().ToString() },
                new JsonObject { [AgentStateKeys.InstanceIdKey] = instance.Id });
        try
        {
            files.WriteSkill(Skill, "one");

            tile.EnsureStarted();
            await WaitUntil(() => tile.LaunchProblem is not null, "the start answered a problem");

            files.WriteSkill(Skill, "two");
            await Settle();
            Assert.Equal(1, starter.Prepared);
        }
        finally { tile.Dispose(); }
    });

    /// <summary>Counts the starts and hands back a session that says it is ready without running a CLI:
    /// what is under test is the decision to start, not a tool the machine running this may not have.
    /// </summary>
    /// <remarks>It has to reach a live session rather than stop at a problem, because "is this agent
    /// running" is the question the tile answers with <c>HasSession</c> — a starter that always refuses
    /// would leave the restart under test unreachable.</remarks>
    private sealed class CountingStarter : IAgentSessionStarter
    {
        private int _prepared;

        public int Prepared => Volatile.Read(ref _prepared);

        /// <summary>What the preparation answers instead of a launch, for the tile that never starts.</summary>
        public string? Problem { get; init; }

        private TaskCompletionSource? _holdNextStart;

        /// <summary>Holds the next start open here, which is the real window a restart has: the old CLI is
        /// gone and the new one has not been spawned. Taken once, so the start it lets through is not held
        /// again.</summary>
        public TaskCompletionSource? HoldNextStart
        {
            get => Volatile.Read(ref _holdNextStart);
            set => Volatile.Write(ref _holdNextStart, value);
        }

        public async Task<(AgentSessionLaunch? Launch, string? Problem)> PrepareAsync(AppSettings settings,
            IAiAgent agent, AiAgentInstance instance, string workingDirectory, string conversationId,
            string? resumeToken, CancellationToken ct)
        {
            Interlocked.Increment(ref _prepared);
            if (Interlocked.Exchange(ref _holdNextStart, null) is { } held) await held.Task;
            if (Problem is { } refused) return (null, refused);

            return (new AgentSessionLaunch(agent.BinaryName, workingDirectory,
                AgentRuntime.For(settings, instance, agent: agent), new Dictionary<string, string?>(),
                AiBehaviour.ToolDefault, AiEffort.ToolDefault, resumeToken, conversationId), null);
        }

        public IAgentSession Create(IAiAgent agent, AgentSessionLaunch launch, IAgentEventSink sink) =>
            new ReadySession(sink);
    }

    /// <summary>A session that starts no process and reports itself ready, which is all the tile reads.</summary>
    private sealed class ReadySession(IAgentEventSink sink) : IAgentSession
    {
        public Task StartAsync(CancellationToken ct)
        {
            sink.Emit(new SessionStateChanged(AgentSessionState.Ready));
            return Task.CompletedTask;
        }

        public Task SendAsync(AgentTurnInput input, CancellationToken ct) => Task.CompletedTask;
        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RespondToApprovalAsync(string requestId, ApprovalDecision decision, CancellationToken ct) =>
            Task.CompletedTask;

        public Task AnswerQuestionsAsync(string requestId,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? answers, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<SettingsChangeOutcome> ChangeSettingsAsync(SessionSettings settings, CancellationToken ct) =>
            Task.FromResult(SettingsChangeOutcome.Rejected);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Lets everything the change posted onto this thread run.</summary>
    /// <remarks>Both tiles answer a skill change off whichever thread wrote the file and get onto the one
    /// they draw on first, so an assertion made in the same breath as the write reads a tile that has not
    /// heard anything yet.</remarks>
    private static async Task Settle()
    {
        for (var pass = 0; pass < 5; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, $"timed out waiting until {what}");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    private static void OnUiThread(Func<Task> body) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SkillChangeWiringTests).Assembly)
            .Dispatch(async () =>
            {
                await body();
                return true;
            }, CancellationToken.None).GetAwaiter().GetResult();
}
