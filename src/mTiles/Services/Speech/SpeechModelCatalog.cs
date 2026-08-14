namespace mTiles.Services.Speech;

/// <summary>Which engine can run a model. The file layout follows from it.</summary>
public enum SpeechModelKind
{
    /// <summary>A single whisper.cpp <c>ggml-*.bin</c>, run by Whisper.net.</summary>
    WhisperGgml,

    /// <summary>A directory of ONNX graphs plus a vocabulary, delivered as a <c>.tar.gz</c>.</summary>
    ParakeetOnnx
}

/// <summary>One downloadable speech-to-text model.</summary>
/// <param name="Id">Stable key stored in settings. Never reuse one for a different file.</param>
/// <param name="Name">What the user sees.</param>
/// <param name="FileName">
/// What the model is called on disk: the file itself for whisper, the directory the archive unpacks
/// into for Parakeet.
/// </param>
/// <param name="Url">Where to fetch it.</param>
/// <param name="DownloadBytes">
/// Size of the download, so progress can be drawn before the server answers. For an archive this is the
/// archive, not what it becomes on disk.
/// </param>
/// <param name="Sha256">Hex digest of the download, checked before anything is unpacked or loaded.</param>
/// <param name="Note">One line on what this model is for.</param>
/// <param name="Kind">Which engine runs it.</param>
public sealed record SpeechModel(
    string Id,
    string Name,
    string FileName,
    string Url,
    long DownloadBytes,
    string Sha256,
    string Note,
    SpeechModelKind Kind = SpeechModelKind.WhisperGgml)
{
    public double SizeMegabytes => DownloadBytes / 1024.0 / 1024.0;

    /// <summary>
    /// Editorial order in the list, lower first; null sinks to the end.
    /// </summary>
    /// <remarks>
    /// Handy's arrangement, and its two fields (<c>recommended_rank</c> and <c>recommended</c> in
    /// <c>catalog.json</c>): ranking is about where a model appears, being recommended is about whether
    /// it is offered to somebody who has none. A model can be ranked without being recommended — that is
    /// how the slow-but-accurate ones stay findable without being put in front of a new user.
    /// </remarks>
    public int? Rank { get; init; }

    /// <summary>Whether this is one of the few offered to a user with no model at all.</summary>
    public bool Recommended { get; init; }

    /// <summary>True when the download is an archive that has to be unpacked into a directory.</summary>
    public bool IsArchive => Kind == SpeechModelKind.ParakeetOnnx;

    /// <summary>
    /// Whether choosing a language, translating, and a vocabulary hint mean anything for this model.
    /// </summary>
    /// <remarks>
    /// A property of the model, not of the settings tab that hides three controls because of it.
    /// Parakeet works its language out across the 25 it knows, cannot translate at all, and takes no
    /// initial prompt — leaving any of the three live lets somebody set something quietly ignored.
    /// </remarks>
    public bool HasWhisperOnlyOptions => Kind == SpeechModelKind.WhisperGgml;

    /// <summary>Name of the downloaded file, which for an archive is not the name on disk afterwards.</summary>
    public string DownloadFileName => IsArchive ? FileName + ".tar.gz" : FileName;
}

/// <summary>
/// The models this app offers: Parakeet, which is the default, and the whisper.cpp ggml files.
/// <para>The same set Handy ships (<c>src-tauri/src/managers/model.rs</c>). Parakeet comes from Handy's
/// own mirror, which is the only place the int8 export is published; the whisper files come from
/// upstream <c>ggerganov/whisper.cpp</c> rather than that mirror — the digests were checked against both
/// and match, and going to the source keeps this list honest as the files move.</para>
/// </summary>
public static class SpeechModelCatalog
{
    /// <summary>
    /// The whisper files, pinned to a revision rather than to <c>main</c>.
    /// </summary>
    /// <remarks>
    /// The digest beside each entry already makes a substituted file a refusal rather than a bad model,
    /// so this is not what stops the wrong bytes being loaded. What it stops is the failure the digest
    /// causes: a file republished upstream turns every download here into "does not match the published
    /// checksum" with no way for a user to get the model at all. A revision is a fixed point in git
    /// history that Hugging Face keeps for good. Verified against this revision — all four resolve and
    /// their sizes match the entries below to the byte; the repository's last commit is from 2024.
    /// </remarks>
    private const string BaseUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/";

