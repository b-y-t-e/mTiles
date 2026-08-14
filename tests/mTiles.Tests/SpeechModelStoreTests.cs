using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What counts as "the model is on this machine". The two kinds answer it differently, and getting it
/// wrong either offers a model that cannot load or asks for a half-gigabyte download that is already
/// there.
/// </summary>
public class SpeechModelStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

    private readonly SpeechModelStore _store;

    public SpeechModelStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new SpeechModelStore(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static SpeechModel Whisper => SpeechModelCatalog.Find("base")!;
    private static SpeechModel Parakeet => SpeechModelCatalog.Find("parakeet-v3")!;

    [Fact]
    public void A_single_file_model_counts_only_at_its_full_size()
    {
        Assert.False(_store.IsDownloaded(Whisper));

        var path = _store.GetPath(Whisper);
        using (var file = File.Create(path))
            file.SetLength(Whisper.DownloadBytes - 1);   // an interrupted copy
        Assert.False(_store.IsDownloaded(Whisper));

        using (var file = File.OpenWrite(path))
            file.SetLength(Whisper.DownloadBytes);
        Assert.True(_store.IsDownloaded(Whisper));
    }

    /// <summary>
    /// An archive model is a directory, and a directory that exists is not the same as a directory that
    /// can be loaded — an extraction stopped halfway leaves exactly that.
    /// </summary>
    /// <remarks>
    /// Every file the engine opens has to be there, one at a time here because a half-extracted model
    /// is precisely a subset of them. "A vocabulary and any <c>.onnx</c>" used to pass, which meant the
    /// three-graph model counted as downloaded with one graph on disk: the shortcut armed, no warning
    /// anywhere, and the failure arriving after the user had spoken.
    /// </remarks>
    [Fact]
    public void An_archive_model_counts_only_once_all_its_parts_are_there()
    {
        Assert.False(_store.IsDownloaded(Parakeet));

        var directory = _store.GetPath(Parakeet);
        Directory.CreateDirectory(directory);
        Assert.False(_store.IsDownloaded(Parakeet));

        foreach (var missing in ParakeetFiles[..^1])
        {
            File.WriteAllText(Path.Combine(directory, missing), "");
            Assert.False(_store.IsDownloaded(Parakeet));
        }

        File.WriteAllText(Path.Combine(directory, ParakeetFiles[^1]), "");
        Assert.True(_store.IsDownloaded(Parakeet));
    }

    /// <summary>What <c>ParakeetSpeechEngine.LoadAsync</c> opens: three graphs and the vocabulary.</summary>
    private static readonly string[] ParakeetFiles =
        ["vocab.txt", "nemo128.onnx", "encoder-model.int8.onnx", "decoder_joint-model.int8.onnx"];

    /// <summary>
    /// The list this test file spells out is the list the engine actually opens.
    /// </summary>
    /// <remarks>
    /// The store calls a model downloaded on the engine's say-so, and the engine loads from the same
    /// <c>RequiredFiles</c> — so the two cannot drift. What can drift is this test's copy, which would
    /// then quietly stop covering anything; comparing them is what keeps the fixture honest.
    /// </remarks>
    [Fact]
    public void The_engine_opens_exactly_the_files_this_fixture_creates()
    {
        // Against a directory that has them, because the graph names are resolved against what is on
        // disk: the engine prefers the .int8 build and falls back to the plain one, so asking about an
        // empty path would compare the fixture with the fallback names instead.
        var directory = _store.GetPath(Parakeet);
        PlaceParakeetFiles(directory);

        var expected = ParakeetSpeechEngine.RequiredFiles(directory).All
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(expected, ParakeetFiles.OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>Every one of them is required: a missing graph is a model that cannot load, whichever
    /// one it is.</summary>
    [Theory]
    [InlineData("vocab.txt")]
    [InlineData("nemo128.onnx")]
    [InlineData("encoder-model.int8.onnx")]
    [InlineData("decoder_joint-model.int8.onnx")]
    public void One_missing_file_is_enough_to_not_count_as_downloaded(string missing)
    {
        var directory = _store.GetPath(Parakeet);
        PlaceParakeetFiles(directory);
        File.Delete(Path.Combine(directory, missing));

        Assert.False(_store.IsDownloaded(Parakeet));
    }

    private static void PlaceParakeetFiles(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var name in ParakeetFiles)
            File.WriteAllText(Path.Combine(directory, name), "");
    }

    [Fact]
    public void Deleting_an_archive_model_removes_its_directory()
    {
        var directory = _store.GetPath(Parakeet);
        PlaceParakeetFiles(directory);

        _store.Delete(Parakeet);

        Assert.False(Directory.Exists(directory));
        Assert.False(_store.IsDownloaded(Parakeet));
    }

    /// <summary>
    /// Deleting reports on whether the files are <em>gone</em>, not on whether the model would still
    /// load.
    /// </summary>
    /// <remarks>
    /// Those stopped being the same question when a model started counting as downloaded only with all
    /// four of its files. A delete that removed two graphs and then hit a locked third leaves something
    /// unloadable and half a gigabyte on the disk — reporting success there is how a user ends up with a
    /// button that appears to work and a disk that never gets emptier.
    /// </remarks>
    [Fact]
    public void A_delete_that_leaves_files_behind_reports_failure()
    {
        var directory = _store.GetPath(Parakeet);
        PlaceParakeetFiles(directory);

        // A file the delete cannot remove, held open exactly as a loaded model holds its graphs.
        using var held = new FileStream(Path.Combine(directory, "encoder-model.int8.onnx"),
            FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(_store.Delete(Parakeet));
        Assert.True(Directory.Exists(directory));
    }

    /// <summary>The archive is downloaded under a different name than it unpacks into; confusing the
    /// two is how a download lands on top of the directory it is supposed to produce.</summary>
    [Fact]
    public void An_archive_downloads_under_its_own_name()
    {
        Assert.Equal("parakeet-tdt-0.6b-v3-int8.tar.gz", Parakeet.DownloadFileName);
        Assert.Equal("parakeet-tdt-0.6b-v3-int8", Parakeet.FileName);
        Assert.Equal("ggml-base.bin", Whisper.DownloadFileName);
    }

    [Fact]
    public void The_default_model_is_in_the_catalogue()
        => Assert.NotNull(SpeechModelCatalog.Find(SpeechModelCatalog.DefaultModelId));

    [Fact]
    public void Every_model_has_a_distinct_id_and_a_digest()
    {
        Assert.Equal(SpeechModelCatalog.All.Count,
            SpeechModelCatalog.All.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var model in SpeechModelCatalog.All)
        {
            Assert.Equal(64, model.Sha256.Length);
            Assert.True(model.DownloadBytes > 0, $"{model.Id} has no size");
        }
    }
}
