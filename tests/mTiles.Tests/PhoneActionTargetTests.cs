using System.ComponentModel;
using mTiles.Models;
using mTiles.Services.Phone;
using mTiles.Services.Speech;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Which tile a phone's action button reaches.
/// </summary>
/// <remarks>
/// The phone shows one tile's name and one tile's buttons, so the press has to land in that tile and no
/// other. It did not: the list and the caption were built from the tile a phone-driven dictation was
/// aimed at, while the press was routed to whichever tile happened to be active at that instant — and
/// the action buttons are deliberately not disabled while somebody is speaking. Two kinds already share
/// an id (<c>commit</c> is Git's and the Goal tile's), so the gap was "Commit" under the name of a Git
/// tile starting a Goal tile's run, and the filter in <see cref="PhoneTileActions"/> could not catch it:
/// it was being asked about the tile the press had already gone to.
/// </remarks>
public class PhoneActionTargetTests : IDisposable
{
    private readonly TempSettings _settings = new();
    private readonly TempDirectory _models = new("mtiles-phone-actions");

    private readonly List<PhoneBridgeManager> _managers = [];
    private readonly List<DictationService> _services = [];

    public void Dispose()
    {
        foreach (var manager in _managers)
            manager.DisposeAsync().AsTask().GetAwaiter().GetResult();

        foreach (var service in _services)
            service.Dispose();

        _settings.Dispose();
        _models.Dispose();
    }

    /// <summary>
    /// The tile the phone was shown is the tile the phone presses, even after the active one has moved.
    /// </summary>
    [Fact]
    public async Task An_action_lands_in_the_tile_the_phone_is_showing()
    {
        var manager = Manager(out var active);
        var sink = (IPhoneSink)manager;

        var dictatedInto = Tile("Git #1", new ActionsStub("Commit"));
        var clickedInto = Tile("Goal #1", new ActionsStub("Commit work"));

        active.Tile = dictatedInto;
        Assert.True((await sink.BeginAsync(16_000)).Accepted);

        // The user moves on at the computer while the sentence is still being spoken. The phone is not
        // told: it goes on showing the tile its words are landing in, which is the whole point of the
        // caption.
        active.Tile = clickedInto;

        var shown = sink.DescribeActions();
        Assert.Contains("Git #1", shown);
        Assert.Contains("\"Commit\"", shown);

        Assert.Null(await sink.InvokeActionAsync(ActionsStub.Id));
        Assert.Equal(1, Content(dictatedInto).Invocations);
        Assert.Equal(0, Content(clickedInto).Invocations);

        // And the hold lasts exactly as long as the utterance: once it is over, the phone is aimed at
        // whatever is active again.
        sink.CancelStream();

        Assert.Null(await sink.InvokeActionAsync(ActionsStub.Id));
        Assert.Equal(1, Content(dictatedInto).Invocations);
        Assert.Equal(1, Content(clickedInto).Invocations);
    }

    private static ActionsStub Content(LeafTileNodeViewModel tile) => (ActionsStub)tile.Content!;

    private static LeafTileNodeViewModel Tile(string name, ActionsStub content) =>
        new(content.KindId, content, "", new TileActivationScope()) { TileName = name };

    /// <summary>Whatever the test says is the active tile, read at the moment it is asked for.</summary>
    private sealed class ActiveTile
    {
        public LeafTileNodeViewModel? Tile { get; set; }
    }

    /// <summary>
    /// A manager whose dictation can actually be started: a downloaded model that is one
    /// <c>SetLength</c>, an engine that loads nothing and a microphone that opens nothing.
    /// </summary>
    private PhoneBridgeManager Manager(out ActiveTile active)
    {
        var holder = new ActiveTile();
        active = holder;

        var model = SpeechModelFiles.PlaceOnDisk(_models.Path);

        _settings.Service.Settings.Speech.Enabled = true;
        _settings.Service.Settings.Speech.ModelId = model.Id;

        var router = new RoutedAudioCapture(new IdleMicrophone(), new PhoneAudioCapture());
        var dictation = new DictationService(_settings.Service, router, new FakeSpeechEngine { Transcript = "" },
            new SpeechModelStore(_models.Path), action => action());
        _services.Add(dictation);

        var manager = new PhoneBridgeManager(
            _settings.Service,
            dictation,
            router,
            activeTile: () => holder.Tile,
            dispatcher: new InlineUiDispatcher(),
            sessionStore: new NowherePhoneSessionStore());

        _managers.Add(manager);
        return manager;
    }

    /// <summary>Tile content offering one action, under an id two real kinds share.</summary>
    private sealed class ActionsStub(string label) : ITileActions
    {
        public const string Id = "commit";

        /// <summary>Nothing here ever changes, so there is nothing to announce.</summary>
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

        public string KindId => "stub";

        public int Invocations { get; private set; }

        public IReadOnlyList<TileAction> Actions => [new(Id, label, "check", IsEnabled: true)];

        public Task<TileActionResult> InvokeAsync(string id)
        {
            if (id == Id)
                Invocations++;

            return Task.FromResult(TileActionResult.Ok);
        }

        public void Dispose() { }
    }
}
