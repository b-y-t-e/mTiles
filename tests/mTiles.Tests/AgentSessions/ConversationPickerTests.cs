using System.Text.Json.Nodes;
using mTiles.AgentSessions;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Storage;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Agents.Sessions;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// Choosing which stored conversation an Agent tile is showing.
/// </summary>
/// <remarks>
/// <para>Every conversation ever held in an Agent tile is in the store and nothing prunes it — but until
/// there was a list to pick from, none of them could be reached again: a conversation was the tile's own
/// id, so the only way to have a new one in a tile was to write over what was there.</para>
/// <para>What the tile stores is a <c>conversationId</c> beside its instance, and <b>only once one has been
/// chosen</b>: absent means the tile's own id, so a layout written before this existed opens on exactly the
/// conversation it always did.</para>
/// </remarks>
public class ConversationPickerTests
{
    // ---- What a row is called -------------------------------------------------------------------

    [Theory]
    [InlineData(null, ConversationTitle.Unused)]
    [InlineData("", ConversationTitle.Unused)]
    [InlineData("   \n  ", ConversationTitle.Unused)]
    [InlineData("Fix the build", "Fix the build")]
    // A message is usually a request with a paragraph of context under it, and a list of paragraphs is not
    // a list.
    [InlineData("Fix the build\n\nIt fails on Linux only.", "Fix the build")]
    [InlineData("  Fix the build  ", "Fix the build")]
    public void A_row_is_called_by_the_first_thing_the_user_said(string? opening, string expected) =>
        Assert.Equal(expected, ConversationTitle.For(opening));

    [Fact]
    public void A_long_opening_is_cut_at_a_word()
    {
        var title = ConversationTitle.For(
            "Rewrite the launch chain so a failing command never takes the tile down with it, and say so");

        Assert.EndsWith("…", title);
        Assert.True(title.Length <= ConversationTitle.MaxLength + 1);
        Assert.DoesNotContain("  ", title);
        // Cut at a word, so what is left is readable rather than ending mid-syllable.
        Assert.StartsWith("Rewrite the launch chain so a failing command never takes the tile", title);
    }

    /// <summary>A path or one long token has no word break to cut at, and a row the width of the message is
    /// worse than a cut one.</summary>
    [Fact]
    public void An_opening_with_no_word_break_is_still_cut()
    {
        var title = ConversationTitle.For(new string('x', 200));

        Assert.Equal(ConversationTitle.MaxLength + 1, title.Length);
    }

    // ---- When it was last used ------------------------------------------------------------------

