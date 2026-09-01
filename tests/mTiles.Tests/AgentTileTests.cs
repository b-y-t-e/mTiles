using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Providers;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The agent tile: what it is created from, what it writes down, and which conversation it resumes.
/// </summary>
/// <remarks>
/// No CLI is installed for these and none needs to be — everything asserted here is about the tile's
/// bookkeeping, which is the part that decides whether a conversation survives a restart of mTiles. The
/// launch itself is the terminal tile's, tested where that is.
/// </remarks>
public class AgentTileTests
{
    /// <summary>
    /// Every agent gets one instance, and a second pass adds nothing.
    /// </summary>
    /// <remarks>Seeding that replaced rather than added would undo a rename or a repointed provider on
    /// every launch; seeding that ran only on a brand new file would leave an agent added by a later
    /// version with no row at all.</remarks>
    [Fact]
    public void Every_agent_is_seeded_one_instance_and_only_one()
    {
        using var settings = new TempSettings();

        Assert.Equal(
            AiAgentCatalog.All.Select(agent => agent.Id).Order(),
            settings.Service.Settings.AiAgentInstances.Select(i => i.AgentId).Order());

        var renamed = settings.Service.Settings.AiAgentInstances[0];
        renamed.Name = "Mine";

        using var reopened = new TempSettings();
        Assert.Equal(AiAgentCatalog.All.Count, reopened.Service.Settings.AiAgentInstances.Count);
        Assert.Equal("Mine", renamed.Name);
    }

