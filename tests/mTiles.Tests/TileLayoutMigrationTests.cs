using System.Text.Json;
using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using Avalonia.Headless;
using Avalonia.Layout;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The layout comes back looking exactly as it went in.
/// </summary>
/// <remarks>
/// <para>That is the acceptance criterion for moving a tile's kind out of a closed enum and its
/// per-kind fields into its kind's own state, and this is the golden file that enforces it: a layout in
/// the format every version before the change wrote, holding all six kinds and nested splits, loaded by
/// the new code and compared node by node. Drift then fails the build instead of failing a user on the
/// launch after an update — which is the only other place it would have shown, as a workspace of empty
/// cards.</para>
/// <para>The other two rules here are about the write rather than the read. A migration rewrites every
/// file under <c>workspaces/</c> at once, and a tile layout is the one thing in this application a user
/// cannot reconstruct from anything else.</para>
/// </remarks>
public sealed class TileLayoutMigrationTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    /// <summary>Where the layout files go — this test's own, so nothing it writes outlives it and no two
    /// of these tests can see each other's <c>.pre-kind.json</c>.</summary>
    private readonly string _layouts;

    public TileLayoutMigrationTests()
    {
        _layouts = Path.Combine(_directory.Path, "layouts");
        Directory.CreateDirectory(_layouts);
    }

    public void Dispose() => _directory.Dispose();

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TileLayoutMigrationTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>The id the golden layout's terminal was created from, so the profile is one the settings
    /// actually hold and the value has somewhere to survive to.</summary>
    private const string ProfileId = "0f2a2f4c-2c8b-4a4a-9d2a-7b1f2d3e4f50";

    /// <summary>
    /// A workspace layout exactly as an older build wrote one.
    /// </summary>
    /// <remarks>
    /// Written out rather than produced by the old code, which is the point of a golden file: what has
    /// to keep working is the bytes on somebody's disk, and those cannot be regenerated once the code
    /// that wrote them is gone. Every field is present on every leaf, including the empty ones, because
    /// that is what <c>JsonSerializer</c> did with the old DTO — and the empty ones are the interesting
    /// half: adopted unconditionally they would give a note a shell and a terminal a file path.
    /// </remarks>
    private string GoldenLayout() =>
        $$"""
        {
          "WorkspaceId": "golden",
          "RootTile": {
            "IsLeaf": false,
            "ContentType": "Empty",
            "TileId": null,
            "TileName": null,
            "ShellName": null,
            "UserProfileId": null,
            "NoteFilePath": null,
            "TodoFilePath": null,
            "GoalFilePath": null,
            "IsActive": false,
            "Settings": null,
            "SplitOrientation": "Vertical",
            "SplitRatio": 0.35,
            "First": {
              "IsLeaf": false,
              "ContentType": "Empty",
              "TileId": null,
              "TileName": null,
              "ShellName": null,
              "UserProfileId": null,
              "NoteFilePath": null,
              "TodoFilePath": null,
              "GoalFilePath": null,
              "IsActive": false,
              "Settings": null,
              "SplitOrientation": "Horizontal",
              "SplitRatio": 0.6,
              "First": {
                "IsLeaf": true,
                "ContentType": "Terminal",
                "TileId": "tile-terminal",
                "TileName": "Terminal#SwiftFox",
                "ShellName": "PowerShell",
                "UserProfileId": "{{ProfileId}}",
                "NoteFilePath": null,
                "TodoFilePath": null,
                "GoalFilePath": null,
                "IsActive": true,
                "Settings": null,
                "SplitOrientation": "Vertical",
                "SplitRatio": 0.5,
                "First": null,
                "Second": null
              },
              "Second": {
                "IsLeaf": true,
                "ContentType": "Note",
                "TileId": "tile-note",
                "TileName": "Note#3",
                "ShellName": null,
                "UserProfileId": null,
                "NoteFilePath": "{{NotePath("note")}}",
                "TodoFilePath": null,
                "GoalFilePath": null,
                "IsActive": false,
                "Settings": null,
                "SplitOrientation": "Vertical",
                "SplitRatio": 0.5,
                "First": null,
                "Second": null
              }
            },
            "Second": {
              "IsLeaf": false,
              "ContentType": "Empty",
              "TileId": null,
              "TileName": null,
              "ShellName": null,
              "UserProfileId": null,
              "NoteFilePath": null,
              "TodoFilePath": null,
              "GoalFilePath": null,
              "IsActive": false,
              "Settings": null,
              "SplitOrientation": "Horizontal",
              "SplitRatio": 0.25,
              "First": {
                "IsLeaf": false,
                "ContentType": "Empty",
                "TileId": null,
                "TileName": null,
                "ShellName": null,
                "UserProfileId": null,
                "NoteFilePath": null,
                "TodoFilePath": null,
                "GoalFilePath": null,
                "IsActive": false,
                "Settings": null,
                "SplitOrientation": "Vertical",
                "SplitRatio": 0.5,
                "First": {
                  "IsLeaf": true,
                  "ContentType": "Todo",
                  "TileId": "tile-todo",
                  "TileName": "Todo#1",
                  "ShellName": null,
                  "UserProfileId": null,
                  "NoteFilePath": null,
                  "TodoFilePath": "{{NotePath("todo")}}",
                  "GoalFilePath": null,
                  "IsActive": false,
                  "Settings": null,
                  "SplitOrientation": "Vertical",
                  "SplitRatio": 0.5,
                  "First": null,
                  "Second": null
                },
                "Second": {
                  "IsLeaf": true,
                  "ContentType": "Git",
                  "TileId": "tile-git",
                  "TileName": "Git#2",
                  "ShellName": null,
                  "UserProfileId": null,
                  "NoteFilePath": null,
                  "TodoFilePath": null,
                  "GoalFilePath": null,
                  "IsActive": false,
                  "Settings": { "showDiffPanel": false },
                  "SplitOrientation": "Vertical",
                  "SplitRatio": 0.5,
                  "First": null,
                  "Second": null
                }
              },
              "Second": {
                "IsLeaf": false,
                "ContentType": "Empty",
                "TileId": null,
                "TileName": null,
                "ShellName": null,
                "UserProfileId": null,
                "NoteFilePath": null,
                "TodoFilePath": null,
                "GoalFilePath": null,
                "IsActive": false,
                "Settings": null,
                "SplitOrientation": "Horizontal",
                "SplitRatio": 0.75,
                "First": {
                  "IsLeaf": true,
                  "ContentType": "Database",
                  "TileId": "tile-db",
                  "TileName": "DB#1",
                  "ShellName": null,
                  "UserProfileId": null,
                  "NoteFilePath": null,
                  "TodoFilePath": null,
                  "GoalFilePath": null,
                  "IsActive": false,
                  "Settings": null,
                  "SplitOrientation": "Vertical",
                  "SplitRatio": 0.5,
                  "First": null,
                  "Second": null
                },
                "Second": {
                  "IsLeaf": true,
                  "ContentType": "Goal",
                  "TileId": "tile-goal",
                  "TileName": "Goal#4",
                  "ShellName": null,
                  "UserProfileId": null,
                  "NoteFilePath": null,
                  "TodoFilePath": null,
                  "GoalFilePath": "{{GoalPath()}}",
                  "IsActive": false,
                  "Settings": null,
                  "SplitOrientation": "Vertical",
                  "SplitRatio": 0.5,
                  "First": null,
                  "Second": null
                }
              }
            }
          }
        }
        """;

    private string NotePath(string name) =>
        Path.Combine(_directory.Path, "files", name + ".md").Replace("\\", "\\\\");

    private string GoalPath() =>
        Path.Combine(_directory.Path, "files", "goal.json").Replace("\\", "\\\\");

    /// <summary>
    /// Everything the golden layout says, read back through the new code.
    /// </summary>
    /// <remarks>
    /// One test rather than eight, deliberately: the claim is about the tree as a whole, and eight
    /// tests each loading it would say the same thing eight times over while still leaving the shape
    /// unasserted.
    /// </remarks>
    [Fact]
    public void A_layout_written_before_kinds_existed_opens_unchanged() => OnUiThread(() =>
    {
        using var settings = new TempSettings();
        settings.Service.Settings.ShellProfiles.Add(new UserShellProfile
        {
            Id = ProfileId, Name = "Golden", StartupScript = "echo hi",
        });

        using var workspace = OpenGolden(settings);

        var root = Assert.IsType<SplitTileNodeViewModel>(workspace.RootTile);
        Assert.Equal(Orientation.Vertical, root.Orientation);
        Assert.Equal(0.35, root.SplitRatio);

        var topLeft = Assert.IsType<SplitTileNodeViewModel>(root.First);
        Assert.Equal(Orientation.Horizontal, topLeft.Orientation);
        Assert.Equal(0.6, topLeft.SplitRatio);

        // The terminal: its name, its identity, its shell — and the view model its kind builds.
        var terminal = Leaf(topLeft.First, TileKindIds.Terminal, "tile-terminal", "Terminal#SwiftFox");
        // Which tile was active, which is what the shortcut and a phone aim at when the workspace opens.
        Assert.Same(terminal, workspace.ActiveTile);
        var terminalContent = Assert.IsType<TerminalTileViewModel>(terminal.Content);
        Assert.Equal("PowerShell", terminalContent.Shell.DisplayName);
        // Read through the tile it belongs to rather than copied into the content, which is what makes a
        // new session and a drag-and-drop swap need no re-stamping.
        Assert.Equal("tile-terminal", terminalContent.TileId);

        // The markdown pair: same files, same folders, nothing moved on disk.
        var note = Leaf(topLeft.Second, TileKindIds.Note, "tile-note", "Note#3");
        Assert.Equal(Path.Combine(_directory.Path, "files", "note.md"),
            Assert.IsType<NoteTileViewModel>(note.Content).FilePath);

        var right = Assert.IsType<SplitTileNodeViewModel>(root.Second);
        Assert.Equal(0.25, right.SplitRatio);

        var todoAndGit = Assert.IsType<SplitTileNodeViewModel>(right.First);
        var todo = Leaf(todoAndGit.First, TileKindIds.Todo, "tile-todo", "Todo#1");
        Assert.Equal(Path.Combine(_directory.Path, "files", "todo.md"),
            Assert.IsType<TodoTileViewModel>(todo.Content).FilePath);

        // The one kind that had per-tile settings before this change, under the key it has always used.
        var git = Leaf(todoAndGit.Second, TileKindIds.Git, "tile-git", "Git#2");
        Assert.False(Assert.IsType<GitTileViewModel>(git.Content).ShowDiffPanel);

        var dbAndGoal = Assert.IsType<SplitTileNodeViewModel>(right.Second);
        Assert.Equal(0.75, dbAndGoal.SplitRatio);
        var database = Leaf(dbAndGoal.First, TileKindIds.Database, "tile-db", "DB#1");
        Assert.IsType<DatabaseTileViewModel>(database.Content);

        var goal = Leaf(dbAndGoal.Second, TileKindIds.Goal, "tile-goal", "Goal#4");
        Assert.Equal(Path.Combine(_directory.Path, "files", "goal.json"),
            Assert.IsType<GoalTileViewModel>(goal.Content).FilePath);
    });

    /// <summary>
    /// And a copy of it is kept before the first rewrite.
    /// </summary>
    /// <remarks>
    /// <c>settings.json</c> has had this rule for a while; layouts did not, and this is the only moment
    /// at which every one of those files is replaced at once.
    /// </remarks>
    [Fact]
    public void The_layout_is_copied_aside_before_it_is_migrated() => OnUiThread(() =>
    {
        using var settings = new TempSettings();
        var before = WriteGolden();

        using var workspace = OpenGolden(settings, alreadyWritten: true);

        var backup = Path.Combine(_layouts, "golden.pre-kind.json");
        Assert.True(File.Exists(backup), "the pre-migration layout was not kept");
        Assert.Equal(before, File.ReadAllText(backup));
    });

    /// <summary>
    /// A kind this build does not have leaves the file alone.
    /// </summary>
    /// <remarks>
    /// The one route by which a layout could be lost for good: a kind written by a newer build, plus the
    /// save a migration triggers, and the tile is gone from the file. The tile is shown as empty for
    /// this session; the file still says what it is, so going back to the build that wrote it brings it
    /// back.
    /// </remarks>
    [Fact]
    public void A_kind_nothing_is_registered_under_is_never_written_over() => OnUiThread(() =>
    {
        using var settings = new TempSettings();
        var layouts = new PersistenceService(_layouts);
        var original = """
        {
          "WorkspaceId": "unknown",
          "RootTile": {
            "IsLeaf": true,
            "Kind": "kaleidoscope",
            "TileId": "tile-future",
            "TileName": "Kaleidoscope#1",
            "IsActive": true
          }
        }
        """;
        File.WriteAllText(Path.Combine(_layouts, "unknown.json"), original);

        using var workspace = new WorkspaceViewModel(
            new Workspace { Id = "unknown", Name = "unknown", DirectoryPath = _directory.Path },
            layouts, settings.Service, TestTiles.Catalog(settings.Service));

        var leaf = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
        Assert.Equal(TileKindIds.None, leaf.KindId);
        Assert.Null(leaf.Content);
        // Its identity is kept, so the empty card the user sees is still the tile that was there.
        Assert.Equal("tile-future", leaf.TileId);

        Assert.False(File.Exists(Path.Combine(_layouts, "unknown.pre-kind.json")));
        Assert.Equal(original, File.ReadAllText(Path.Combine(_layouts, "unknown.json")));
    });

    /// <summary>
    /// And it is never written over later either — not by a splitter drag, a rename or a split.
    /// </summary>
    /// <remarks>
    /// The load is only the first of the writes that would lose the tile: every ordinary layout change
    /// serialises the same tree, and a leaf whose kind the catalog does not have serialises as an empty
    /// one. Nothing takes a <c>.pre-kind.json</c> copy ahead of those, so the loss would be for good.
    /// The rename here stands in for all of them — they meet at one method.
    /// </remarks>
    [Fact]
    public void A_kind_nothing_is_registered_under_survives_an_ordinary_save() => OnUiThread(() =>
    {
        using var settings = new TempSettings();
        var original = """
        {
          "WorkspaceId": "unknown",
          "RootTile": {
            "IsLeaf": true,
            "Kind": "kaleidoscope",
            "TileId": "tile-future",
            "TileName": "Kaleidoscope#1",
            "IsActive": true
          }
        }
        """;
        File.WriteAllText(Path.Combine(_layouts, "unknown.json"), original);
        File.WriteAllText(Path.Combine(_layouts, "known.json"), original
            .Replace("unknown", "known").Replace("kaleidoscope", TileKindIds.Note));

        // Its own PersistenceService each, because one holds a single debounce timer and the second
        // workspace's save would cancel the first's — which is the assertion, so it has to be its own.
        var unknown = OpenRenamed(settings, "unknown", new PersistenceService(_layouts));
        var known = OpenRenamed(settings, "known", new PersistenceService(_layouts));

        Thread.Sleep(AppDefaults.SaveDebounceMs * 2);

        // The control: the rename is a real save trigger, so the refusal above is the refusal and not a
        // change that never reached the file.
        Assert.Contains("Renamed", File.ReadAllText(Path.Combine(_layouts, "known.json")));
        Assert.Equal(original, File.ReadAllText(Path.Combine(_layouts, "unknown.json")));
        Assert.False(File.Exists(Path.Combine(_layouts, "unknown.pre-kind.json")));

        unknown.Dispose();
        known.Dispose();
    });

    /// <summary>Opens a workspace and renames its root tile — one ordinary layout change.</summary>
    private WorkspaceViewModel OpenRenamed(TempSettings settings, string id, PersistenceService layouts)
    {
        var workspace = new WorkspaceViewModel(
            new Workspace { Id = id, Name = id, DirectoryPath = _directory.Path },
            layouts, settings.Service, TestTiles.Catalog(settings.Service));
        ((LeafTileNodeViewModel)workspace.RootTile!).TileName = "Renamed";
        return workspace;
    }

    /// <summary>An empty tile carries no state of its own, so it adds no line to the file.</summary>
    [Fact]
    public void A_tile_with_nothing_to_remember_writes_nothing_down()
    {
        var node = new TileNode { IsLeaf = true, Kind = TileKindIds.None };
        Assert.Null(node.Settings);
        Assert.DoesNotContain("Settings", JsonSerializer.Serialize(node, JsonDefaults.Options));
    }

    /// <summary>
    /// A blank old field is not adopted.
    /// </summary>
    /// <remarks>
    /// Every leaf in an old layout carries all of them, so copying them across unconditionally would
    /// give a note a shell name of nothing and a terminal a file path of nothing — and a terminal whose
    /// state holds an empty <c>filePath</c> is one whose kind has to start ignoring keys it does not
    /// recognise.
    /// </remarks>
    [Fact]
    public void The_old_fields_only_move_across_when_they_say_something()
    {
        var blank = new TileNode { IsLeaf = true, ContentType = TileContentType.Note, NoteFilePath = "" };
        Assert.Null(blank.Settings);
        Assert.True(blank.IsLegacyFormat);

        var filled = new TileNode { IsLeaf = true, ContentType = TileContentType.Note, NoteFilePath = "x.md" };
        Assert.Equal("x.md", filled.Settings?["filePath"]?.GetValue<string>());
    }

    /// <summary>
    /// The order the old fields appear in does not matter.
    /// </summary>
    /// <remarks>
    /// Every layout an older build wrote has <c>"Settings": null</c> <em>after</em> the per-kind fields,
    /// and a plain setter would have wiped what they had just put there. Order-independence is a
    /// property of the format that nobody controls, so it is asserted rather than assumed.
    /// </remarks>
    [Fact]
    public void A_null_settings_object_does_not_undo_a_migrated_field()
    {
        var node = new TileNode { IsLeaf = true, ShellName = "PowerShell", Settings = null };
        Assert.Equal("PowerShell", node.Settings?["shellName"]?.GetValue<string>());
    }

    /// <summary>A tile already in the new format is not treated as a migration.</summary>
    [Fact]
    public void A_layout_already_in_the_new_format_is_left_alone()
    {
        var node = new TileNode { IsLeaf = true, Kind = TileKindIds.Git };
        Assert.False(node.IsLegacyFormat);
    }

    /// <summary>
    /// And a layout this build writes is still readable by the build before it.
    /// </summary>
    /// <remarks>
    /// <para>The migration only ever ran one way. Velopack can put an older build back — this
    /// application already writes <c>settings.json</c> to survive that — and an older build knows
    /// nothing of <c>Kind</c> or of the keys inside <c>Settings</c>, so a file holding only those opens
    /// as a workspace of empty tiles and the first splitter drag saves the emptiness over it.
    /// <c>{id}.pre-kind.json</c> is the only way back and nothing tells the user it exists.</para>
    /// <para>Asserted by deserialising into the shape that build actually had, rather than by looking
    /// for property names in a string: what has to keep working is that an older reader gets its values,
    /// and only its own DTO can say that.</para>
    /// </remarks>
    [Fact]
    public void A_saved_layout_still_says_what_an_older_build_reads()
    {
        var tree = new TileNode
        {
            IsLeaf = false,
            SplitOrientation = Orientation.Horizontal,
            SplitRatio = 0.4,
            First = new TileNode
            {
                IsLeaf = true, Kind = TileKindIds.Terminal, TileId = "t", TileName = "Terminal#1",
                Settings = new JsonObject { ["shellName"] = "PowerShell", ["userProfileId"] = ProfileId },
            },
            Second = new TileNode
            {
                IsLeaf = true, Kind = TileKindIds.Note, TileId = "n", TileName = "Note#1",
                Settings = new JsonObject { ["filePath"] = "x.md" },
            },
        };

        var older = AsOlderBuildReadsIt(tree);

        // A split has no kind, then or now.
        Assert.Equal(TileContentType.Empty, older.ContentType);
        Assert.Equal(0.4, older.SplitRatio);

        var terminal = older.First!;
        Assert.Equal(TileContentType.Terminal, terminal.ContentType);
        Assert.Equal("Terminal#1", terminal.TileName);
        Assert.Equal("PowerShell", terminal.ShellName);
        Assert.Equal(ProfileId, terminal.UserProfileId);

        // The three old file-path fields are one key, so only the kind's own is written: an older build
        // told a tile is a note and a goal at once picks whichever arm of its switch comes first.
        var note = older.Second!;
        Assert.Equal(TileContentType.Note, note.ContentType);
        Assert.Equal("x.md", note.NoteFilePath);
        Assert.Null(note.TodoFilePath);
        Assert.Null(note.GoalFilePath);
        Assert.Null(note.ShellName);
    }

    /// <summary>
    /// An agent tile degrades to a terminal rather than to nothing.
    /// </summary>
    /// <remarks>The one kind that answers with a legacy name that is not its own, and deliberately: an
    /// agent tile <em>is</em> a terminal running an agent, so a build Velopack has rolled back opens it
    /// as a plain shell — on the shell it was running, which is the other half and the reason the state
    /// carries a <c>shellName</c> this build never reads. Read as an empty tile it would be the tile
    /// itself gone from the layout; degraded, what is lost is a conversation.</remarks>
    [Fact]
    public void An_agent_tile_is_read_as_a_terminal_by_a_build_that_never_had_one()
    {
        var older = AsOlderBuildReadsIt(new TileNode
        {
            IsLeaf = true, Kind = TileKindIds.Agent, TileId = "a", TileName = "Agent#1",
            Settings = new JsonObject
            {
                ["agentInstanceId"] = "an-instance",
                ["shellName"] = "PowerShell",
            },
        });

        Assert.Equal(TileContentType.Terminal, older.ContentType);
        Assert.Equal("PowerShell", older.ShellName);
        Assert.Equal("Agent#1", older.TileName);

        // And not a shell profile it never had: an older build matching this against its own profiles
        // would launch whatever happened to share the id.
        Assert.Null(older.UserProfileId);
    }

    /// <summary>A kind that build never had is written as no kind at all.</summary>
    /// <remarks>The honest answer: it could not have built one either, so it gets the empty tile this
    /// build gives an unregistered kind — and its <c>TileId</c>, so the card is still that tile.</remarks>
    [Fact]
    public void A_kind_an_older_build_never_had_is_written_as_no_kind_at_all()
    {
        var older = AsOlderBuildReadsIt(
            new TileNode { IsLeaf = true, Kind = "kaleidoscope", TileId = "tile-future" });

        Assert.Equal(TileContentType.Empty, older.ContentType);
        Assert.Equal("tile-future", older.TileId);
    }

    /// <summary>
    /// Carrying both formats does not make a file look like an old one.
    /// </summary>
    /// <remarks>
    /// The old fields stopped being evidence of an old file the moment this build started writing them,
    /// so <see cref="TileNode.IsLegacyFormat"/> asks whether <c>Kind</c> was there as well. Without that
    /// every launch would call an up-to-date layout a migration — a save it does not need, and a
    /// <c>.pre-kind.json</c> that is not what it says it is.
    /// </remarks>
    [Fact]
    public void A_layout_carrying_both_formats_is_not_taken_for_an_old_one()
    {
        var written = JsonSerializer.Serialize(
            new TileNode
            {
                IsLeaf = true, Kind = TileKindIds.Note,
                Settings = new JsonObject { ["filePath"] = "x.md" },
            },
            JsonDefaults.Options);

        var read = JsonSerializer.Deserialize<TileNode>(written, JsonDefaults.Options)!;

        Assert.False(read.IsLegacyFormat);
        Assert.Equal(TileKindIds.Note, read.Kind);
        Assert.Equal("x.md", read.Settings?["filePath"]?.GetValue<string>());
    }

    private static LegacyTileNode AsOlderBuildReadsIt(TileNode node) =>
        JsonSerializer.Deserialize<LegacyTileNode>(
            JsonSerializer.Serialize(node, JsonDefaults.Options), JsonDefaults.Options)!;

    /// <summary>
    /// <c>TileNode</c> exactly as the build before tile kinds declared it.
    /// </summary>
    /// <remarks>A copy rather than a reference, for the same reason the golden layout above is written
    /// out by hand: the thing that has to keep working is what that build does, and that build is
    /// gone.</remarks>
    private sealed class LegacyTileNode
    {
        public bool IsLeaf { get; set; }
        public TileContentType ContentType { get; set; }
        public string? TileId { get; set; }
        public string? TileName { get; set; }
        public string? ShellName { get; set; }
        public string? UserProfileId { get; set; }
        public string? NoteFilePath { get; set; }
        public string? TodoFilePath { get; set; }
        public string? GoalFilePath { get; set; }
        public bool IsActive { get; set; }
        public Dictionary<string, object?>? Settings { get; set; }
        public Orientation SplitOrientation { get; set; } = Orientation.Vertical;
        public double SplitRatio { get; set; } = 0.5;
        public LegacyTileNode? First { get; set; }
        public LegacyTileNode? Second { get; set; }
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────

    private string WriteGolden()
    {
        var json = GoldenLayout();
        File.WriteAllText(Path.Combine(_layouts, "golden.json"), json);
        return json;
    }

    private WorkspaceViewModel OpenGolden(TempSettings settings, bool alreadyWritten = false)
    {
        if (!alreadyWritten) WriteGolden();

        return new WorkspaceViewModel(
            new Workspace { Id = "golden", Name = "golden", DirectoryPath = _directory.Path },
            new PersistenceService(_layouts),
            settings.Service,
            TestTiles.Catalog(settings.Service));
    }

    private static LeafTileNodeViewModel Leaf(TileNodeViewModel? node, string kindId, string tileId, string name)
    {
        var leaf = Assert.IsType<LeafTileNodeViewModel>(node);
        Assert.Equal(kindId, leaf.KindId);
        Assert.Equal(tileId, leaf.TileId);
        Assert.Equal(name, leaf.TileName);
        return leaf;
    }
}