    /// <summary>Read by recognising a day, not by comparing spans: "2d 3h" is exactly the shape that has to
    /// be converted back into Tuesday before it means anything.</summary>
    [Fact]
    public void When_it_was_used_is_said_as_a_day_and_never_as_a_span()
    {
        var now = new DateTimeOffset(2026, 9, 16, 14, 0, 0, TimeSpan.Zero).ToLocalTime();

        Assert.Equal("09:30", ConversationWhen.For(At(2026, 9, 16, 9, 30), now));
        Assert.Equal("yesterday 22:05", ConversationWhen.For(At(2026, 9, 15, 22, 5), now));
        Assert.Contains("11:00", ConversationWhen.For(At(2026, 9, 12, 11, 0), now));
        // Past the week the day stops naming it, and the year only shows where it is not this one.
        Assert.DoesNotContain(":", ConversationWhen.For(At(2026, 7, 1, 11, 0), now));
        Assert.DoesNotContain("2026", ConversationWhen.For(At(2026, 7, 1, 11, 0), now));
        Assert.Contains("2025", ConversationWhen.For(At(2025, 7, 1, 11, 0), now));
    }

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute) =>
        new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local);

    // ---- What the store answers -----------------------------------------------------------------

    [Fact]
    public void The_store_lists_a_directorys_conversations_newest_first_with_what_was_asked()
    {
        var store = TestTiles.ConversationStore();
        Record(store, "older", "claude", @"C:\work", "Fix the build", DateTimeOffset.UtcNow.AddDays(-2));
        Record(store, "newer", "codex", @"C:\work", "Add a picker", DateTimeOffset.UtcNow);
        Record(store, "elsewhere", "claude", @"C:\other", "Not this one", DateTimeOffset.UtcNow);

        var listed = store.List(@"C:\work");

        Assert.Equal(["newer", "older"], listed.Select(c => c.Id));
        Assert.Equal(["Add a picker", "Fix the build"], listed.Select(c => c.Opening));
        Assert.Equal(["codex", "claude"], listed.Select(c => c.AgentId));
    }

    /// <summary>
    /// A conversation nobody said anything in is not offered.
    /// </summary>
    /// <remarks>A row exists from the moment a session starts, so every tile opened and left — and, now that
    /// "New conversation" no longer forgets, every one started and abandoned — has one. Its <c>updated_at</c>
    /// never moves, so offered they would sort to the *top* of the list, name nothing, be indistinguishable
    /// from each other and could only be removed one at a time by opening one.</remarks>
    [Fact]
    public void A_conversation_nobody_said_anything_in_is_not_offered()
    {
        var store = TestTiles.ConversationStore();
        store.Save(new ConversationRecord("empty", "claude", @"C:\work", null, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
        Record(store, "real", "claude", @"C:\work", "Fix the build", DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Equal(["real"], store.List(@"C:\work").Select(c => c.Id));
        // Kept, though: what the list does not ask about is still there to be opened and to be pruned.
        Assert.NotNull(store.Find("empty"));
    }

    // ---- What the layout keeps ------------------------------------------------------------------

    /// <summary>The whole of the compatibility promise: a tile nobody has pointed elsewhere writes exactly
    /// what it always wrote, so an older build reading that layout opens the same conversation.</summary>
    [Fact]
    public void A_tile_that_was_never_pointed_elsewhere_writes_no_conversation_id()
    {
        using var settings = new TempSettings();
        var kind = new AgentConversationTileKind(TestTiles.ConversationStore(), NoStarter.Instance);
        using var tile = Tile(kind, settings, "tile-1", new JsonObject());

        // Asked twice, because nothing may cache it: the leaf is given its id before the kind builds its
        // content, and a value taken any earlier is the constructor's default — the trap TileTreeSerializer
        // already spells out for ${tileId}.
        Assert.Equal("tile-1", tile.ConversationId);
        Assert.Equal("tile-1", tile.ConversationId);
        Assert.Null(tile.StoredConversationId);
        Assert.False(((ITileKind)kind).Save(tile)!.ContainsKey(AgentStateKeys.ConversationIdKey));
    }

    /// <summary>The leaf is given its id before the kind builds its content, so nothing built in a
    /// constructor may hold on to what it read — the trap <c>TileTreeSerializer</c> spells out for
    /// <c>${tileId}</c>, reached here by the conversation chooser asking at construction.</summary>
    [Fact]
    public void The_tiles_id_is_read_every_time_and_never_cached()
    {
        using var settings = new TempSettings();
        var tileId = "";
        var kind = new AgentConversationTileKind(TestTiles.ConversationStore(), NoStarter.Instance);
        using var tile = (AgentConversationTileViewModel)((ITileKind)kind).Create(
            new TileContext(Path.GetTempPath(), settings.Service) { TileId = () => tileId }, new JsonObject());

        tileId = "settled-later";

        Assert.Equal("settled-later", tile.ConversationId);
        Assert.Null(tile.StoredConversationId);
    }

    [Fact]
    public async Task A_chosen_conversation_is_written_down_and_a_restored_tile_opens_it()
    {
        using var settings = new TempSettings();
        var store = TestTiles.ConversationStore();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        Record(store, "earlier", "claude", Path.GetTempPath(), "Fix the build", DateTimeOffset.UtcNow.AddDays(-1));

        var kind = new AgentConversationTileKind(store, NoStarter.Instance);
        JsonObject saved;
        using (var tile = Tile(kind, settings, "tile-1",
                   new JsonObject { [AgentStateKeys.InstanceIdKey] = claude.Id }))
        {
            await tile.SwitchConversationAsync(Assert.Single(store.List(Path.GetTempPath()), c => c.Id == "earlier"));

            Assert.Equal("earlier", tile.ConversationId);
            saved = ((ITileKind)kind).Save(tile)!;
        }

        Assert.Equal("earlier", (string?)saved[AgentStateKeys.ConversationIdKey]);

        using var restored = Tile(kind, settings, "tile-1", saved);
        Assert.Equal("earlier", restored.ConversationId);
    }

    /// <summary>Deleting a conversation this tile cannot open is the case it exists for.</summary>
    /// <remarks>A tile whose stored conversation belongs to another agent refuses to start and says so, and
    /// is exactly the tile somebody deletes. Forgetting it through a host built on the <i>running</i> agent
    /// throws <c>ConversationOfAnotherAgentException</c> and forgets nothing — the delete would look as if it
    /// had worked and the conversation would still be there.</remarks>
    [Fact]
    public async Task Deleting_forgets_the_conversation_even_when_another_agent_holds_it()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var store = TestTiles.ConversationStore();
        Record(store, "tile-1", "codex", Path.GetTempPath(), "Held by codex", DateTimeOffset.UtcNow);

        using var tile = Tile(new AgentConversationTileKind(store, NoStarter.Instance), settings, "tile-1",
            new JsonObject { [AgentStateKeys.InstanceIdKey] = claude.Id });
        tile.ConfirmAction = _ => Task.FromResult(true);

        await tile.InvokeAsync(AgentConversationTileViewModel.DeleteConversationActionId);

        Assert.Null(store.Find("tile-1"));
        Assert.Empty(store.ReadEvents("tile-1"));
        // And the tile has moved on rather than being left on something that is gone.
        Assert.NotEqual("tile-1", tile.ConversationId);
    }

    /// <summary>
    /// Delete acts on the conversation that was open when it was asked for, not on whatever is open when it
    /// is answered.
    /// </summary>
    /// <remarks>A dialog stays open for as long as somebody takes to answer it, and the picker is live behind
    /// it. Reading the id afterwards names the conversation the user has just asked to <i>open</i> — so the
    /// gesture would destroy the one thing they were reaching for, along with its checkpoints.</remarks>
    [Fact]
    public async Task Delete_cannot_be_turned_on_a_conversation_picked_while_it_was_asking()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var store = TestTiles.ConversationStore();
        Record(store, "tile-1", "claude", Path.GetTempPath(), "The open one", DateTimeOffset.UtcNow);
        Record(store, "other", "claude", Path.GetTempPath(), "The one reached for", DateTimeOffset.UtcNow);

        using var tile = Tile(new AgentConversationTileKind(store, NoStarter.Instance), settings, "tile-1",
            new JsonObject { [AgentStateKeys.InstanceIdKey] = claude.Id });

        // Answering the question is where the other conversation gets picked, which is the whole race.
        var switching = Task.CompletedTask;
        tile.ConfirmAction = _ =>
        {
            switching = tile.SwitchConversationAsync(store.List(Path.GetTempPath()).Single(c => c.Id == "other"));
            return Task.FromResult(true);
        };

        await tile.InvokeAsync(AgentConversationTileViewModel.DeleteConversationActionId);
        await switching;

        Assert.Null(store.Find("tile-1"));
        Assert.NotNull(store.Find("other"));
        Assert.NotEmpty(store.ReadEvents("other"));
    }

    /// <summary>It used to forget the old conversation; with a list to pick from, the old one is a row rather
    /// than a loss.</summary>
    [Fact]
    public async Task New_conversation_leaves_the_old_one_in_the_store_and_in_the_list()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var store = TestTiles.ConversationStore();
        Record(store, "tile-1", "claude", Path.GetTempPath(), "The old one", DateTimeOffset.UtcNow);

        using var tile = Tile(new AgentConversationTileKind(store, NoStarter.Instance), settings, "tile-1",
            new JsonObject { [AgentStateKeys.InstanceIdKey] = claude.Id });

        await tile.InvokeAsync(AgentConversationTileViewModel.NewConversationActionId);

        Assert.NotEqual("tile-1", tile.ConversationId);
        Assert.NotNull(store.Find("tile-1"));
        Assert.NotEmpty(store.ReadEvents("tile-1"));
        Assert.Contains(tile.Conversations.Options, o => o.Summary.Id == "tile-1" && o.IsPickable);
    }

    /// <summary>The agent comes with the conversation: a resume token is only ever handed back to the CLI
    /// that issued it.</summary>
    [Fact]
    public async Task Picking_another_agents_conversation_moves_the_tile_onto_that_agent()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var store = TestTiles.ConversationStore();
        Record(store, "codex-one", "codex", Path.GetTempPath(), "Held by codex", DateTimeOffset.UtcNow);

        using var tile = Tile(new AgentConversationTileKind(store, NoStarter.Instance), settings, "tile-1",
            new JsonObject { [AgentStateKeys.InstanceIdKey] = claude.Id });

        await tile.SwitchConversationAsync(store.List(Path.GetTempPath()).Single(c => c.Id == "codex-one"));

        Assert.Equal("codex-one", tile.ConversationId);
        Assert.Equal("codex", tile.Agent.Id);
        Assert.Equal("codex", tile.Instance.AgentId);
    }

    // ---- Two tiles, one conversation ------------------------------------------------------------

    /// <summary>A tile refused a conversation because another tile holds it still names that conversation,
    /// and deleting it from there would take the events and checkpoints out from under the other tile's live
    /// host.</summary>
    [Fact]
    public async Task A_conversation_another_tile_is_showing_is_not_deleted_from_here()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var store = TestTiles.ConversationStore();
        Record(store, "shared-delete", "claude", Path.GetTempPath(), "Theirs", DateTimeOffset.UtcNow);
        try
        {
            Assert.True(OpenConversations.TryHold("shared-delete", "tile-holder"));
            using var tile = Tile(new AgentConversationTileKind(store, NoStarter.Instance), settings,
                "shared-delete", new JsonObject { [AgentStateKeys.InstanceIdKey] = claude.Id });
            var asked = false;
            tile.ConfirmAction = _ =>
            {
                asked = true;
                return Task.FromResult(true);
            };

            await tile.InvokeAsync(AgentConversationTileViewModel.DeleteConversationActionId);

            Assert.False(asked);
            Assert.NotNull(store.Find("shared-delete"));
            Assert.NotEmpty(store.ReadEvents("shared-delete"));
            Assert.Equal("shared-delete", tile.ConversationId);
        }
        finally
        {
            OpenConversations.ReleaseAllOf("tile-holder");
        }
    }

    /// <summary>The refusal is the start's, not only the chooser's: a tile restored onto a conversation another
    /// tile already holds must not build a second host of it.</summary>
    [Fact]
    public async Task A_tile_starting_on_a_conversation_another_tile_holds_says_so_and_starts_nothing()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var store = TestTiles.ConversationStore();
        try
        {
            Assert.True(OpenConversations.TryHold("shared-start", "tile-holder"));
            using var tile = Tile(new AgentConversationTileKind(store, NoStarter.Instance), settings,
                "shared-start", new JsonObject { [AgentStateKeys.InstanceIdKey] = claude.Id });

            await tile.InvokeAsync(TileActionIds.Restart);

            Assert.Contains("Another tile is already showing this conversation", tile.LaunchProblem);
            Assert.False(OpenConversations.IsHeldByAnother("shared-start", "tile-holder"));
            Assert.Null(store.Find("shared-start"));
        }
        finally
        {
            OpenConversations.ReleaseAllOf("tile-holder");
        }
    }

    /// <summary>It could not happen until a conversation could be chosen — tile ids are unique by
    /// construction. Two hosts of one conversation each number their events from what the store held when
    /// they were built, so both write the same sequence numbers and one of the two is lost.</summary>
    [Fact]
    public void A_conversation_another_tile_is_showing_is_refused_with_the_reason()
    {
        try
        {
            Assert.True(OpenConversations.TryHold("shared", "tile-a"));
            Assert.False(OpenConversations.TryHold("shared", "tile-b"));
            Assert.True(OpenConversations.IsHeldByAnother("shared", "tile-b"));
            // A tile re-taking its own — every restart does — is not another tile.
            Assert.True(OpenConversations.TryHold("shared", "tile-a"));
            Assert.False(OpenConversations.IsHeldByAnother("shared", "tile-a"));

            OpenConversations.ReleaseAllOf("tile-a");
            Assert.True(OpenConversations.TryHold("shared", "tile-b"));
        }
        finally
        {
            OpenConversations.ReleaseAllOf("tile-a");
            OpenConversations.ReleaseAllOf("tile-b");
        }
    }

    // ---- The list itself ------------------------------------------------------------------------

    /// <summary>A tile opened on a conversation nobody has said anything in has no row in the store yet, and
    /// a chooser with no selection reads as a tile that has lost its place.</summary>
    [Fact]
    public void The_open_conversation_is_in_the_list_even_when_the_store_has_never_heard_of_it()
    {
        var chooser = new ConversationChooser(TestTiles.ConversationStore(), @"C:\work", () => "mine",
            () => "claude", _ => null, action => action(), _ => { }, () => { });

        var only = Assert.Single(chooser.Options, option => !option.IsNew);

        Assert.Equal("mine", only.Summary.Id);
        Assert.True(only.IsCurrent);
        Assert.Same(only, chooser.Selected);
    }

    /// <summary>
    /// Starting a conversation is a row in the list, because it was reachable from nowhere else.
    /// </summary>
    /// <remarks>The tile declares it in <c>ITileActions.Actions</c>, but the tile header's overflow is a
    /// hand-written list of menu items and renders no content action, and <c>NeedsLocalScreen</c> keeps a phone
    /// away — so the gesture existed and had no way in on any screen.</remarks>
    [Fact]
    public void Starting_a_conversation_is_the_first_row_and_leaves_the_selection_alone()
    {
        var started = 0;
        var chooser = new ConversationChooser(TestTiles.ConversationStore(), @"C:\work", () => "mine",
            () => "claude", _ => null, action => action(), _ => { }, () => started++);

        chooser.Draw([Summary("mine", "claude")]);

        Assert.True(chooser.Options[0].IsNew);
        Assert.Equal("New conversation", chooser.Options[0].Title);

        chooser.Selected = chooser.Options[0];

        Assert.Equal(1, started);
        // Put back, or the strip would name the open conversation "New conversation" until the start redrew it.
        Assert.Equal("mine", chooser.Selected!.Summary.Id);
    }

    [Fact]
    public void A_refused_row_is_offered_dimmed_and_puts_the_selection_back()
    {
        var picked = new List<string>();
        var chooser = new ConversationChooser(TestTiles.ConversationStore(), @"C:\work", () => "mine",
            () => "claude", summary => summary.Id == "theirs" ? "Another tile has it." : null,
            action => action(), summary => picked.Add(summary.Id), () => { });

        chooser.Draw([
            Summary("mine", "claude"),
            Summary("theirs", "claude"),
            Summary("free", "claude"),
        ]);

        var refused = chooser.Options.Single(o => o.Summary.Id == "theirs");
        Assert.False(refused.IsPickable);
        Assert.Equal("Another tile has it.", refused.Reason);

        // Offered rather than hidden, and never disabled: a disabled item is out of Avalonia's hit test, so
        // the sentence explaining the refusal would never be drawn.
        chooser.Selected = refused;
        Assert.Empty(picked);
        Assert.Equal("mine", chooser.Selected!.Summary.Id);

        chooser.Selected = chooser.Options.Single(o => o.Summary.Id == "free");
        Assert.Equal(["free"], picked);
    }

    /// <summary>A conversation belongs to the agent that holds it, so a machine with no instance of that
    /// agent is told why rather than shown the transcript on a CLI that has never seen it.</summary>
    [Fact]
    public void A_conversation_of_an_agent_that_cannot_run_here_is_refused_by_name()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        settings.Service.Settings.AiAgentInstances.RemoveAll(i => i.AgentId == "codex");

        var store = TestTiles.ConversationStore();
        Record(store, "codex-one", "codex", Path.GetTempPath(), "Something", DateTimeOffset.UtcNow);
        using var tile = Tile(new AgentConversationTileKind(store, NoStarter.Instance), settings, "tile-1",
            new JsonObject { [AgentStateKeys.InstanceIdKey] = claude.Id });

        tile.Conversations.Draw(store.List(Path.GetTempPath()));

        var refused = tile.Conversations.Options.Single(o => o.Summary.Id == "codex-one");
        Assert.False(refused.IsPickable);
        Assert.Contains(AiAgentCatalog.Find("codex")!.DisplayName, refused.Reason);
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static ConversationSummary Summary(string id, string agentId) =>
        new(id, agentId, DateTimeOffset.UtcNow, id);

    private static void Record(IConversationStore store, string id, string agentId, string directory,
        string opening, DateTimeOffset at)
    {
        store.Save(new ConversationRecord(id, agentId, directory, null, at, at));
        store.Append(id, [
            new SessionStateChanged(AgentSessionState.Ready, null) { Sequence = 1, At = at },
            new UserMessageAdded("m1", opening, []) { Sequence = 2, At = at },
            new UserMessageAdded("m2", "and then this", []) { Sequence = 3, At = at },
        ]);
    }

    private static AgentConversationTileViewModel Tile(AgentConversationTileKind kind, TempSettings settings,
        string tileId, JsonObject state) =>
        (AgentConversationTileViewModel)((ITileKind)kind).Create(
            new TileContext(Path.GetTempPath(), settings.Service) { TileId = () => tileId }, state);

    /// <summary>Prepares no launch, so nothing here starts whichever CLIs the machine happens to have.</summary>
    private sealed class NoStarter : IAgentSessionStarter
    {
        public static NoStarter Instance { get; } = new();

        public Task<(AgentSessionLaunch? Launch, string? Problem)> PrepareAsync(AppSettings settings,
            IAiAgent agent, AiAgentInstance instance, string workingDirectory, string conversationId,
            string? resumeToken, CancellationToken ct) =>
            Task.FromResult<(AgentSessionLaunch?, string?)>((null, "Not in a test."));

        public IAgentSession Create(IAiAgent agent, AgentSessionLaunch launch, IAgentEventSink sink) =>
            throw new NotSupportedException();
    }
}