    /// <summary>
    /// A tile whose agent lets us name the session runs under the tile's own id, and writes none down.
    /// </summary>
    /// <remarks>Writing it down as well would give one value two writers: "New session" replaces the
    /// leaf's id, and a copy of the old one in the layout is a tile that reopens the conversation the
    /// user has just left.</remarks>
    [Fact]
    public void An_agent_we_can_name_the_session_for_runs_under_the_tiles_own_id()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var claude = AiAgentCatalog.All.First(a => a.SessionStrategy == SessionStrategy.Fixed);
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == claude.Id);

        var tileId = Guid.NewGuid().ToString();
        var kind = new AgentTileKind();
        var tile = (AgentTileViewModel)((ITileKind)kind).Create(
            Context(directory.Path, settings, tileId),
            new JsonObject { [AgentTileKind.InstanceIdKey] = instance.Id });

        try
        {
            Assert.Equal(tileId, tile.SessionId);
            Assert.Null(((ITileKind)kind).Save(tile)?[AgentTileKind.SessionIdKey]);
        }
        finally { tile.Dispose(); }
    }

    /// <summary>
    /// A tile whose agent names its own session keeps that id in the layout, and reopens it.
    /// </summary>
    /// <remarks>This is the one thing <see cref="SessionStrategy.CapturedAfterStart"/> costs that the
    /// other two do not: an id that is not written down is a conversation lost at the next restart.
    /// </remarks>
    [Fact]
    public void A_captured_session_survives_a_restart_and_a_new_session_ends_it()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var captured = AiAgentCatalog.All
            .First(a => a.SessionStrategy == SessionStrategy.CapturedAfterStart);
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == captured.Id);

        var tileId = Guid.NewGuid().ToString();
        var state = new JsonObject
        {
            [AgentTileKind.InstanceIdKey] = instance.Id,
            [AgentTileKind.SessionIdKey] = "kept-conversation",
        };

        // The identity the tile loads under is the one the stored id was captured under — a layout only
        // ever carries the two together. Read through a function, as the leaf supplies it.
        var identity = tileId;
        var kind = (ITileKind)new AgentTileKind();
        var tile = (AgentTileViewModel)kind.Create(
            Context(directory.Path, settings, reads: () => identity), state);
        try
        {
            Assert.Equal("kept-conversation", tile.SessionId);
            Assert.Equal("kept-conversation",
                kind.Save(tile)?[AgentTileKind.SessionIdKey]?.GetValue<string>());

            // "New session" replaces the leaf's id under the running tile and restarts it. The captured
            // conversation belonged to the identity that is now gone, so the tile starts a fresh one
            // rather than reopening the conversation the user has just asked to leave.
            identity = Guid.NewGuid().ToString();

            Assert.Equal("", tile.SessionId);
            Assert.Null(kind.Save(tile)?[AgentTileKind.SessionIdKey]);
        }
        finally { tile.Dispose(); }
    }

    /// <summary>
    /// The conversation a tile leaves behind at "New session" becomes available to another tile.
    /// </summary>
    /// <remarks>The claim is keyed by the identity that made it, and "New session" replaces that
    /// identity under the running tile. Nothing else releases the old entry — <c>Dispose</c> releases
    /// under the identity the tile has <em>now</em> — so the abandoned conversation stayed spoken for
    /// until the application was closed, and the register grew by one entry every time the command was
    /// used.</remarks>
    [Fact]
    public async Task A_session_left_behind_by_a_new_session_stops_being_held()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var captured = AiAgentCatalog.All
            .First(a => a.SessionStrategy == SessionStrategy.CapturedAfterStart && a.CapturesWhileRunning);
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == captured.Id);

        var abandoned = "conversation-" + Guid.NewGuid();
        var identity = Guid.NewGuid().ToString();
        var tile = (AgentTileViewModel)((ITileKind)new AgentTileKind()).Create(
            Context(directory.Path, settings, reads: () => identity),
            new JsonObject
            {
                [AgentTileKind.InstanceIdKey] = instance.Id,
                [AgentTileKind.SessionIdKey] = abandoned,
            });

        try
        {
            Assert.False(CapturedSessions.TryClaim(abandoned, "another tile"));

            // "New session": the leaf takes a new id and relaunches through the launcher, which is
            // where the tile first sees that its identity has moved.
            identity = Guid.NewGuid().ToString();
            await tile.PrepareForLaunchAsync();

            Assert.True(CapturedSessions.TryClaim(abandoned, "another tile"));
        }
        finally
        {
            tile.Dispose();
            CapturedSessions.ReleaseAllOf("another tile");
        }
    }

    /// <summary>
    /// An instance the user has deleted leaves a tile that still starts.
    /// </summary>
    /// <remarks>The agent's id is written down beside the instance's for exactly this: the tile falls
    /// back to another instance of the same agent, which is the closest thing to what the user had.
    /// </remarks>
    [Fact]
    public void A_deleted_instance_falls_back_to_the_agent_it_was_running()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var agent = AiAgentCatalog.All[1];

        var tile = (AgentTileViewModel)((ITileKind)new AgentTileKind()).Create(
            Context(directory.Path, settings, Guid.NewGuid().ToString()),
            new JsonObject
            {
                [AgentTileKind.InstanceIdKey] = "an instance that is gone",
                [AgentTileKind.AgentIdKey] = agent.Id,
            });

        try { Assert.Equal(agent.Id, tile.AgentId); }
        finally { tile.Dispose(); }
    }

    /// <summary>
    /// An OpenCode tile resolves its own commands, and resumes the session its id spells.
    /// </summary>
    /// <remarks>The tile used to build the session id itself — a bare GUID, which OpenCode's import
    /// document refuses — so <c>ResolveCurrentScripts</c> threw inside the view's <c>async void</c>
    /// attach handler and the tile never launched at all. The id is the agent's to spell, and this is
    /// the only strategy where the two differ.</remarks>
    [Fact]
    public void An_opencode_tile_resolves_the_commands_that_resume_its_session()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var opencode = AiAgentCatalog.All.First(a => a.SessionStrategy == SessionStrategy.ImportedFixed);
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == opencode.Id);

        var tileId = Guid.NewGuid().ToString();
        var tile = (AgentTileViewModel)((ITileKind)new AgentTileKind()).Create(
            Context(directory.Path, settings, tileId),
            new JsonObject { [AgentTileKind.InstanceIdKey] = instance.Id });

        try
        {
            Assert.Equal("ses_" + tileId, tile.SessionId);

            var scripts = tile.ResolveCurrentScripts();
            Assert.Equal($"opencode --session ses_{tileId}", scripts.Startup);

            // The launcher writes the import document only for a script that names it by token, and
            // the token has to survive resolution as the path of *this* tile's document.
            Assert.Contains(TileScript.OpenCodeSessionFileToken, scripts.Fallback!,
                StringComparison.Ordinal);
            Assert.Contains(OpenCodeSession.DocumentPath(tileId),
                TileScript.Resolve(scripts.Fallback!, tileId), StringComparison.Ordinal);
        }
        finally { tile.Dispose(); }
    }

    /// <summary>
    /// A model that cannot be resolved fails the launch, in words, rather than starting the session on
    /// something else.
    /// </summary>
    /// <remarks>The sentinel exists so that changing the model in LM Studio changes it here too; an
    /// instance asking for "the first loaded model" with no provider to ask has no answer, and a tile
    /// that launched anyway would run on the CLI's own model with the reason in a log file. That is the
    /// silent substitution, and it is invisible precisely because the tile looks like it worked.
    /// </remarks>
    [Fact]
    public async Task An_unresolvable_model_stops_the_launch_and_says_why()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var instance = settings.Service.Settings.AiAgentInstances[0];
        instance.Model = AiModelChoice.FirstLoaded;
        instance.ApiAccountId = "";

        var tile = (AgentTileViewModel)((ITileKind)new AgentTileKind()).Create(
            Context(directory.Path, settings, Guid.NewGuid().ToString()),
            new JsonObject { [AgentTileKind.InstanceIdKey] = instance.Id });

        try
        {
            await tile.PrepareForLaunchAsync();

            Assert.True(tile.HasLaunchProblem);
            Assert.Contains("provider", tile.LaunchProblem, StringComparison.OrdinalIgnoreCase);
        }
        finally { tile.Dispose(); }
    }

    /// <summary>A model on an agent that has no way of being told one is said out loud for the same
    /// reason: the setting is on the row and does nothing at all.</summary>
    [Fact]
    public async Task A_model_an_agent_cannot_be_told_is_not_launched_past()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var agent = new GenericAgent("some-tool");
        var instance = new AiAgentInstance { AgentId = agent.Id, Model = "some/model" };
        settings.Service.Settings.AiAgentInstances.Add(instance);

        var tile = new AgentTileViewModel(directory.Path, null, settings.Service, agent, instance.Id,
            tileId: () => Guid.NewGuid().ToString());

        try
        {
            await tile.PrepareForLaunchAsync();

            Assert.True(tile.HasLaunchProblem);
            Assert.Contains("some/model", tile.LaunchProblem, StringComparison.Ordinal);
        }
        finally { tile.Dispose(); }
    }

    /// <summary>An ordinary instance launches with nothing to say, and its model on the command line.
    /// </summary>
    [Fact]
    public async Task An_ordinary_model_launches_and_reaches_the_commands()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var opencode = AiAgentCatalog.All.First(a => a.SessionStrategy == SessionStrategy.ImportedFixed);
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == opencode.Id);
        instance.Model = "some/model";

        var tile = (AgentTileViewModel)((ITileKind)new AgentTileKind()).Create(
            Context(directory.Path, settings, Guid.NewGuid().ToString()),
            new JsonObject { [AgentTileKind.InstanceIdKey] = instance.Id });

        try
        {
            await tile.PrepareForLaunchAsync();

            Assert.False(tile.HasLaunchProblem);
            Assert.Contains("--model some/model", tile.ResolveCurrentScripts().Startup!,
                StringComparison.Ordinal);
        }
        finally { tile.Dispose(); }
    }

    /// <summary>
    /// A stored conversation belongs to the agent that named it, and to no other.
    /// </summary>
    /// <remarks>An instance whose agent is changed in Settings — or a deleted one, which drops the tile
    /// onto whatever is available — leaves a layout holding an id the new agent has never seen. That is
    /// the one thing the captured strategy forbids: <c>codex resume &lt;unknown&gt;</c> stops on an
    /// interactive picker the launch chain cannot answer, and <c>agy --conversation &lt;unknown&gt;</c>
    /// quietly starts a different conversation and exits 0. The id an agent spells for itself
    /// (opencode's <c>ses_</c>) is never written down at all, for the same reason.</remarks>
    [Fact]
    public void A_session_id_is_dropped_when_the_tile_lands_on_another_agent()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var opencode = AiAgentCatalog.All.First(a => a.SessionStrategy == SessionStrategy.ImportedFixed);
        var captured = AiAgentCatalog.All
            .First(a => a.SessionStrategy == SessionStrategy.CapturedAfterStart);
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == opencode.Id);
        var kind = (ITileKind)new AgentTileKind();

        // An id this tile derives from its own identity is not stored: it is recomputed at every launch,
        // and a copy could only ever disagree with it.
        var self = (AgentTileViewModel)kind.Create(
            Context(directory.Path, settings, Guid.NewGuid().ToString()),
            new JsonObject { [AgentTileKind.InstanceIdKey] = instance.Id });
        try { Assert.Null(kind.Save(self)?[AgentTileKind.SessionIdKey]); }
        finally { self.Dispose(); }

        // The user repoints the instance at an agent that names its own sessions. The layout still
        // carries opencode's id, and the tile must start a fresh conversation instead of resuming it.
        instance.AgentId = captured.Id;
        var tile = (AgentTileViewModel)kind.Create(
            Context(directory.Path, settings, Guid.NewGuid().ToString()),
            new JsonObject
            {
                [AgentTileKind.InstanceIdKey] = instance.Id,
                [AgentTileKind.AgentIdKey] = opencode.Id,
                [AgentTileKind.SessionIdKey] = "ses_from-another-agent",
            });

        try
        {
            Assert.Equal(captured.Id, tile.AgentId);
            Assert.Equal("", tile.SessionId);
            Assert.Null(kind.Save(tile)?[AgentTileKind.SessionIdKey]);
        }
        finally { tile.Dispose(); }
    }

    /// <summary>
    /// A tile that lands on another agent says so, and the layout keeps asking for the one it was
    /// created with.
    /// </summary>
    /// <remarks>Both halves are the same fault seen twice: silently changing which agent is working in
    /// somebody's repository, and then making that permanent. The layout is saved for any reason at all —
    /// a splitter dragged — so writing the substitute's ids would settle the question within seconds of
    /// the tile opening, and restoring the instance in Settings would no longer bring the tile back.
    /// </remarks>
    [Fact]
    public void A_tile_that_lands_on_another_agent_says_so_and_keeps_its_original_choice()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var kind = (ITileKind)new AgentTileKind();

        // The instance is gone and so is every other instance of its agent, which is what drops the tile
        // onto whatever else is configured.
        var gone = AiAgentCatalog.All[1];
        foreach (var instance in settings.Service.Settings.AiAgentInstances
                     .Where(i => i.AgentId == gone.Id).ToList())
            settings.Service.Settings.AiAgentInstances.Remove(instance);

        var tile = (AgentTileViewModel)kind.Create(
            Context(directory.Path, settings, Guid.NewGuid().ToString()),
            new JsonObject
            {
                [AgentTileKind.InstanceIdKey] = "an instance that is gone",
                [AgentTileKind.AgentIdKey] = gone.Id,
            });

        try
        {
            Assert.NotEqual(gone.Id, tile.AgentId);
            Assert.Contains(gone.DisplayName, tile.LaunchNotice);

            var saved = kind.Save(tile);
            Assert.Equal("an instance that is gone",
                saved?[AgentTileKind.InstanceIdKey]?.GetValue<string>());
            Assert.Equal(gone.Id, saved?[AgentTileKind.AgentIdKey]?.GetValue<string>());
        }
        finally { tile.Dispose(); }
    }

    /// <summary>
    /// An instance naming an agent this build does not have is a substitution too, said out loud and
    /// with the layout left as it was.
    /// </summary>
    /// <remarks>The instance is found by id, so nothing looks wrong until the tile launches a different
    /// program: <c>settings.json</c> is read tolerantly and never pruned, so a row written by a newer
    /// build survives a Velopack rollback intact. Without this the tile started as the first agent in
    /// the catalog with no notice, and the next save wrote that agent's id over the only record of the
    /// one the user configured.</remarks>
    [Fact]
    public void An_instance_naming_an_agent_this_build_lacks_says_so_and_keeps_its_original_choice()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var kind = (ITileKind)new AgentTileKind();

        var fromANewerBuild = new AiAgentInstance { AgentId = "an-agent-from-later", Name = "Later" };
        settings.Service.Settings.AiAgentInstances.Add(fromANewerBuild);

        var tile = (AgentTileViewModel)kind.Create(
            Context(directory.Path, settings, Guid.NewGuid().ToString()),
            new JsonObject
            {
                [AgentTileKind.InstanceIdKey] = fromANewerBuild.Id,
                [AgentTileKind.AgentIdKey] = fromANewerBuild.AgentId,
            });

        try
        {
            Assert.NotNull(tile.Substitution);
            Assert.Contains(fromANewerBuild.AgentId, tile.LaunchNotice);

            var saved = kind.Save(tile);
            Assert.Equal(fromANewerBuild.Id, saved?[AgentTileKind.InstanceIdKey]?.GetValue<string>());
            Assert.Equal(fromANewerBuild.AgentId, saved?[AgentTileKind.AgentIdKey]?.GetValue<string>());
        }
        finally { tile.Dispose(); }
    }

    /// <summary>A tile that opens on the instance it was created with says nothing.</summary>
    /// <remarks>The notice is a report of a substitution, so an ordinary tile must not carry one — a
    /// warning shown on every tile is a warning nobody reads on the one that has something to say.
    /// </remarks>
    [Fact]
    public void A_tile_that_opens_as_configured_carries_no_notice()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var instance = settings.Service.Settings.AiAgentInstances[0];

        var tile = (AgentTileViewModel)((ITileKind)new AgentTileKind()).Create(
            Context(directory.Path, settings, Guid.NewGuid().ToString()),
            new JsonObject
            {
                [AgentTileKind.InstanceIdKey] = instance.Id,
                [AgentTileKind.AgentIdKey] = instance.AgentId,
            });

        try
        {
            Assert.False(tile.HasLaunchNotice);
            Assert.Null(tile.Substitution);
        }
        finally { tile.Dispose(); }
    }

    /// <summary>
    /// Switching a tile to another instance of the same agent is what the layout carries afterwards.
    /// </summary>
    /// <remarks>Everything the tile runs on is derived from the instance at every launch, so the switch
    /// is the id and the save: without the save it is a choice the next start of mTiles does not honour.
    /// </remarks>
    [Fact]
    public void Switching_an_instance_is_what_the_layout_carries_afterwards()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var (chosen, other) = TwoInstancesOfOneAgent(settings);

        var kind = (ITileKind)new AgentTileKind();
        var saves = 0;
        var tile = (AgentTileViewModel)kind.Create(
            Context(directory.Path, settings, Guid.NewGuid().ToString(), saves: () => saves++),
            new JsonObject
            {
                [AgentTileKind.InstanceIdKey] = chosen.Id,
                [AgentTileKind.AgentIdKey] = chosen.AgentId,
            });

        try
        {
            tile.SwitchTo(other.Id);

            Assert.Equal(other.Id, tile.InstanceId);
            Assert.Equal(1, saves);
            Assert.Equal(other.Id,
                kind.Save(tile)?[AgentTileKind.InstanceIdKey]?.GetValue<string>());
        }
        finally { tile.Dispose(); }
    }

    /// <summary>
    /// Switching a substituted tile is the user overruling the substitution, so it is put down.
    /// </summary>
    /// <remarks><c>AgentTileKind.Save</c> writes the <em>requested</em> id while a substitution stands,
    /// which is right for a fallback nobody chose and wrong the moment somebody chooses: the new id
    /// would be gone at the next load, and the tile would come back on the instance that is not there.
    /// </remarks>
    [Fact]
    public void Switching_a_substituted_tile_puts_the_substitution_down()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var (chosen, other) = TwoInstancesOfOneAgent(settings);

        var kind = (ITileKind)new AgentTileKind();
        var tile = (AgentTileViewModel)kind.Create(
            Context(directory.Path, settings, Guid.NewGuid().ToString()),
            new JsonObject
            {
                [AgentTileKind.InstanceIdKey] = "an instance that is gone",
                [AgentTileKind.AgentIdKey] = chosen.AgentId,
            });

        try
        {
            Assert.NotNull(tile.Substitution);
            Assert.True(tile.HasLaunchNotice);

            tile.SwitchTo(other.Id);

            Assert.Null(tile.Substitution);
            Assert.False(tile.HasLaunchNotice);
            Assert.Equal(other.Id,
                kind.Save(tile)?[AgentTileKind.InstanceIdKey]?.GetValue<string>());
        }
        finally { tile.Dispose(); }
    }

    /// <summary>
    /// A captured conversation belongs to the account it was captured under, and nothing else.
    /// </summary>
    /// <remarks>The id is only meaningful inside its own <c>CODEX_HOME</c> / <c>~/.gemini</c>: handed to
    /// the new account, <c>codex resume &lt;unknown&gt;</c> stops on an interactive picker and
    /// <c>agy --conversation &lt;unknown&gt;</c> quietly starts a different conversation and exits 0.
    /// The claim goes with it, or the abandoned id stays spoken for until the application closes.
    /// </remarks>
    [Fact]
    public void Switching_to_another_account_drops_a_captured_conversation()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var captured = AiAgentCatalog.All
            .First(a => a.SessionStrategy == SessionStrategy.CapturedAfterStart);
        var (chosen, other) = TwoInstancesOfOneAgent(settings, captured.Id);
        other.SignInId = "another sign-in";

        var conversation = "conversation-" + Guid.NewGuid();
        var kind = (ITileKind)new AgentTileKind();
        var tile = (AgentTileViewModel)kind.Create(
            Context(directory.Path, settings, Guid.NewGuid().ToString()),
            new JsonObject
            {
                [AgentTileKind.InstanceIdKey] = chosen.Id,
                [AgentTileKind.AgentIdKey] = chosen.AgentId,
                [AgentTileKind.SessionIdKey] = conversation,
            });

        try
        {
            Assert.Equal(conversation, tile.SessionId);

            tile.SwitchTo(other.Id);

            Assert.Equal("", tile.SessionId);
            Assert.Null(kind.Save(tile)?[AgentTileKind.SessionIdKey]);
            Assert.True(CapturedSessions.TryClaim(conversation, "another tile"));
        }
        finally
        {
            tile.Dispose();
            CapturedSessions.ReleaseAllOf("another tile");
        }
    }

    /// <summary>
    /// The same account with another provider or model keeps the conversation.
    /// </summary>
    /// <remarks>The rule is about the sign-in and only about it: that is what relocates the CLI's own
    /// state directory, which is where the conversation lives. Dropping the id on any change at all
    /// would cost a conversation every time somebody repointed a tile's model.</remarks>
    [Fact]
    public void Switching_within_one_account_keeps_the_captured_conversation()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var captured = AiAgentCatalog.All
            .First(a => a.SessionStrategy == SessionStrategy.CapturedAfterStart);
        var (chosen, other) = TwoInstancesOfOneAgent(settings, captured.Id);
        other.ApiAccountId = "another provider";

        var conversation = "conversation-" + Guid.NewGuid();
        var tile = (AgentTileViewModel)((ITileKind)new AgentTileKind()).Create(
            Context(directory.Path, settings, Guid.NewGuid().ToString()),
            new JsonObject
            {
                [AgentTileKind.InstanceIdKey] = chosen.Id,
                [AgentTileKind.AgentIdKey] = chosen.AgentId,
                [AgentTileKind.SessionIdKey] = conversation,
            });

        try
        {
            tile.SwitchTo(other.Id);

            Assert.Equal(other.Id, tile.InstanceId);
            Assert.Equal(conversation, tile.SessionId);
        }
        finally { tile.Dispose(); }
    }

    /// <summary>
    /// The list offers this agent's available instances and nothing else.
    /// </summary>
    /// <remarks>Another agent is another program working in somebody's repository — the failure
    /// <c>AgentSubstitution</c> exists to announce — and an unavailable instance is one
    /// <c>AgentModelResolver</c> would refuse the launch of. Computed rather than written out, the way
    /// the tile chooser's own test is: which agents are installed is a fact about this machine.</remarks>
    [Fact]
    public void The_switcher_offers_this_agents_available_instances_only()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var (chosen, _) = TwoInstancesOfOneAgent(settings);

        var tile = (AgentTileViewModel)((ITileKind)new AgentTileKind()).Create(
            Context(directory.Path, settings, Guid.NewGuid().ToString()),
            new JsonObject
            {
                [AgentTileKind.InstanceIdKey] = chosen.Id,
                [AgentTileKind.AgentIdKey] = chosen.AgentId,
            });

        try
        {
            var expected = settings.Service.Settings.AiAgentInstances
                .Where(instance => instance.AgentId == tile.AgentId
                                   && AiAgentCatalog.IsAvailable(instance, settings.Service.Settings))
                .Select(instance => instance.Id);

            Assert.Equal(expected, tile.SwitchTargets.Select(instance => instance.Id));

            // An instance of another agent is never in there, whatever this machine has installed.
            var stranger = settings.Service.Settings.AiAgentInstances
                .First(instance => instance.AgentId != tile.AgentId);
            Assert.DoesNotContain(stranger.Id, tile.SwitchTargets.Select(instance => instance.Id));
            Assert.Null(tile.ConfirmationForSwitchTo(stranger.Id));

            tile.SwitchTo(stranger.Id);
            Assert.Equal(chosen.Id, tile.InstanceId);
        }
        finally { tile.Dispose(); }
    }

    /// <summary>
    /// The header's own route to a switch asks first, and an unwired question is a no.
    /// </summary>
    /// <remarks>The one safety rule of the whole feature, and the layer <c>SwitchTo</c> does not cover:
    /// switching kills whatever the shell is running. A view built without a window to ask in has no
    /// <c>ConfirmAction</c>, and the shape used beside this one — <c>ConfirmAction != null &amp;&amp;
    /// !await …</c> — would let that through silently.</remarks>
    [Fact]
    public async Task An_unanswerable_question_does_not_switch_the_tile()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var (chosen, other) = TwoInstancesOfOneAgent(settings);

        var tile = (AgentTileViewModel)((ITileKind)new AgentTileKind()).Create(
            Context(directory.Path, settings, Guid.NewGuid().ToString()),
            new JsonObject
            {
                [AgentTileKind.InstanceIdKey] = chosen.Id,
                [AgentTileKind.AgentIdKey] = chosen.AgentId,
            });
        var leaf = new LeafTileNodeViewModel(TileKindIds.Agent, tile, directory.Path,
            new TileActivationScope());

        try
        {
            leaf.RefreshAgentInstances();
            var target = leaf.AgentInstances.First(choice => choice.Label == other.Name);

            // Nothing wired: the question cannot be put, so the answer is no.
            target.SwitchCommand.Execute(null);
            Assert.Equal(chosen.Id, tile.InstanceId);

            // Wired and answered no, which is the same outcome by the other route.
            var asked = "";
            leaf.ConfirmAction = question => { asked = question; return Task.FromResult(false); };
            target.SwitchCommand.Execute(null);
            await Task.Yield();

            Assert.Contains(other.Name, asked);
            Assert.Equal(chosen.Id, tile.InstanceId);
        }
        finally { leaf.Dispose(); }
    }

    /// <summary>Two instances of one agent, so a switch has somewhere to go.</summary>
    /// <remarks>The second is a copy of the first, so the pair differs by nothing the availability rule
    /// looks at: a test about switching must not turn into a test about what is installed.</remarks>
    private static (AiAgentInstance Chosen, AiAgentInstance Other) TwoInstancesOfOneAgent(
        TempSettings settings, string? agentId = null)
    {
        var chosen = settings.Service.Settings.AiAgentInstances
            .First(instance => agentId is null || instance.AgentId == agentId);

        var other = new AiAgentInstance
        {
            AgentId = chosen.AgentId,
            Name = chosen.Name + " (second account)",
            ApiAccountId = chosen.ApiAccountId,
            SignInId = chosen.SignInId,
            Model = chosen.Model,
        };
        settings.Service.Settings.AiAgentInstances.Add(other);
        return (chosen, other);
    }

    private static TileContext Context(string workingDirectory, TempSettings settings,
        string? tileId = null, Func<string>? reads = null, Action? saves = null) =>
        new(workingDirectory, settings.Service, saves) { TileId = reads ?? (() => tileId ?? "") };
}
