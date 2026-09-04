using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Shells;
using mTiles.Services.Speech;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using Terminal.Avalonia;
using Terminal.Pty;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Changing a tile into another kind, in place.
/// </summary>
/// <remarks>
/// <para>The tile stays the same tile: same id, same place in the tree, same activation. What changes is
/// what it holds — and that is the only irreversible part of it, which is why the order the flow is
/// written in is what these tests are mostly about. The kind's own step comes before anything is
/// destroyed, the question comes after it, and only then is the old content taken apart.</para>
/// <para>Most of them run against a catalog of stub kinds rather than the application's own. What is
/// being pinned is the conversion, not any one kind: a stub says exactly what state it was built from
/// and how often it was disposed of, where a real note answers neither. The two that need a real one —
/// a terminal's shell, and what the layout comes out looking like — use it.</para>
/// </remarks>
public sealed class TileKindChangeTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    // ── stub kinds ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Content that answers the two questions a conversion asks of it.</summary>
    private sealed class StubTile(string kindId, JsonObject? state) : ObservableObject, ITile
    {
        public string KindId { get; } = kindId;

        /// <summary>What it was built from, so a setup step's choice can be followed all the way in.
        /// </summary>
        public JsonObject? State { get; } = state;

        public int Disposals { get; private set; }

        public void Dispose() => Disposals++;
    }

    /// <summary>A kind with nothing in it but the answers the conversion needs.</summary>
    /// <param name="setupOptions">What it asks before it can be built — none, or the one card that
    /// carries a state through.</param>
    private sealed class StubKind(string id, IReadOnlyList<TileSetupOption> setupOptions) : ITileKind
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        public string IconId => "note";
        public string AccentKey => "TileAccentNote";
        public string NamePrefix => Id;

        public string NameFor(IReadOnlySet<string> used) => $"{Id}#1";

        public IReadOnlyList<TileSetupOption> SetupOptions(TileContext context) => setupOptions;

        public ITile Create(TileContext context, JsonObject? state) => new StubTile(Id, state);

        public JsonObject? Save(ITile tile) => (tile as StubTile)?.State;
    }

    private const string Plain = "plain";
    private const string Other = "other";
    private const string Asking = "asking";

    /// <summary>The one card the asking kind offers, and the state it carries.</summary>
    private static readonly TileSetupOption AskedOption =
        new("only choice", "note", "TileAccentNote", new JsonObject { ["chosen"] = "yes" });

    private static TileCatalog StubCatalog() =>
        new TileCatalog()
            .Register(new StubKind(Plain, []), _ => new UserControl())
            .Register(new StubKind(Other, []), _ => new UserControl())
            .Register(new StubKind(Asking, [AskedOption]), _ => new UserControl());

    /// <summary>A tile holding stub content, wired the way a workspace wires one.</summary>
    private (LeafTileNodeViewModel Tile, StubTile Content) StubTileNode(TempSettings settings)
    {
        var content = new StubTile(Plain, state: null);
        var context = new TileContext(_directory.Path, settings.Service);
        var tile = new LeafTileNodeViewModel(Plain, content, _directory.Path,
            new TileActivationScope(), StubCatalog(), context);
        return (tile, content);
    }

    private static Task ChangeTo(LeafTileNodeViewModel tile, string kindId) =>
        tile.BeginChangeKindCommand.ExecuteAsync(kindId);

    // ── what the menu offers ────────────────────────────────────────────────────────────────────────

    /// <summary>Every registered kind but the one the tile already is.</summary>
    /// <remarks>An entry that changes a tile into what it already is would destroy its content to build
    /// the same thing again — a click that can only lose something.</remarks>
    [Fact]
    public void The_menu_offers_every_kind_but_the_current_one()
    {
        using var settings = new TempSettings();
        var (tile, _) = StubTileNode(settings);

        tile.RefreshChangeKindOptions();

        Assert.True(tile.CanChangeKind);
        Assert.Equal([Other, Asking], tile.ChangeKindOptions.Select(choice => choice.Label));
    }

    /// <summary>An empty tile is offered nothing, and refuses the command if it is reached anyway.
    /// </summary>
    /// <remarks>It has a chooser of its own, whose cards are this same list. Offered here as well, the
    /// tile would be asked what changing costs while it is holding nothing - a question with no true
    /// answer to give.</remarks>
    [Fact]
    public async Task An_empty_tile_is_not_offered_a_change_of_kind()
    {
        using var settings = new TempSettings();
        var context = new TileContext(_directory.Path, settings.Service);
        var tile = new LeafTileNodeViewModel(TileKindIds.None, null, _directory.Path,
            new TileActivationScope(), StubCatalog(), context);

        tile.RefreshChangeKindOptions();

        Assert.False(tile.CanChangeKind);
        Assert.Empty(tile.ChangeKindOptions);

        var asked = 0;
        tile.ConfirmAction = _ => { asked++; return Task.FromResult(true); };

        await ChangeTo(tile, Other);

        Assert.Equal(0, asked);
        Assert.Equal(TileKindIds.None, tile.KindId);
        Assert.Null(tile.Content);
    }


    // ── the conversion itself ───────────────────────────────────────────────────────────────────────

    /// <summary>The old content is taken apart exactly once, and the new one is not.</summary>
    [Fact]
    public async Task Converting_disposes_the_old_content_exactly_once()
    {
        using var settings = new TempSettings();
        var (tile, content) = StubTileNode(settings);

        await ChangeTo(tile, Other);

        Assert.Equal(1, content.Disposals);
        Assert.Equal(Other, tile.KindId);

        var replacement = Assert.IsType<StubTile>(tile.Content);
        Assert.Equal(0, replacement.Disposals);
    }

    /// <summary>Refusing the question leaves the tile exactly as it was.</summary>
    /// <remarks>The only moment anything is destroyed is after this answer, so "no" has to mean the same
    /// content, the same kind and nothing written to the layout — a saved conversion nobody agreed to
    /// would outlive the session that refused it.</remarks>
    [Fact]
    public async Task A_refused_confirmation_changes_nothing()
    {
        using var settings = new TempSettings();
        var (tile, content) = StubTileNode(settings);

        var saves = 0;
        tile.LayoutChanged = () => saves++;
        tile.ConfirmAction = _ => Task.FromResult(false);

        await ChangeTo(tile, Other);

        Assert.Same(content, tile.Content);
        Assert.Equal(0, content.Disposals);
        Assert.Equal(Plain, tile.KindId);
        Assert.Equal(0, saves);
    }

    /// <summary>A tile closed while the question is on screen builds nothing.</summary>
    /// <remarks>The question is the one point in the change at which the tile can go underneath it: its
    /// Dispose has already run, so content built afterwards is content nobody will ever take apart -
    /// for a terminal, a shell left running with nothing pointing at it. The claim TileLauncher makes
    /// with IsCurrentLaunch, for the same reason.</remarks>
    [Fact]
    public async Task A_tile_closed_while_the_question_is_open_builds_nothing()
    {
        using var settings = new TempSettings();
        var (tile, content) = StubTileNode(settings);

        tile.ConfirmAction = _ =>
        {
            tile.Dispose();
            return Task.FromResult(true);
        };

        await ChangeTo(tile, Other);

        Assert.Same(content, tile.Content);
        Assert.Equal(1, content.Disposals);
        Assert.Equal(Plain, tile.KindId);
    }


    /// <summary>The question names the kind being asked for, and what the current one costs.</summary>
    [Fact]
    public async Task The_question_is_the_one_the_rule_writes()
    {
        using var settings = new TempSettings();
        var (tile, _) = StubTileNode(settings);

        string? asked = null;
        tile.ConfirmAction = question => { asked = question; return Task.FromResult(true); };

        await ChangeTo(tile, Other);

        Assert.Equal(TileConversion.Warning(Plain, Other), asked);
    }

    /// <summary>The tile is the same tile in the same place afterwards.</summary>
    /// <remarks>The id in particular, and it is worth saying why: an agent's conversation is keyed by
    /// it, so keeping it is what makes <c>agent → note → agent</c> come back to the same conversation.
    /// A fresh id would also break the tile's link with the full-screen scope and the activation.
    /// </remarks>
    [Fact]
    public async Task Converting_keeps_the_tile_in_place()
    {
        using var settings = new TempSettings();
        var (tile, _) = StubTileNode(settings);

        tile.Activate();
        var id = tile.TileId;
        var parent = tile.Parent;

        await ChangeTo(tile, Other);

        Assert.Equal(id, tile.TileId);
        Assert.Same(parent, tile.Parent);
        Assert.True(tile.IsActive);
    }

    /// <summary>Changing a tile into what it already is does nothing at all.</summary>
    [Fact]
    public async Task Changing_a_tile_into_its_own_kind_is_refused()
    {
        using var settings = new TempSettings();
        var (tile, content) = StubTileNode(settings);

        await ChangeTo(tile, Plain);

        Assert.Same(content, tile.Content);
        Assert.Equal(0, content.Disposals);
    }

    // ── the kind's own step ─────────────────────────────────────────────────────────────────────────

    /// <summary>A kind that asks something asks it before anything is destroyed.</summary>
    /// <remarks>The whole of the ordering decision: the step is drawn over content that is still
    /// running, so a terminal goes on working while the shell for its successor is chosen — and cancel
    /// has something to go back to.</remarks>
    [Fact]
    public async Task The_setup_step_of_a_conversion_can_be_cancelled()
    {
        using var settings = new TempSettings();
        var (tile, content) = StubTileNode(settings);

        await ChangeTo(tile, Asking);

        Assert.True(tile.IsChoosingSetup);
        Assert.Same(content, tile.Content);
        Assert.Equal(0, content.Disposals);

        tile.CancelSetupCommand.Execute(null);

        Assert.False(tile.IsChoosingSetup);
        Assert.Same(content, tile.Content);
        Assert.Equal(Plain, tile.KindId);
        Assert.Equal(0, content.Disposals);
    }

    /// <summary>A step left standing is closed by the conversion that overtakes it.</summary>
    /// <remarks>The header stays clickable while the step is drawn, so a second Change type can be
    /// picked from under it. Left on screen it would hide the content just put in place — and one of its
    /// stale cards, clicked afterwards, would convert again and dispose what was built.</remarks>
    [Fact]
    public async Task A_conversion_closes_a_setup_step_it_overtakes()
    {
        using var settings = new TempSettings();
        var (tile, content) = StubTileNode(settings);

        await ChangeTo(tile, Asking);
        Assert.True(tile.IsChoosingSetup);

        await ChangeTo(tile, Other);

        Assert.False(tile.IsChoosingSetup);
        Assert.Empty(tile.SetupOptions);
        Assert.Equal(Other, tile.KindId);
        Assert.Equal(1, content.Disposals);
    }

    /// <summary>And what is picked there is what the new content is built from.</summary>
    [Fact]
    public async Task The_option_picked_in_the_step_becomes_the_new_tiles_state()
    {
        using var settings = new TempSettings();
        var (tile, content) = StubTileNode(settings);

        await ChangeTo(tile, Asking);
        await tile.SelectSetupOptionCommand.ExecuteAsync(tile.SetupOptions.Single());

        Assert.Equal(Asking, tile.KindId);
        Assert.Equal(1, content.Disposals);
        Assert.Equal(AskedOption.State?.ToJsonString(),
            Assert.IsType<StubTile>(tile.Content).State?.ToJsonString());
    }

    /// <summary>The same step, reached from an empty tile, still fills it in rather than converting.
    /// </summary>
    /// <remarks>One field tells the two apart, so the branch that reads it is worth a test from both
    /// sides: an empty tile has no content to ask about and no question to put.</remarks>
    [Fact]
    public async Task The_same_step_still_fills_in_an_empty_tile()
    {
        using var settings = new TempSettings();
        var context = new TileContext(_directory.Path, settings.Service);
        var tile = new LeafTileNodeViewModel(TileKindIds.None, null, _directory.Path,
            new TileActivationScope(), StubCatalog(), context);

        var asked = 0;
        tile.ConfirmAction = _ => { asked++; return Task.FromResult(true); };

        tile.SelectKindCommand.Execute(Asking);
        Assert.True(tile.IsChoosingSetup);

        await tile.SelectSetupOptionCommand.ExecuteAsync(tile.SetupOptions.Single());

        Assert.Equal(Asking, tile.KindId);
        Assert.Equal(0, asked);
    }

    // ── the rest of the tile ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A maximized tile turned into a kind that cannot be maximized gives the workspace back.
    /// </summary>
    /// <remarks>Otherwise the splits above it stay soloed on a tile whose header no longer offers the
    /// way out — half a workspace invisible for the rest of the session, which is the failure
    /// <c>Dispose</c> calls <c>Forget</c> to avoid.</remarks>
    [Fact]
    public async Task A_maximized_tile_converted_to_a_kind_that_cannot_be_maximized_is_restored()
    {
        using var settings = new TempSettings();
        using var workspace = new WorkspaceViewModel(
            new Workspace { Name = "test", DirectoryPath = _directory.Path },
            settings.Layouts, settings.Service, TestTiles.Catalog(settings.Service));

        var root = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
        root.SelectKindCommand.Execute(TileKindIds.Note);
        root.SplitVerticalCommand.Execute(null);

        var split = Assert.IsType<SplitTileNodeViewModel>(workspace.RootTile);
        var note = Assert.IsType<LeafTileNodeViewModel>(split.First);

        note.ToggleMaximizeCommand.Execute(null);
        Assert.True(note.IsMaximized);

        // Git lays itself out in panes of its own, so it is one of the kinds that does not maximize.
        await ChangeTo(note, TileKindIds.Git);

        Assert.False(note.IsMaximized);
        Assert.Null(split.Solo);
    }

    /// <summary>
    /// The layout written after a conversion holds the new kind's fields and none of the old one's.
    /// </summary>
    /// <remarks>Each kind saves its own state, so this is a promise the registry already makes — but the
    /// conversion is the one route that leaves a leaf holding a kind it was not created as, and a
    /// terminal's shell name left behind in a note is what an older build would open as a shell.
    /// </remarks>
    [Fact]
    public void Converting_writes_only_the_new_kinds_fields() => OnUiThread(async () =>
    {
        using var settings = new TempSettings();
        using var workspace = new WorkspaceViewModel(
            new Workspace { Name = "test", DirectoryPath = _directory.Path },
            settings.Layouts, settings.Service, TestTiles.Catalog(settings.Service));

        var root = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
        root.SelectKindCommand.Execute(TileKindIds.Terminal);
        if (root.IsChoosingSetup)
            await root.SelectSetupOptionCommand.ExecuteAsync(root.SetupOptions.First());

        var id = root.TileId;
        await ChangeTo(root, TileKindIds.Note);

        var node = new TileTreeSerializer(TestTiles.Catalog(settings.Service),
            new TileContext(_directory.Path, settings.Service),
            _ => "name", _ => { }, new TileActivationScope()).Serialize(root);

        Assert.NotNull(node);
        Assert.Equal(TileKindIds.Note, node.Kind);
        Assert.Equal(id, node.TileId);
        Assert.Null(node.ShellName);
        Assert.NotNull(node.Settings?[MarkdownTileKind.FilePathKey]);
    });

    /// <summary>What was written comes back as the tile that wrote it.</summary>
    [Fact]
    public void Layout_round_trip_after_a_conversion() => OnUiThread(async () =>
    {
        using var settings = new TempSettings();
        var catalog = TestTiles.Catalog(settings.Service);
        var context = new TileContext(_directory.Path, settings.Service);
        var serializer = new TileTreeSerializer(catalog, context, _ => "name", _ => { },
            new TileActivationScope());

        using var workspace = new WorkspaceViewModel(
            new Workspace { Name = "test", DirectoryPath = _directory.Path },
            settings.Layouts, settings.Service, catalog);

        var root = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
        root.SelectKindCommand.Execute(TileKindIds.Note);
        await ChangeTo(root, TileKindIds.Todo);

        var written = serializer.Serialize(root);
        var read = Assert.IsType<LeafTileNodeViewModel>(serializer.Deserialize(written!, () => { }).Root);

        Assert.Equal(TileKindIds.Todo, read.KindId);
        Assert.Equal(root.TileId, read.TileId);
        Assert.Equal(root.TileName, read.TileName);
        read.Dispose();
    });

    /// <summary>A terminal's shell goes with the tile it was reached through.</summary>
    /// <remarks>The one thing a conversion destroys that nothing can bring back, so it has to actually
    /// happen: a child left running with no UI able to reach it is an orphan process for the life of
    /// the session.</remarks>
    [Fact]
    public void Converting_a_terminal_ends_its_session() => OnUiThread(async () =>
    {
        using var settings = new TempSettings();
        FakePty? pty = null;
        using var control = new TerminalControl { PtyFactory = options => pty = new FakePty(options) };

        var terminal = new TerminalTileViewModel("", new ShellInstallation(new BashTerminal(), "fake-shell"),
            settings.Service, LaunchScripts.None);
        terminal.AttachControl(control);
        control.Start(new PtyOptions { Command = "fake-shell", Arguments = ["-l"] });

        var tile = new LeafTileNodeViewModel(TileKindIds.Terminal, terminal, _directory.Path,
            new TileActivationScope(), TestTiles.Catalog(settings.Service),
            new TileContext(_directory.Path, settings.Service));

        await ChangeTo(tile, TileKindIds.Note);

        Assert.Equal(TileKindIds.Note, tile.KindId);
        Assert.NotNull(pty);
        Assert.True(pty.Disposed);
    });

    /// <summary>A recording this tile owns is thrown away rather than delivered into content that is
    /// no longer there.</summary>
    /// <remarks>Asked of the service rather than of the tile's own flag, which is set from a dispatcher
    /// callback and still reads false between the start and that callback — the same rule
    /// <c>Dispose</c> follows.</remarks>
    [Fact]
    public async Task Converting_a_dictating_tile_cancels_the_recording()
    {
        using var settings = new TempSettings();
        using var models = new FakeSpeechModels();
        settings.Service.Settings.Speech.Enabled = true;
        settings.Service.Settings.Speech.ModelId = models.ModelId;

        using var dictation = new DictationService(settings.Service, new SilentCapture(),
            new SilentEngine(), models.Store, action => action());

        var (tile, _) = StubTileNode(settings);
        tile.Dictation = dictation;

        Assert.True(dictation.Start(tile, _ => true));

        await ChangeTo(tile, Other);

        Assert.Equal(DictationState.Idle, dictation.State);
    }

    // ── the harness the two speech tests need ───────────────────────────────────────────────────────

    private sealed class SilentCapture : IAudioCapture
    {
        private sealed record Handle : IRecordingHandle;

        public bool IsAvailable => true;
        public bool IsRecording { get; private set; }
        public IReadOnlyList<string> GetInputDevices(bool rescan = false) => ["fake microphone"];
        public void Start(string deviceName) => IsRecording = true;

        public IRecordingHandle? Detach()
        {
            if (!IsRecording) return null;
            IsRecording = false;
            return new Handle();
        }

        public float[] Finish(IRecordingHandle? detached) => [];
        public void Dispose() { }
    }

    private sealed class SilentEngine : ISpeechToTextEngine
    {
        public bool IsLoaded => false;
        public Task LoadAsync(string modelPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public void Unload() { }
        public Task<string> TranscribeAsync(float[] samples, TranscriptionOptions options,
            CancellationToken cancellationToken = default) => Task.FromResult("");
        public void Dispose() { }
    }

    /// <summary>A store pointed at a file of exactly the right size, so the service believes the model
    /// is downloaded without any hundreds of megabytes being involved.</summary>
    private sealed class FakeSpeechModels : IDisposable
    {
        private static readonly SpeechModel Model = SpeechModelCatalog.Find("base")!;

        private readonly TempDirectory _directory = new();

        public FakeSpeechModels()
        {
            Store = new SpeechModelStore(_directory.Path);
            using var file = File.Create(Path.Combine(_directory.Path, Model.FileName));
            file.SetLength(Model.DownloadBytes);
        }

        public SpeechModelStore Store { get; }
        public string ModelId => Model.Id;

        public void Dispose() => _directory.Dispose();
    }

    private static void OnUiThread(Func<Task> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TileKindChangeTests).Assembly);
        session.Dispatch(async () => { await body(); return true; }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }
}
