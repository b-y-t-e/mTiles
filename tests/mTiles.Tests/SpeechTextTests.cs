using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What happens to a transcript between the model and the shell. Both halves matter for different
/// reasons: the post-processor for how the text reads, and the sanitiser because bytes that reach a
/// pseudo-console are not text at all — 0x03 is an interrupt and a newline is a submitted command.
/// </summary>
public class SpeechTextTests
{
    [Theory]
    [InlineData("um so we uh need this", "en", "so we need this")]
    [InlineData("hmm let me think", "en", "let me think")]
    [InlineData("yyy no to zaczynamy", "pl", "no to zaczynamy")]
    // "ah" is an English filler only. A German transcript keeps it.
    [InlineData("ah so ist das", "de", "ah so ist das")]
    public void Filler_words_go_only_where_they_are_fillers(string input, string language, string expected)
        => Assert.Equal(expected, TranscriptPostProcessor.Clean(input, language, removeFillerWords: true));

    /// <summary>
    /// With no language, only what is not a word in any of them goes.
    /// </summary>
    /// <remarks>
    /// <para>"auto" is the default and, with Parakeet, the only thing this is ever told: the model works
    /// the language out and never says which it chose. Applying every list on a miss — which is what
    /// this used to do — is a guess, and the guess eats real words: <c>AAA</c> and <c>EE</c> are
    /// hesitations in Polish and things people dictate in English, so the shipped configuration was
    /// stripping them from English prompts.</para>
    /// <para>Failing closed instead is Handy's rule (<c>UNIVERSAL_FILLER_WORDS</c> against
    /// <c>gated_filler_words_for_language</c>, "unknown output languages fail closed"). It costs a "yyy"
    /// left standing in a Polish prompt, which costs nothing.</para>
    /// </remarks>
    [Theory]
    [InlineData("uh so we need this", "so we need this")]      // universal: not a word anywhere
    [InlineData("hmm no to zaczynamy", "no to zaczynamy")]
    [InlineData("yyy no to zaczynamy", "yyy no to zaczynamy")] // gated: Polish, and nobody said Polish
    [InlineData("um so we need this", "um so we need this")]   // "um" is Portuguese for "a"
    [InlineData("dodaj obsługę AAA", "dodaj obsługę AAA")]     // the one that made this a bug
    [InlineData("   ", "")]                                    // and nothing in is nothing out
    public void With_an_unknown_language_only_the_universal_fillers_go(string input, string expected)
        => Assert.Equal(expected, TranscriptPostProcessor.Clean(input, "auto", removeFillerWords: true));

    [Fact]
    public void Filler_removal_can_be_switched_off()
        => Assert.Equal("um so we uh need this",
            TranscriptPostProcessor.Clean("um so we uh need this", "en", removeFillerWords: false));

    [Theory]
    [InlineData("wh wh wh wh what", "wh what")]   // the run becomes one, it does not vanish
    [InlineData("the the the file", "the file")]
    // Two in a row is ordinary speech ("that that clause"), and stays.
    [InlineData("that that clause", "that that clause")]
    // Whatever else it does, the result comes back squeezed and trimmed.
    [InlineData("  one   two  ", "one two")]
    public void A_run_of_three_or_more_identical_words_collapses(string input, string expected)
        => Assert.Equal(expected, TranscriptPostProcessor.Clean(input, "en", removeFillerWords: false));

    /// <summary>
    /// Measured on this machine: two seconds of silence through <c>ggml-base</c> comes back as
    /// "[muzyka]". Handy never sees this because Silero VAD drops the silence first; without a VAD the
    /// annotation would be typed into the user's shell.
    /// </summary>
    [Theory]
    [InlineData("[muzyka]", "")]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData("*cisza*", "")]
    [InlineData("(upbeat music)", "")]
    [InlineData("[muzyka] uruchom testy", "uruchom testy")]
    [InlineData("*upbeat music* uruchom testy", "uruchom testy")]
    public void Whisper_s_non_speech_annotations_are_dropped(string input, string expected)
        => Assert.Equal(expected, TranscriptPostProcessor.Clean(input, "pl", removeFillerWords: true));

    /// <summary>
    /// Asterisks that are arithmetic or a glob keep what is between them.
    /// </summary>
    /// <remarks>
    /// The annotation pattern used to be <c>\*[^*]*\*</c> — anything at all between two asterisks — so
    /// dictating a multiplication or a wildcard into a shell silently lost its middle. Both are ordinary
    /// things to say into a terminal, and this is a feature for saying things into terminals.
    /// </remarks>
    [Theory]
    [InlineData("2 * 3 * 4")]
    [InlineData("rm build/*.tmp and dist/*.log")]
    [InlineData("git add * then commit")]
    public void Asterisks_in_ordinary_text_are_left_alone(string input)
        => Assert.Equal(input, TranscriptPostProcessor.Clean(input, "en", removeFillerWords: false));

    [Fact]
    public void A_dictated_aside_in_round_brackets_survives()
        => Assert.Equal("run the tests (the slow ones)",
            TranscriptPostProcessor.Clean("run the tests (the slow ones)", "en", false));

    [Theory]
    [InlineData("run the tests\r", "run the tests")]
    [InlineData("first\nsecond", "first second")]
    [InlineData("stop\u0003that", "stop that")]   // 0x03 interrupts the whole pseudo-console
    [InlineData("tab\there", "tab here")]
    // And ordinary text comes back exactly as it was said.
    [InlineData("napisz test dla ChainPolicy", "napisz test dla ChainPolicy")]
    public void Nothing_a_console_would_act_on_survives_sanitising(string input, string expected)
        => Assert.Equal(expected, DictationTextSink.Sanitize(input));

    /// <summary>
    /// What gets typed, as opposed to where. The routing needs Avalonia's focus and a live terminal, but
    /// this part is what decides whether a shell runs a command, and it is a string operation.
    /// </summary>
    [Fact]
    public void The_composed_payload_carries_the_trailing_space_only_when_asked()
    {
        var settings = new mTiles.Models.SpeechSettings { AppendTrailingSpace = true };
        Assert.Equal("run the tests ", DictationTextSink.Compose("run the tests", settings));

        settings.AppendTrailingSpace = false;
        Assert.Equal("run the tests", DictationTextSink.Compose("run the tests", settings));
    }

    /// <summary>
    /// Nothing worth typing means nothing typed — and the caller reports that rather than sending an
    /// empty line, which in a shell is a command.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void A_transcript_of_nothing_composes_to_nothing(string text)
        => Assert.Equal("", DictationTextSink.Compose(text,
            new mTiles.Models.SpeechSettings { AppendTrailingSpace = true }));
}
