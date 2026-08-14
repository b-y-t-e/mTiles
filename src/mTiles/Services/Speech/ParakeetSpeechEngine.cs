using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace mTiles.Services.Speech;

/// <summary>
/// Parakeet TDT — the model Handy recommends — run directly on ONNX Runtime.
/// </summary>
/// <remarks>
/// <para>A port of <c>onnx/parakeet/mod.rs</c> in <a href="https://github.com/cjpais/transcribe-rs">
/// transcribe-rs</a>, which is what Handy uses. There is no .NET wrapper for these models, but there
/// does not need to be: the model is three ONNX graphs — a NeMo preprocessor that turns raw samples
/// into features, an encoder, and a joint decoder — and a greedy loop over them.</para>
/// <para>The loop is a transducer, not a classifier: at each encoder frame the decoder is asked what
/// comes next given the last token emitted and its own recurrent state. A blank means "nothing here,
/// move on"; anything else is emitted and the frame is asked again, up to
/// <see cref="MaxTokensPerFrame"/> times. That is why time only advances on a blank.</para>
/// </remarks>
internal sealed partial class ParakeetSpeechEngine : ISpeechToTextEngine
{
    /// <summary>How many tokens one encoder frame may produce before time is forced forward.</summary>
    private const int MaxTokensPerFrame = 10;

    /// <summary>
    /// Silence prepended to every utterance, as transcribe-rs does. The encoder needs a moment of
    /// context before the first word, and dictation hands it audio that starts on one.
    /// </summary>
    private const int LeadingSilenceMs = 250;

    private readonly Lock _gate = new();
    private Model? _model;

    private sealed record Model(
        InferenceSession Preprocessor,
        InferenceSession Encoder,
        InferenceSession DecoderJoint,
        ParakeetVocabulary Vocabulary,
        int StateLayers,
        int StateWidth,
        string Path) : IDisposable
    {
        public void Dispose()
        {
            Preprocessor.Dispose();
            Encoder.Dispose();
            DecoderJoint.Dispose();
        }
    }

    public bool IsLoaded
    {
        get { lock (_gate) return _model is not null; }
    }

