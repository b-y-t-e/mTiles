using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Agents.SessionLogs;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a terminal agent tile does with its agent's own session store: which conversation it adopts,
/// what it writes down, and when its Restart button asks to be pressed.
/// </summary>
public sealed class TerminalAgentSessionFollowingTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(SessionStrategy.CapturedAfterStart, false, false, true)]
    [InlineData(SessionStrategy.Fixed, false, true, false)]
    [InlineData(SessionStrategy.Fixed, true, true, true)]
    [InlineData(SessionStrategy.Fixed, true, false, false)]
    public void A_stored_session_id_is_kept_only_for_an_agent_that_can_be_handed_it_back(
        SessionStrategy strategy, bool hasLog, bool follows, bool expected)
    {
        var agent = new FollowingAgent(hasLog ? new MovedLog(tellsHeadlessRunsApart: true) : null, follows, strategy);

        Assert.Equal(expected, TerminalAgentTileViewModel.KeepsSessionId(agent));
    }

    [Fact]
    public void A_grok_tile_writes_no_session_id_into_its_layout_because_no_launch_resumes_one()
    {
        Assert.False(TerminalAgentTileViewModel.KeepsSessionId(new GrokAgent()));
    }

    [Fact]
    public void A_store_that_cannot_tell_a_headless_run_apart_keeps_no_stored_session_id()
    {
        var agent = new FollowingAgent(new MovedLog(tellsHeadlessRunsApart: false), follows: true,
            SessionStrategy.Fixed);

        Assert.False(TerminalAgentTileViewModel.KeepsSessionId(agent));
    }

    [Fact]
    public async Task A_conversation_moved_to_after_an_Enter_in_the_active_tile_is_adopted_and_saved()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var log = new MovedLog(tellsHeadlessRunsApart: true);
        var tile = Tile(settings, directory, new FollowingAgent(log, follows: true));

        try
        {
            tile.OnInputSubmitted();
            tile.OnActiveChanged(true);
            await Until(() => tile.StoredSessionId == log.MovedId);

            Assert.Equal(log.MovedId, tile.StoredSessionId);
            Assert.Equal(log.MovedId,
                ((ITileKind)new TerminalAgentTileKind()).Save(tile)?[AgentStateKeys.SessionIdKey]?.GetValue<string>());
        }
        finally { tile.Dispose(); }
    }

    [Fact]
    public async Task An_agent_that_does_not_follow_its_store_draws_the_gauge_and_keeps_its_id()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var tile = Tile(settings, directory,
            new FollowingAgent(new MovedLog(tellsHeadlessRunsApart: true), follows: false));

        try
        {
            tile.OnInputSubmitted();
            tile.OnActiveChanged(true);
            await Until(() => tile.ContextGauge!.HasAnythingToSay);

            Assert.True(tile.ContextGauge!.HasAnythingToSay);
            Assert.Equal("", tile.StoredSessionId);
        }
        finally { tile.Dispose(); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_new_conversation_is_not_taken_without_both_an_Enter_and_the_active_tile(
        bool active, bool submitted)
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var tile = Tile(settings, directory, new FollowingAgent(new MovedLog(tellsHeadlessRunsApart: true), follows: true));

        try
        {
            if (submitted) tile.OnInputSubmitted();
            tile.OnActiveChanged(active);
            await Until(() => tile.ContextGauge!.HasAnythingToSay);
            await Task.Delay(AgentSessionWatcher.QuietWindow * 2);

            Assert.Equal("", tile.StoredSessionId);
        }
        finally { tile.Dispose(); }
    }

    /// <summary>Closing the bar puts the sentence away, not the need for the restart: the running CLI
    /// still holds the old skills, so the header stays lit until a process actually starts.</summary>
    [Fact]
    public void The_restart_stays_urgent_after_the_notice_is_dismissed_until_a_process_starts()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var tile = Tile(settings, directory, new FollowingAgent(log: null, follows: true));

        try
        {
            Assert.Null(RestartUrgency(tile));

            tile.AskForRestartForSkills();
            Assert.Equal(SkillChangePolicy.Notice, RestartUrgency(tile));

            tile.DismissLaunchNoticeCommand.Execute(null);
            Assert.False(tile.HasLaunchNotice);
            Assert.Equal(SkillChangePolicy.Notice, RestartUrgency(tile));

            tile.NoteProcessStarting(isOneOfTheTilesOwnCommands: true);
            Assert.Null(RestartUrgency(tile));
        }
        finally { tile.Dispose(); }
    }

    private static string? RestartUrgency(TerminalAgentTileViewModel tile) =>
        tile.Actions.Single(a => a.Id == TileActionIds.Restart).Urgency;

    private static TerminalAgentTileViewModel Tile(TempSettings settings, TempDirectory directory, IAiAgent agent)
    {
        var instance = new AiAgentInstance { AgentId = agent.Id };
        settings.Service.Settings.AiAgentInstances.Add(instance);
        var tileId = Guid.NewGuid().ToString();
        return new TerminalAgentTileViewModel(directory.Path, null, settings.Service, agent, instance.Id,
            tileId: () => tileId, post: action => action());
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
    }

    private sealed class FollowingAgent(IAgentSessionLog? log, bool follows,
        SessionStrategy strategy = SessionStrategy.Fixed) : StubAgent
    {
        public override SessionStrategy SessionStrategy => strategy;
        public override IAgentSessionLog? SessionLog => log;
        public override bool FollowsSessionChanges => follows;
    }

    /// <summary>A store in which the tile's own conversation was last written an hour ago and a new one
    /// has just been begun.</summary>
    private sealed class MovedLog(bool tellsHeadlessRunsApart) : IAgentSessionLog
    {
        public string MovedId { get; } = Guid.NewGuid().ToString();

        public bool TellsHeadlessRunsApart => tellsHeadlessRunsApart;

        public Task<IReadOnlyList<string>> ListInteractiveAsync(AiSignIn? signIn, string workspaceDir,
            DateTimeOffset since, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<AgentSessionReading?> ReadLatestAsync(AiSignIn? signIn, string workspaceDir,
            DateTimeOffset since, Func<string, bool>? isFree = null, CancellationToken ct = default) =>
            Task.FromResult<AgentSessionReading?>(new AgentSessionReading(MovedId, DateTimeOffset.UtcNow, 2000));

        public Task<AgentSessionReading?> ReadAsync(AiSignIn? signIn, string workspaceDir, string sessionId,
            CancellationToken ct = default) =>
            Task.FromResult<AgentSessionReading?>(
                new AgentSessionReading(sessionId, DateTimeOffset.UtcNow.AddHours(-1), 1000));

        public string? WatchDirectory(AiSignIn? signIn, string workspaceDir) => null;
    }
}