    /// <summary>
    /// The model this app is set up for, and the one Handy recommends — the only entry its own table
    /// marks <c>is_recommended</c> (<c>managers/model.rs</c>). It is quicker on a CPU than any whisper
    /// of comparable accuracy, and it covers 25 languages including Polish without being told which.
    /// </summary>
    public const string DefaultModelId = "parakeet-v3";

    public static IReadOnlyList<SpeechModel> All { get; } =
    [
        new("parakeet-v3", "Parakeet TDT 0.6B v3", "parakeet-tdt-0.6b-v3-int8",
            "https://blob.handy.computer/parakeet-v3-int8.tar.gz",
            478_517_071, "43d37191602727524a7d8c6da0eef11c4ba24320f5b4730f1a2497befc2efa77",
            "Recommended. Fast on a CPU, works out 25 languages by itself, cannot translate.",
            SpeechModelKind.ParakeetOnnx) { Rank = 1, Recommended = true },

        new("small", "Whisper Small", "ggml-small.bin", BaseUrl + "ggml-small.bin",
            487_601_967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
            "The usual whisper choice: usable in most languages, still quick on a CPU.")
            { Rank = 2, Recommended = true },

        new("base", "Whisper Base", "ggml-base.bin", BaseUrl + "ggml-base.bin",
            147_951_465, "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
            "Smallest download and the quickest. Fine for English, rough on anything inflected.")
            { Rank = 3, Recommended = true },

        new("medium-q5", "Whisper Medium (q5)", "ggml-medium-q5_0.bin", BaseUrl + "ggml-medium-q5_0.bin",
            539_212_467, "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f",
            "Noticeably better than Small at roughly the same size, and slower.") { Rank = 4 },

        new("turbo-q5", "Whisper Large v3 Turbo (q5)", "ggml-large-v3-turbo-q5_0.bin",
            BaseUrl + "ggml-large-v3-turbo-q5_0.bin",
            574_041_195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2",
            "The most accurate here, and slower than real time on a CPU: measured at 0.5×, so a "
            + "ten-second sentence takes twenty to transcribe.") { Rank = 5 },

        new("large-q5", "Whisper Large v3 (q5)", "ggml-large-v3-q5_0.bin", BaseUrl + "ggml-large-v3-q5_0.bin",
            1_081_140_203, "d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1",
            "A gigabyte, and slow enough on a CPU to be painful for dictation.") { Rank = 6 },
    ];

    /// <summary>
    /// What to offer somebody who has no model yet, best first.
    /// </summary>
    /// <remarks>
    /// <para>Editorial, exactly as Handy does it — a flag and a rank in the catalogue, with no
    /// inspection of the machine. Checked against Handy at <c>37a26fd</c>: there is no RAM, CPU or disk
    /// probe anywhere in its model selection, and its default setting is the empty string. The onboarding
    /// screen shows the top two of this list and hides the rest behind "show all".</para>
    /// <para>Nothing here would justify a hardware probe either: the whole catalogue is 148 MB to 1 GB,
    /// and every model runs on a CPU. What separates them is accuracy against speed, which is a
    /// judgement, and languages, which is a fact about the model.</para>
    /// </remarks>
    public static IReadOnlyList<SpeechModel> Recommended { get; } =
        [.. All.Where(m => m.Recommended).OrderBy(m => m.Rank ?? int.MaxValue)];

    public static SpeechModel? Find(string id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Languages offered in Settings. Whisper knows a hundred; these are the ones worth
    /// listing, and <c>auto</c> covers the rest.</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages { get; } =
    [
        ("auto", "Detect automatically"),
        ("pl", "Polski"),
        ("en", "English"),
        ("de", "Deutsch"),
        ("fr", "Français"),
        ("es", "Español"),
        ("it", "Italiano"),
        ("uk", "Українська"),
        ("cs", "Čeština"),
        ("ru", "Русский"),
    ];
}
