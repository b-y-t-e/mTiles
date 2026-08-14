using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a fresh installation is offered, and when it is asked at all.
/// </summary>
/// <remarks>
/// Nothing ships with the application — 456 MB is not an installer — so "dictation on, no model" is the
/// state every first run starts in, and until the question is put the feature is invisible: the shortcut
/// deliberately stands down, and the only hint is a microphone button that answers with a complaint.
/// </remarks>
public class SpeechModelOfferTests : IDisposable
{
    private sealed class SilentCapture(bool available) : IAudioCapture
    {
        public bool IsAvailable => available;
        public bool IsRecording => false;
        public IReadOnlyList<string> GetInputDevices(bool rescan = false) => [];
        public void Start(string deviceName) { }
        public IRecordingHandle? Detach() => null;
        public float[] Finish(IRecordingHandle? detached) => [];
        public void Dispose() { }
    }

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

    public SpeechModelOfferTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private DictationService Build(TempSettings settings, bool audio = true)
        => new(settings.Service, new SilentCapture(audio), store: new SpeechModelStore(_directory),
            dispatch: action => action());

    private void PlaceOnDisk(string modelId)
    {
        var model = SpeechModelCatalog.Find(modelId)!;
        using var file = File.Create(Path.Combine(_directory, model.FileName));
        file.SetLength(model.DownloadBytes);
    }

    [Fact]
    public void A_fresh_installation_is_asked()
    {
        using var settings = new TempSettings();
        using var service = Build(settings);

        Assert.True(service.ShouldOfferModelDownload());
    }

    /// <summary>Any model on disk answers the question — including one that is not the selected
    /// one, since the tab adopts a downloaded model when nothing usable is chosen.</summary>
    [Fact]
    public void Nobody_with_a_model_is_asked()
    {
        using var settings = new TempSettings();
        PlaceOnDisk("base");
        using var service = Build(settings);

        Assert.False(service.ShouldOfferModelDownload());
    }

    [Fact]
    public void Asked_once_and_not_again()
    {
        using var settings = new TempSettings();
        using var service = Build(settings);

        service.MarkModelPromptAnswered();

        Assert.False(service.ShouldOfferModelDownload());
        Assert.True(settings.Service.Settings.Speech.ModelPromptAnswered);
    }

    /// <summary>Switching dictation off is an answer about the whole feature; a machine with no audio
    /// backend has nothing to be offered.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Nothing_is_offered_when_there_is_nothing_to_offer(bool enabled, bool audio)
    {
        using var settings = new TempSettings();
        settings.Service.Settings.Speech.Enabled = enabled;
        using var service = Build(settings, audio);

        Assert.False(service.ShouldOfferModelDownload());
    }

    /// <summary>
    /// The offer is editorial, and ordered: rank decides what goes first, and the first entry is what a
    /// user who presses Enter gets.
    /// </summary>
    /// <remarks>
    /// Handy's arrangement — a <c>recommended</c> flag and a <c>recommended_rank</c> in its catalogue,
    /// with no probe of the machine anywhere in the choice. The top of ours stays Parakeet because it is
    /// the only entry that both works out its own language across 25 of them (Polish included) and runs
    /// faster than real time on a CPU.
    /// </remarks>
    [Fact]
    public void The_recommended_models_come_out_in_rank_order_with_parakeet_first()
    {
        var recommended = SpeechModelCatalog.Recommended;

        Assert.NotEmpty(recommended);
        Assert.Equal(SpeechModelCatalog.DefaultModelId, recommended[0].Id);
        Assert.Equal(recommended.OrderBy(m => m.Rank).Select(m => m.Id), recommended.Select(m => m.Id));
        Assert.All(recommended, model => Assert.NotNull(model.Rank));
    }

    /// <summary>Every model is ranked, so the list order is a decision rather than the order somebody
    /// happened to type them in.</summary>
    [Fact]
    public void The_whole_catalogue_is_ranked_and_the_ranks_are_unique()
    {
        var ranks = SpeechModelCatalog.All.Select(m => m.Rank).ToList();

        Assert.All(ranks, rank => Assert.NotNull(rank));
        Assert.Equal(ranks.Count, ranks.Distinct().Count());
        Assert.Equal(SpeechModelCatalog.All.Select(m => m.Id),
            SpeechModelCatalog.All.OrderBy(m => m.Rank).Select(m => m.Id));
    }
}
