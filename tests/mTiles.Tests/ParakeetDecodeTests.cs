using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The parts of the Parakeet engine that are arithmetic and text rather than tensors: the vocabulary,
/// picking a token out of the logits, and putting the pieces back together into a sentence. The ONNX
/// graphs themselves cannot be unit-tested without half a gigabyte of model, but these can — and these
/// are where a port goes quietly wrong.
/// </summary>
public class ParakeetDecodeTests
{
    private static ParakeetVocabulary Vocabulary(params string[] lines) =>
        ParakeetVocabulary.Parse(lines);

    [Fact]
    public void The_vocabulary_is_indexed_by_id_and_knows_its_blank()
    {
        var vocab = Vocabulary("▁hello 0", "▁world 1", "<blk> 2", "ing 3");

        Assert.Equal(4, vocab.Count);
        Assert.Equal(2, vocab.BlankIndex);
        Assert.Equal(" hello", vocab[0]);      // U+2581 becomes the space it stands for
        Assert.Equal("ing", vocab[3]);
        Assert.Equal("", vocab[99]);           // out of range is empty, never an exception
    }

    [Fact]
    public void A_vocabulary_without_a_blank_is_refused()
        => Assert.Throws<InvalidDataException>(() => Vocabulary("▁a 0", "▁b 1"));

    /// <summary>A line whose id is negative drops, like any other line without a usable id.</summary>
    /// <remarks>
    /// It parses as a number and then indexes nothing, so letting it through means an
    /// <see cref="IndexOutOfRangeException"/> out of the array the vocabulary is built into — from a
    /// class whose stated rule is that a line without an id simply falls out. The file is digest-checked
    /// and cannot contain one; the rule should still say what it does.
    /// </remarks>
    [Fact]
    public void A_negative_id_drops_like_any_other_unusable_line()
    {
        var vocab = Vocabulary("<blk> 0", "▁bad -5", "▁x 2");

        Assert.Equal(3, vocab.Count);
        Assert.Equal(" x", vocab[2]);
    }

    [Fact]
    public void Gaps_in_the_ids_stay_empty_rather_than_shifting_the_rest()
    {
        var vocab = Vocabulary("<blk> 0", "▁x 5");

        Assert.Equal(6, vocab.Count);
        Assert.Equal("", vocab[3]);
        Assert.Equal(" x", vocab[5]);
    }

    /// <summary>
    /// A TDT model's output vector is the vocabulary followed by duration buckets. Taking the maximum
    /// over the whole thing lets a duration win and be read as a token id — silently, as some other
    /// word.
    /// </summary>
    [Fact]
    public void The_argmax_ignores_everything_past_the_vocabulary()
    {
        float[] logits = [0.1f, 0.9f, 0.2f, /* durations: */ 5.0f, 9.9f];

        Assert.Equal(1, ParakeetSpeechEngine.ArgMax(logits, vocabularySize: 3));
    }

    [Fact]
    public void A_shorter_logit_vector_than_the_vocabulary_is_still_handled()
        => Assert.Equal(2, ParakeetSpeechEngine.ArgMax([0f, 1f, 3f], vocabularySize: 100));

    /// <summary>
    /// The greedy loop, driven by a scripted decoder instead of half a gigabyte of ONNX.
    /// <para>Each entry is what the joint decoder answers on the n-th question: the token it wants and
    /// the state it hands back.</para>
    /// </summary>
    private static List<int> Decode(int frames, int blank, params (int Token, string State)[] answers)
    {
        var asked = new List<(int Frame, int LastToken, string State)>();
        return Decode(frames, blank, answers, asked);
    }

    private static List<int> Decode(int frames, int blank, (int Token, string State)[] answers,
        List<(int Frame, int LastToken, string State)> asked)
    {
        var step = 0;
        return ParakeetSpeechEngine.Decode<string>(frames, blank, vocabularySize: 100, "initial",
            (frame, lastToken, state) =>
            {
                asked.Add((frame, lastToken, state));
                var answer = answers[Math.Min(step++, answers.Length - 1)];

                var logits = new float[100];
                logits[answer.Token] = 1f;
                return (logits, answer.State);
            });
    }

    [Fact]
    public void A_blank_moves_time_on_and_emits_nothing()
    {
        const int blank = 99;
        var asked = new List<(int Frame, int LastToken, string State)>();

        var tokens = Decode(3, blank, [(blank, "ignored")], asked);

        Assert.Empty(tokens);
        Assert.Equal([0, 1, 2], asked.Select(a => a.Frame));
        // The state a blank offered is never taken up: every question still starts from "initial".
        Assert.All(asked, a => Assert.Equal("initial", a.State));
    }

    /// <summary>
    /// A real token holds the clock still and advances the decoder — that is how one encoder frame
    /// produces a whole word. Advancing time here instead would drop the rest of it.
    /// </summary>
    [Fact]
    public void A_token_advances_the_decoder_but_not_the_clock()
    {
        const int blank = 99;
        var asked = new List<(int Frame, int LastToken, string State)>();

        var tokens = Decode(2, blank, [(7, "after-7"), (8, "after-8"), (blank, "x")], asked);

        Assert.Equal([7, 8], tokens);

        // Frame 0 asked three times: two tokens, then the blank that let time move.
        Assert.Equal([0, 0, 0, 1], asked.Select(a => a.Frame));
        Assert.Equal([99, 7, 8, 8], asked.Select(a => a.LastToken));
        Assert.Equal(["initial", "after-7", "after-8", "after-8"], asked.Select(a => a.State));
    }

    /// <summary>
    /// A decoder that keeps answering with tokens would otherwise never let go of the clock, and the
    /// loop would not end. Ten per frame is transcribe-rs's cap.
    /// </summary>
    [Fact]
    public void A_frame_that_never_yields_a_blank_is_forced_to_give_up_the_clock()
    {
        var tokens = Decode(2, blank: 99, [(5, "s")]);

        Assert.Equal(20, tokens.Count);          // ten per frame, two frames
        Assert.All(tokens, token => Assert.Equal(5, token));
    }

    [Fact]
    public void No_frames_means_no_tokens()
        => Assert.Empty(Decode(0, blank: 99, [(1, "s")]));

    [Fact]
    public void Tokens_are_joined_into_a_sentence_without_a_leading_space()
    {
        var vocab = Vocabulary("▁urucho 0", "m 1", "▁testy 2", "<blk> 3");

        Assert.Equal("uruchom testy", ParakeetSpeechEngine.Detokenize(vocab, [0, 1, 2]));
    }

    [Fact]
    public void No_tokens_means_no_text()
        => Assert.Equal("", ParakeetSpeechEngine.Detokenize(Vocabulary("<blk> 0"), []));

    /// <summary>
    /// The encoder wants a moment before the first word; dictation hands it audio that starts on one.
    /// 250 ms at 16 kHz is 4000 samples, and the speech must still all be there afterwards.
    /// </summary>
    [Fact]
    public void Leading_silence_is_prepended_without_losing_any_audio()
    {
        float[] speech = [0.5f, -0.5f, 0.25f];

        var padded = ParakeetSpeechEngine.PrependSilence(speech, 250);

        Assert.Equal(4000 + speech.Length, padded.Length);
        Assert.All(padded[..4000], sample => Assert.Equal(0f, sample));
        Assert.Equal(speech, padded[4000..]);
    }
}
