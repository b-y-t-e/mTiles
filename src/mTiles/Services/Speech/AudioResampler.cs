namespace mTiles.Services.Speech;

/// <summary>
/// Turns a stream of mono samples at an arbitrary rate into 16 kHz, the only rate whisper accepts.
/// <para>Microphones are opened at whatever rate they run natively — 44.1 or 48 kHz, usually — because
/// asking the driver for 16 kHz is how a capture fails on hardware that will not resample. Handy makes
/// the same choice and resamples in software (<c>audio_toolkit/resampler.rs</c>, rubato); this is the
/// same idea with a windowed-sinc kernel, which is short enough to write and read here.</para>
/// <para>Stateful across calls: the kernel needs samples either side of the point it interpolates, so
/// the tail of one chunk is the left context of the next. There is deliberately no way to clear that
/// state — one instance belongs to one recording, which is what makes it impossible for the end of an
/// utterance to bleed into the start of the next.</para>
/// </summary>
internal sealed class AudioResampler
{
    /// <summary>Kernel half-width, counted in periods of the lower of the two rates.</summary>
    private const int LobesPerSide = 16;

    /// <summary>
    /// Cutoff as a fraction of the lower sampling rate. Just under the 0.5 that would put it exactly on
    /// Nyquist, so the filter has somewhere to roll off instead of folding the top of the band back.
    /// </summary>
    private const double CutoffFraction = 0.47;

    private readonly double _step;      // input samples consumed per output sample
    private readonly double _cutoff;    // cycles per input sample
    private readonly int _halfWidth;    // kernel half-width in input samples
    private readonly double[] _kernel;  // half-kernel, sampled at KernelResolution points per input sample
    private const int KernelResolution = 32;

    private float[] _tail;
    private double _position;           // where the next output sample falls, in _tail-relative input samples

    public AudioResampler(int inputRate, int outputRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRate);

        InputRate = inputRate;
        OutputRate = outputRate;
        _step = (double)inputRate / outputRate;
        _cutoff = CutoffFraction * Math.Min(1.0, (double)outputRate / inputRate);
        _halfWidth = (int)Math.Ceiling(LobesPerSide / (2 * _cutoff));

        _kernel = BuildKernel(_halfWidth, _cutoff);
        _tail = new float[_halfWidth];
        _position = _halfWidth;
    }

    public int InputRate { get; }
    public int OutputRate { get; }

    /// <summary>True when input and output rates match and <see cref="Process"/> only copies.</summary>
    public bool IsPassthrough => InputRate == OutputRate;

    /// <summary>
    /// Resamples what it can from <paramref name="input"/> and keeps the rest for the next call.
    /// </summary>
    public float[] Process(ReadOnlySpan<float> input)
    {
        if (IsPassthrough)
            return input.ToArray();

        var combined = new float[_tail.Length + input.Length];
        _tail.CopyTo(combined, 0);
        input.CopyTo(combined.AsSpan(_tail.Length));

        // An output sample needs _halfWidth input samples to its right, so stop before the point where
        // the kernel would run off the end of what we have.
        var limit = combined.Length - _halfWidth;
        var count = _position >= limit ? 0 : (int)Math.Ceiling((limit - _position) / _step);
        var output = new float[Math.Max(0, count)];

        for (var i = 0; i < output.Length; i++)
        {
            output[i] = InterpolateAt(combined, _position);
            _position += _step;
        }

        var keepFrom = Math.Max(0, (int)Math.Floor(_position) - _halfWidth);
        _tail = combined[keepFrom..];
        _position -= keepFrom;
        return output;
    }

    /// <summary>
    /// The last few output samples, produced by letting the kernel run out over silence.
    /// <para>Without it the final <see cref="Process"/> call leaves up to a kernel-width of audio
    /// unconverted — a few milliseconds, which is the end of the last word.</para>
    /// </summary>
    public float[] Flush() => IsPassthrough ? [] : Process(new float[_halfWidth * 2]);

    private float InterpolateAt(ReadOnlySpan<float> samples, double position)
    {
        var first = Math.Max(0, (int)Math.Ceiling(position) - _halfWidth);
        var last = Math.Min(samples.Length - 1, (int)Math.Floor(position) + _halfWidth);

        double sum = 0;
        double weightSum = 0;
        for (var i = first; i <= last; i++)
        {
            var weight = KernelAt(Math.Abs(i - position));
            sum += samples[i] * weight;
            weightSum += weight;
        }

        // Dividing by the weights actually used keeps the gain at unity even at the edges, where part of
        // the kernel hangs over the end of the buffer.
        return weightSum > 1e-9 ? (float)(sum / weightSum) : 0f;
    }

    private double KernelAt(double distanceInSamples)
    {
        var index = distanceInSamples * KernelResolution;
        var i = (int)index;
        if (i + 1 >= _kernel.Length)
            return 0;

        var fraction = index - i;
        return _kernel[i] * (1 - fraction) + _kernel[i + 1] * fraction;
    }

    private static double[] BuildKernel(int halfWidth, double cutoff)
    {
        var length = halfWidth * KernelResolution + 2;
        var kernel = new double[length];
        for (var i = 0; i < length; i++)
        {
            var t = (double)i / KernelResolution;   // distance in input samples
            var window = BlackmanHalf(t / halfWidth);
            kernel[i] = window * Sinc(2 * cutoff * t);
        }
        return kernel;
    }

    private static double Sinc(double x) =>
        Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

    /// <summary>Blackman window over [0,1], where 0 is the centre of the kernel and 1 its edge.</summary>
    private static double BlackmanHalf(double x)
    {
        if (x >= 1.0)
            return 0;

        var phase = Math.PI * x;
        return 0.42 + 0.5 * Math.Cos(phase) + 0.08 * Math.Cos(2 * phase);
    }
}
