using System.Text.Json.Serialization;

namespace mTiles.Models;

/// <summary>
/// How a recording is started and stopped.
/// </summary>
public enum DictationMode
{
    /// <summary>Hold the shortcut to record; releasing it transcribes.</summary>
    PushToTalk,
    /// <summary>One press starts, the next stops.</summary>
    Toggle
}

/// <summary>
/// Everything the dictation feature reads from settings.
/// <para>Speech recognition runs entirely on this machine: no audio and no transcript leaves it,
/// which is why the model is a file the user downloads once rather than a service they sign in to.</para>
/// </summary>
public sealed class SpeechSettings
{
    /// <summary>
    /// Whether the microphone button and the shortcut exist at all.
    /// <para>On by default. Nothing is opened or loaded until somebody dictates — the switch gates the
    /// UI — so the cost of it being on is a button in a toolbar, and the cost of it being off is a
    /// feature nobody finds.</para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Id from <c>SpeechModelCatalog</c>. Empty means the user has not chosen one yet.</summary>
    public string ModelId { get => _modelId; set => _modelId = value ?? ""; }
    private string _modelId = "";

    /// <summary>
    /// Whether the "you have no model" question has been put to the user.
    /// </summary>
    /// <remarks>
    /// Asked once, on a start where dictation is on and no model is on disk — the state every fresh
    /// installation is in, since nothing ships with the application. Set whichever way they answer,
    /// including "not now": a prompt that returns every launch is a prompt people learn to dismiss
    /// without reading, and Settings → Speech is where the models live either way.
    /// </remarks>
    public bool ModelPromptAnswered { get; set; }

    /// <summary>
    /// ISO-639-1 code, or <c>auto</c> to let the model decide.
    /// </summary>
    /// <remarks>
    /// The one string here that is dereferenced rather than compared: it is split to its base code on
    /// the way into the filler-word cleaner, and handed to whisper as the language to decode in. A
    /// <c>"Language": null</c> in the settings file therefore threw once per sentence, inside the
    /// pipeline's own catch — so the application ran, and every dictation answered "Transcription
    /// failed: Object reference not set" with nothing to connect it to a settings file nobody had looked
    /// at. <c>auto</c> is what "no answer" already means everywhere else here.
    /// </remarks>
    public string Language { get => _language; set => _language = value ?? "auto"; }
    private string _language = "auto";

    /// <summary>Ask the model for English regardless of what was spoken.</summary>
    public bool TranslateToEnglish { get; set; }

    /// <summary>
    /// Input device name as reported by PortAudio. Empty — the default — means whichever device the
    /// system considers current, so switching to a headset works without anyone opening Settings.
    /// </summary>
    public string InputDeviceName { get => _inputDeviceName; set => _inputDeviceName = value ?? ""; }
    private string _inputDeviceName = "";

    /// <summary>
    /// The push-to-talk shortcut, in the form <c>Alt+Space</c>. Parsed by <c>HotkeyGesture</c>.
    /// </summary>
    /// <remarks>
    /// <b>Empty means no shortcut</b>, and that is the only switch it needs: a gesture that cannot be
    /// parsed is one the application cannot listen for, which is exactly what "off" means. There used to
    /// be a separate <c>HotkeyEnabled</c> toggle beside it, which could only ever say the same thing
    /// twice — and say it twice differently, since a shortcut set but switched off looks configured.
    /// <para>A null becomes empty rather than the default: a file that says nothing here is not asking
    /// for a key, and quietly claiming Alt+Space on its behalf is the one outcome that costs the user
    /// something.</para>
    /// </remarks>
    public string Hotkey { get => _hotkey; set => _hotkey = value ?? ""; }
    private string _hotkey = "Alt+Space";

    /// <summary>
    /// The old separate on/off switch for the shortcut.
    /// </summary>
    /// <remarks>
    /// Read once, from a file written by an older version: somebody who turned the shortcut off said
    /// something, and an update is not the moment to hand them back a key that swallows Alt+Space.
    /// <c>SettingsService.MigrateLegacySettings</c> turns "off" into an empty <see cref="Hotkey"/> and
    /// drops this. Nullable so "never said" and "said no" stay different.
    /// </remarks>
    [JsonPropertyName("HotkeyEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyHotkeyEnabled { get; set; }
    public DictationMode Mode { get; set; } = DictationMode.PushToTalk;

    /// <summary>
    /// Press Enter for the user once the transcript is in. Off by default: a misheard word in a
    /// terminal is a typo to correct, and an executed command otherwise.
    /// </summary>
    public bool AutoSubmitEnter { get; set; }

    /// <summary>Append a space, so dictating twice in a row does not run the words together.</summary>
    public bool AppendTrailingSpace { get; set; } = true;

    /// <summary>
    /// Names and jargon the model keeps mangling. Handed to whisper as its initial prompt, which is
    /// what Handy does — it biases the decoder rather than editing the text afterwards.
    /// </summary>
    /// <remarks>
    /// The setter refuses null, and every collection and section in the settings does the same. A
    /// property initialiser is not a guarantee: <c>"CustomWords": null</c> in the file overwrites it
    /// during deserialisation, which is not an error, and the first read then throws while the main
    /// window is being built — the application does not start and says nothing about why. Guarding the
    /// property rather than patching after loading means it holds for whoever sets it, at whatever
    /// depth, including a section nobody thought to normalise.
    /// </remarks>
    public List<string> CustomWords
    {
        get => _customWords;
        set => _customWords = value ?? [];
    }
    private List<string> _customWords = [];

    /// <summary>Strip "um", "eh" and their neighbours from the transcript.</summary>
    public bool RemoveFillerWords { get; set; } = true;

    /// <summary>
    /// Minutes of not dictating after which the model is dropped from memory. Zero keeps it loaded for
    /// as long as the application runs.
    /// </summary>
    /// <remarks>
    /// Half an hour, because the two costs are not symmetrical. Holding the model costs memory that is
    /// only a problem if something else needs it; dropping it costs a second or two at the start of the
    /// next dictation, and that lands on the user while they are waiting. Somebody dictating on and off
    /// through a working session should not pay that repeatedly — five minutes was short enough to
    /// expire between one prompt and the next.
    /// </remarks>
    public int ModelUnloadMinutes { get; set; } = 30;

    /// <summary>
    /// The largest idle period this setting can name, in minutes.
    /// </summary>
    /// <remarks>
    /// One number, used by the control in Settings and by the timer that reads it, because two would
    /// eventually disagree — and the way they disagree is a settings file naming a value the UI cannot
    /// show and the timer cannot take. <see cref="System.Threading.Timer"/> refuses a due time past
    /// about 49 days, so an unclamped 100000 in a hand-edited file is not a long wait: it is an
    /// <c>ArgumentOutOfRangeException</c> on a thread-pool thread at the end of the first dictation.
    /// Six hours covers a working day with a lunch break in the middle of it, and zero still means never.
    /// </remarks>
    public const int MaxUnloadMinutes = 360;

    /// <summary>The same number, typed for the control that binds to it — <c>NumericUpDown.Maximum</c>
    /// is a <see cref="decimal"/>, and XAML will not convert. Derived rather than written twice, which
    /// is the whole point of there being one.</summary>
    public static decimal MaxUnloadMinutesForUi => MaxUnloadMinutes;
}
