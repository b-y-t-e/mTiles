using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The step between a microphone and whisper: 44.1 or 48 kHz from the device, 16 kHz for the model.
/// Nothing else in the pipeline can tell whether this is right — a bad resampler produces audio that
/// transcribes into plausible nonsense.
/// </summary>
public class AudioResamplerTests
{
    private static float[] Sine(int rate, double frequency, double seconds)
    {
        var samples = new float[(int)(rate * seconds)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * frequency * i / rate);
        return samples;
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples)
            sum += s * s;
        return Math.Sqrt(sum / Math.Max(1, samples.Length));
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    [InlineData(32_000)]
    public void One_second_in_gives_about_one_second_out(int deviceRate)
    {
        var resampler = new AudioResampler(deviceRate, 16_000);
        var output = resampler.Process(Sine(deviceRate, 440, 1.0));
        var total = output.Length + resampler.Flush().Length;

        // Within a couple of milliseconds: the kernel's own width is the slack.
        Assert.InRange(total, 16_000 - 64, 16_000 + 64);
    }

    [Fact]
    public void At_a_matching_rate_it_passes_the_samples_through_untouched()
    {
        var resampler = new AudioResampler(16_000, 16_000);
        Assert.True(resampler.IsPassthrough);

        var input = Sine(16_000, 300, 0.1);
        Assert.Equal(input, resampler.Process(input));
        Assert.Empty(resampler.Flush());
    }

    /// <summary>
    /// A tone well inside the band survives with its level intact — the check that the filter passes
    /// speech rather than muffling it.
    /// </summary>
    [Fact]
    public void A_speech_frequency_keeps_its_level()
    {
        var resampler = new AudioResampler(48_000, 16_000);
        var input = Sine(48_000, 440, 0.5);
        var output = resampler.Process(input);

        // Ignore the first and last few samples, where the kernel is still filling.
        var body = output.AsSpan(200, output.Length - 400);
        Assert.InRange(Rms(body), Rms(input) * 0.9, Rms(input) * 1.1);
    }

    /// <summary>
    /// A tone above the output Nyquist must be filtered out, not folded down into the middle of the
    /// speech band. Plain decimation would turn this 9 kHz tone into a 7 kHz whistle.
    /// </summary>
    [Fact]
    public void A_tone_above_the_new_nyquist_is_removed_rather_than_folded_down()
    {
        var resampler = new AudioResampler(48_000, 16_000);
        var output = resampler.Process(Sine(48_000, 9_000, 0.5));

        var body = output.AsSpan(200, output.Length - 400);
        Assert.True(Rms(body) < 0.05, $"aliased energy leaked through: RMS {Rms(body):0.000}");
    }

    /// <summary>
    /// Chunked delivery is what the audio callback actually does, and the result has to be the same
    /// audio — the kernel needs the tail of the previous chunk as its left context.
    /// </summary>
    [Fact]
    public void Feeding_it_in_chunks_gives_the_same_audio_as_feeding_it_whole()
    {
        var input = Sine(48_000, 440, 0.3);

        var whole = new AudioResampler(48_000, 16_000);
        var expected = whole.Process(input).Concat(whole.Flush()).ToArray();

        var chunked = new AudioResampler(48_000, 16_000);
        var actual = new List<float>();
        for (var offset = 0; offset < input.Length; offset += 1024)
            actual.AddRange(chunked.Process(input.AsSpan(offset, Math.Min(1024, input.Length - offset))));
        actual.AddRange(chunked.Flush());

        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) < 1e-5,
                $"sample {i} differs: {expected[i]} vs {actual[i]}");
    }

    /// <summary>
    /// Down to mono, by averaging — what every microphone that reports more than one channel goes
    /// through before anything else touches it. Getting it wrong halves the level or drops a channel,
    /// and the only symptom downstream is that recognition is quietly worse.
    /// </summary>
    /// <remarks>
    /// <c>PortAudioCapture</c> rather than the resampler, and it lives here because the two are one step
    /// of the same conversion: whatever the device hands over becomes 16 kHz mono or the model sees
    /// nothing it can use.
    /// </remarks>
    [Theory]
    // Interleaved: L R L R L R
    [InlineData(2, new[] { 1.0f, 0.0f, 0.5f, 0.5f, -1.0f, 1.0f }, new[] { 0.5f, 0.5f, 0.0f })]
    [InlineData(1, new[] { 0.1f, -0.2f, 0.3f }, new[] { 0.1f, -0.2f, 0.3f })]   // passed through untouched
    [InlineData(4, new[] { 1.0f, 0.0f, 1.0f, 0.0f }, new[] { 0.5f })]           // and more than two, as well
    public void Channels_are_averaged_down_to_one(int channels, float[] interleaved, float[] expected)
        => Assert.Equal(expected, PortAudioCapture.Downmix(interleaved, channels));

    /// <summary>
    /// A fresh instance starts from silence. There is no reset, on purpose: one resampler belongs to one
    /// recording, which is what stops the end of an utterance bleeding into the start of the next.
    /// </summary>
    [Fact]
    public void A_new_resampler_carries_nothing_over()
    {
        var used = new AudioResampler(48_000, 16_000);
        var spoken = used.Process(Sine(48_000, 440, 0.2));

        // The fixture has to be a fixture: if the first resampler produced nothing, the assertion below
        // would hold against an instance that had never seen a sample and the test would say nothing at
        // all about state being shared.
        Assert.Contains(spoken, sample => Math.Abs(sample) > 0.1);

        var fresh = new AudioResampler(48_000, 16_000);
        Assert.All(fresh.Process(new float[4_800]), sample => Assert.True(Math.Abs(sample) < 1e-6));

        // And the one that *was* used still carries its own tail: filter state exists, it is per
        // instance, and silence into a primed filter is audibly not silence coming out.
        Assert.Contains(used.Process(new float[4_800]), sample => Math.Abs(sample) > 1e-6);
    }

    /// <summary>
    /// Upsampling: the branch the anti-alias cutoff has to leave alone.
    /// </summary>
    /// <remarks>
    /// A Bluetooth headset in HFP hands over 8 or 16 kHz, so this is not a corner: it is what happens
    /// the moment somebody dictates through their headphones. The cutoff is the <em>lower</em> of the two
    /// Nyquist limits, so going up it is the input's own — filtering to the output's would throw away
    /// nothing here, but the arithmetic that picks it is the same expression that protects the far more
    /// common way down, and only this direction proves the <c>min</c> is a <c>min</c>.
    /// </remarks>
    [Fact]
    public void Upsampling_keeps_the_signal_and_produces_the_longer_buffer()
    {
        var resampler = new AudioResampler(8_000, 16_000);

        // 500 Hz: well inside 8 kHz audio, so nothing about this tone should be attenuated on the way up.
        var output = resampler.Process(Sine(8_000, 500, 0.5));

        Assert.InRange(output.Length, 7_000, 9_000);          // half a second at 16 kHz, give or take latency
        Assert.Contains(output, sample => Math.Abs(sample) > 0.5);
    }

    [Fact]
    public void An_empty_buffer_produces_nothing_rather_than_throwing()
        => Assert.Empty(new AudioResampler(48_000, 16_000).Process([]));
}