    public Task LoadAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // FileHelper.SamePath, not ==: one answer in this application to "is this the same file?",
            // and the one the service already uses when it decides whether the model being deleted is
            // the loaded one.
            if (_model is { } loaded && FileHelper.SamePath(loaded.Path, modelPath))
                return Task.CompletedTask;
        }

        if (!Directory.Exists(modelPath))
            throw new DirectoryNotFoundException($"The Parakeet model directory is missing: {modelPath}");

        return Task.Run(() =>
        {
            // Disposed once the sessions are built: SessionOptions is a handle to native memory of its
            // own, the sessions copy what they need out of it, and this runs again on every model load.
            using var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount - 1, 1, 8),
            };

            var files = RequiredFiles(modelPath);

            // Sessions are native memory measured in hundreds of megabytes, and they are built one after
            // another: anything that throws part-way through — a truncated graph, a state shape this
            // port cannot use, the machine simply running out of memory — used to strand the ones
            // already built with nothing holding them. The user presses the key again, and it happens
            // again. So the whole construction hands back either a complete model or nothing.
            InferenceSession? preprocessor = null, encoder = null, decoderJoint = null;
            Model model;
            try
            {
                preprocessor = new InferenceSession(files.Preprocessor, options);
                encoder = new InferenceSession(files.Encoder, options);
                decoderJoint = new InferenceSession(files.DecoderJoint, options);
                var vocabulary = ParakeetVocabulary.Load(files.Vocabulary);

                // The decoder's recurrent state is two tensors whose shape the graph itself declares; the
                // batch dimension is dynamic, and this only ever runs one utterance at a time. The other
                // two must be concrete — a -1 there would silently become a zero-sized state and the
                // model would decode noise rather than fail.
                var stateShape = decoderJoint.InputMetadata["input_states_1"].Dimensions;
                if (stateShape.Length < 3 || stateShape[0] <= 0 || stateShape[2] <= 0)
                    throw new InvalidDataException(
                        $"The decoder does not declare a usable state shape ({string.Join('×', stateShape)}).");

                model = new Model(preprocessor, encoder, decoderJoint, vocabulary,
                    stateShape[0], stateShape[2], modelPath);
            }
            catch
            {
                // In reverse, and each guarded: one session refusing to close must not keep the others
                // alive. Model owns them once it exists, so this only ever runs before that.
                foreach (var session in new[] { decoderJoint, encoder, preprocessor })
                {
                    try { session?.Dispose(); }
                    catch (Exception ex) { Trace.TraceWarning("Closing a half-loaded graph: {0}", ex.Message); }
                }

                throw;
            }

            Model? previous;
            lock (_gate)
            {
                previous = _model;
                _model = model;
            }
            previous?.Dispose();
            Trace.WriteLine($"[speech] loaded Parakeet from {modelPath} ({model.Vocabulary.Count} tokens)");
        }, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Only while nothing is transcribing — see <see cref="ISpeechToTextEngine.Unload"/>. Disposing the
    /// ONNX sessions while the greedy loop is still calling into them frees memory the native runtime is
    /// reading, which ends the process instead of throwing.
    /// </remarks>
    public void Unload()
    {
        Model? model;
        lock (_gate)
        {
            model = _model;
            _model = null;
        }

        model?.Dispose();
        if (model is not null)
            Trace.WriteLine("[speech] Parakeet unloaded");
    }

    public Task<string> TranscribeAsync(float[] samples, TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        Model model;
        lock (_gate)
        {
            model = _model ?? throw new InvalidOperationException("No Parakeet model is loaded.");
        }

        // Language and translation are not this model's to offer: v3 works the language out for itself
        // across the 25 it knows, and it cannot translate. Ignored rather than refused, so switching
        // models never invalidates the settings.
        return Task.Run(() => Transcribe(model, samples, cancellationToken), cancellationToken);
    }

    public void Dispose() => Unload();

    private static string Transcribe(Model model, float[] samples, CancellationToken cancellationToken)
    {
        var padded = PrependSilence(samples, LeadingSilenceMs);

        var (features, featureDimensions, featureLengths) = Preprocess(model, padded);
        cancellationToken.ThrowIfCancellationRequested();

        var (encoded, frames, width) = Encode(model, features, featureDimensions, featureLengths);
        cancellationToken.ThrowIfCancellationRequested();

        var tokens = Decode(model, encoded, frames, width, cancellationToken);
        return Detokenize(model.Vocabulary, tokens);
    }

    private static (float[] Features, int[] Dimensions, long Length) Preprocess(Model model, float[] samples)
    {
        var waveforms = new DenseTensor<float>(samples, [1, samples.Length]);
        var lengths = IntegerTensor(model.Preprocessor, "waveforms_lens", [samples.Length], [1]);

        using var outputs = model.Preprocessor.Run(
        [
            NamedOnnxValue.CreateFromTensor("waveforms", waveforms),
            lengths,
        ]);

        var features = outputs.First(o => o.Name == "features").AsTensor<float>();
        var featureLengths = ReadInteger(outputs.First(o => o.Name == "features_lens"));

        return (features.ToArray(), [.. features.Dimensions], featureLengths);
    }

    private static (float[] Encoded, int Frames, int Width) Encode(
        Model model, float[] features, int[] dimensions, long length)
    {
        var signal = new DenseTensor<float>(features, dimensions);
        var lengths = IntegerTensor(model.Encoder, "length", [length], [1]);

        using var outputs = model.Encoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("audio_signal", signal),
            lengths,
        ]);

        // [batch, width, time] out of the encoder; the decoder wants one frame at a time, so it is
        // transposed here once rather than indexed awkwardly in the loop.
        var encoded = outputs.First(o => o.Name == "outputs").AsTensor<float>();
        var frames = (int)ReadInteger(outputs.First(o => o.Name == "encoded_lengths"));

        var width = encoded.Dimensions[1];
        var time = encoded.Dimensions[2];
        frames = Math.Clamp(frames, 0, time);

        var flat = encoded.ToArray();
        var transposed = new float[frames * width];
        for (var t = 0; t < frames; t++)
            for (var d = 0; d < width; d++)
                transposed[t * width + d] = flat[d * time + t];

        return (transposed, frames, width);
    }

    /// <summary>
    /// One question to the joint decoder: given this encoder frame, the last token emitted and the
    /// decoder's state, what comes next and what is the state now?
    /// </summary>
    /// <remarks>The seam that lets the greedy loop be tested without half a gigabyte of ONNX.</remarks>
    internal delegate (float[] Logits, TState State) DecoderStep<TState>(
        int frame, int lastToken, TState state);

    /// <summary>
    /// The greedy transducer loop, with the tensors left outside.
    /// </summary>
    /// <remarks>
    /// Two rules carry it, and both are easy to get backwards. Time advances only on a <b>blank</b> — a
    /// real token is emitted and the <em>same</em> frame is asked again, which is how one frame produces
    /// several tokens. And the decoder state advances only on a real token: taking the state back from a
    /// blank would feed the next question an answer that was never given.
    /// </remarks>
    internal static List<int> Decode<TState>(int frames, int blankIndex, int vocabularySize,
        TState initialState, DecoderStep<TState> step, CancellationToken cancellationToken = default)
    {
        var tokens = new List<int>();
        var state = initialState;
        var lastToken = blankIndex;
        var emittedHere = 0;

        for (var t = 0; t < frames;)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (logits, nextState) = step(t, lastToken, state);
            var token = ArgMax(logits, vocabularySize);

            if (token != blankIndex)
            {
                state = nextState;
                tokens.Add(token);
                lastToken = token;
                emittedHere++;
            }

            // A frame that keeps producing tokens would otherwise never let go of the clock.
            if (token == blankIndex || emittedHere == MaxTokensPerFrame)
            {
                t++;
                emittedHere = 0;
            }
        }

        return tokens;
    }

    private static List<int> Decode(Model model, float[] encoded, int frames, int width,
        CancellationToken cancellationToken)
    {
        var stateShape = new[] { model.StateLayers, 1, model.StateWidth };
        var frame = new float[width];

        return Decode(frames, model.Vocabulary.BlankIndex, model.Vocabulary.Count,
            (State1: new DenseTensor<float>(stateShape), State2: new DenseTensor<float>(stateShape)),
            (t, lastToken, state) =>
            {
                Array.Copy(encoded, t * width, frame, 0, width);
                var result = Step(model, frame, lastToken, state.State1, state.State2);
                return (result.Logits, (result.State1, result.State2));
            },
            cancellationToken);
    }

    private static (float[] Logits, DenseTensor<float> State1, DenseTensor<float> State2) Step(
        Model model, float[] frame, int lastToken, DenseTensor<float> state1, DenseTensor<float> state2)
    {
        var encoderOutputs = new DenseTensor<float>(frame, [1, frame.Length, 1]);

        using var outputs = model.DecoderJoint.Run(
        [
            NamedOnnxValue.CreateFromTensor("encoder_outputs", encoderOutputs),
            IntegerTensor(model.DecoderJoint, "targets", [lastToken], [1, 1]),
            IntegerTensor(model.DecoderJoint, "target_length", [1], [1]),
            NamedOnnxValue.CreateFromTensor("input_states_1", state1),
            NamedOnnxValue.CreateFromTensor("input_states_2", state2),
        ]);

        var logits = outputs.First(o => o.Name == "outputs").AsTensor<float>().ToArray();
        var nextState1 = ToDense(outputs.First(o => o.Name == "output_states_1").AsTensor<float>());
        var nextState2 = ToDense(outputs.First(o => o.Name == "output_states_2").AsTensor<float>());

        return (logits, nextState1, nextState2);
    }

    /// <summary>
    /// The most likely token, over the vocabulary alone.
    /// </summary>
    /// <remarks>
    /// <para>A TDT joint decoder emits the vocabulary logits <b>and then a few duration logits</b> — the
    /// "T" in Token-and-Duration Transducer, which is how it predicts how many frames to skip. They are
    /// part of the same vector and mean something entirely different, so the argmax has to stop at the
    /// end of the vocabulary.</para>
    /// <para>Not theoretical, and not a rounding error either: v3's vocabulary is 8193 tokens and this
    /// graph emits <b>8198</b> logits. A winning duration bucket is read as token id 8193–8197, which
    /// does not exist — the lookup then either throws or silently produces a piece of some other word.
    /// The five extra values are ignored here because this port advances time on the blank instead
    /// (<see cref="Decode"/>), which is what transcribe-rs does; the durations are what a faster
    /// implementation would use to skip frames.</para>
    /// </remarks>
    internal static int ArgMax(ReadOnlySpan<float> logits, int vocabularySize)
    {
        var length = Math.Min(logits.Length, vocabularySize);
        var best = 0;
        var bestValue = float.NegativeInfinity;

        for (var i = 0; i < length; i++)
        {
            if (logits[i] <= bestValue)
                continue;
            bestValue = logits[i];
            best = i;
        }

        return best;
    }

    /// <summary>
    /// Joins the tokens back into text.
    /// <para>SentencePiece pieces carry their own leading space (the vocabulary turned <c>▁</c> back
    /// into one), so joining is nearly enough: what is left is the space before the first word and any
    /// space that did not land at a word boundary. The expression is transcribe-rs's, kept as it is so
    /// the two produce the same string.</para>
    /// </summary>
    internal static string Detokenize(ParakeetVocabulary vocabulary, IEnumerable<int> tokens)
    {
        var joined = new StringBuilder();
        foreach (var token in tokens)
            joined.Append(vocabulary[token]);

        return StraySpace().Replace(joined.ToString(), match => match.Groups[1].Success ? " " : "");
    }

    internal static float[] PrependSilence(float[] samples, int milliseconds)
    {
        var silence = IAudioCapture.SampleRate * milliseconds / 1000;
        var padded = new float[silence + samples.Length];
        samples.CopyTo(padded, silence);
        return padded;
    }

    /// <summary>
    /// Builds an integer input in whichever width the graph declares.
    /// <para>These models are exported with a mixture of int32 and int64 indices, and feeding the wrong
    /// one is not a conversion but a refusal from the runtime.</para>
    /// </summary>
    private static NamedOnnxValue IntegerTensor(InferenceSession session, string name,
        ReadOnlySpan<long> values, ReadOnlySpan<int> dimensions)
    {
        var isLong = session.InputMetadata[name].ElementDataType == TensorElementType.Int64;
        if (isLong)
        {
            var data = new long[values.Length];
            values.CopyTo(data);
            return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(data, dimensions.ToArray()));
        }

        var narrowed = new int[values.Length];
        for (var i = 0; i < values.Length; i++)
            narrowed[i] = checked((int)values[i]);
        return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(narrowed, dimensions.ToArray()));
    }

    private static long ReadInteger(DisposableNamedOnnxValue value) =>
        value.ElementType == TensorElementType.Int64
            ? value.AsTensor<long>().GetValue(0)
            : value.AsTensor<int>().GetValue(0);

    /// <summary>
    /// Copies a result tensor onto the managed heap.
    /// <para>Always copies, never hands the runtime's own tensor onward: an output may be a view over
    /// native memory that the results collection frees when it is disposed, and the decoder state
    /// outlives the call that produced it. Two tensors of a few hundred floats each — the copy costs
    /// nothing and reading freed memory costs the process.</para>
    /// </summary>
    private static DenseTensor<float> ToDense(Tensor<float> tensor) =>
        new(tensor.ToArray(), [.. tensor.Dimensions]);

    private static string ResolveGraph(string directory, string name)
    {
        // The int8 build is what Handy ships and what the size on disk assumes; the unquantised file is
        // accepted as well so a model directory dropped in by hand still loads.
        var quantised = Path.Combine(directory, name + ".int8.onnx");
        return File.Exists(quantised) ? quantised : Path.Combine(directory, name + ".onnx");
    }

    /// <summary>The four files a Parakeet model is, resolved against the directory it was unpacked into.</summary>
    internal readonly record struct ModelFiles(
        string Vocabulary, string Preprocessor, string Encoder, string DecoderJoint)
    {
        public IEnumerable<string> All => [Vocabulary, Preprocessor, Encoder, DecoderJoint];
    }

    /// <summary>
    /// Where <see cref="LoadAsync"/> looks, and the only place these names appear.
    /// </summary>
    /// <remarks>
    /// One list, used both to load the model and to answer whether it is there. They were two copies of
    /// the same four expressions, which is a consistency nobody was checking: the store would call a
    /// model downloaded on the strength of one list while the engine opened files from the other, and
    /// the first time somebody renamed a graph the failure would arrive after the user had spoken.
    /// </remarks>
    internal static ModelFiles RequiredFiles(string directory) => new(
        Vocabulary: Path.Combine(directory, "vocab.txt"),
        Preprocessor: Path.Combine(directory, "nemo128.onnx"),
        Encoder: ResolveGraph(directory, "encoder-model"),
        DecoderJoint: ResolveGraph(directory, "decoder_joint-model"));

    /// <summary>
    /// Whether <paramref name="directory"/> holds everything <see cref="LoadAsync"/> opens.
    /// </summary>
    /// <remarks>
    /// Asked by the model store before it calls a model downloaded, and it lives here because this is
    /// the class that decides what the files are called — including the <c>.int8</c> variants. An
    /// extraction stopped halfway leaves a directory that exists, holds a graph or two, and cannot be
    /// loaded; without this, dictation is armed until the moment somebody speaks into it.
    /// </remarks>
    internal static bool HasRequiredFiles(string directory) =>
        Directory.Exists(directory) && RequiredFiles(directory).All.All(File.Exists);

    [GeneratedRegex(@"\A\s|\s\B|(\s)\b")]
    private static partial Regex StraySpace();
}
