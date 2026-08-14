# Third-party notices

This file covers the parts of mTiles that are somebody else's work: code ported into this repository,
libraries it links against, and the models it downloads at run time. Licences were read from the
projects themselves (repository `LICENSE` files, `.nuspec` metadata, Hugging Face model cards) rather
than from memory; where a licence requires the copyright notice to travel with the code, it is
reproduced in full below.

## Code ported into this repository

Dictation was not written from scratch. Two MIT-licensed projects were read closely and their working
parts translated into C#; **the MIT licence requires their copyright and permission notices to be
carried with any substantial portion**, which is what this section is for.

### Handy — <https://github.com/cjpais/Handy>

Copyright (c) 2025 CJ Pais. MIT.

The whole shape of the feature comes from here: the capture pipeline (device-native rate → 16 kHz mono
float), the padding of very short recordings, the push-to-talk state machine with its 30 ms press
debounce and 50 ms release grace, the transcript post-processing (filler words, repeated words,
whisper's non-speech annotations), the model catalogue with its download URLs and SHA-256 digests, the
idle model-unload timeout, and the editorial `recommended` / `recommended_rank` arrangement behind the
first-run model offer. Specific files are cited in the comments where the behaviour is implemented.

### transcribe-rs — <https://github.com/cjpais/transcribe-rs>

Copyright (c) 2025 Ilya Stupakov. MIT.

> Reproduced verbatim from that repository's `LICENSE` (checked at commit `efc6611`, file unmodified in
> its history). It is worth recording why it reads oddly rather than leaving the next person to
> re-litigate it: the repository is CJ Pais's, while its README credits
> [istupakov](https://github.com/istupakov/onnx-asr) separately for the ONNX exports — so the copyright
> line may well be an upstream copy-paste. That is not ours to correct. The MIT licence asks that *the
> above copyright notice* travel with the code, and the notice above is the one the code came with;
> substituting a name no document supports would be an assertion about somebody's copyright rather than
> a reproduction of it.

`ParakeetSpeechEngine` is a port of `src/onnx/parakeet/mod.rs`: the three-graph arrangement (NeMo
preprocessor → encoder → joint decoder), the greedy transducer loop and the details that make it work —
time advancing only on a blank, the decoder state advancing only on a real token, the ten-token cap per
frame, truncating the argmax to the vocabulary so the duration logits are not read as token ids, and the
250 ms of leading silence.

### The MIT licence, as both projects carry it

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction,
> including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
> and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
> subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial
> portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
> LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN
> NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
> WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
> SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Models downloaded at run time

No model ships with the application; each is fetched from its publisher when the user asks for it. The
licence is the publisher's, not ours, and it applies to the file on the user's disk.

| Model | Source | Licence |
|---|---|---|
| Parakeet TDT 0.6B v3 (int8 ONNX) | NVIDIA, via Handy's mirror `blob.handy.computer` | **CC-BY-4.0** — attribution required, which this table is |
| Whisper `ggml-*.bin` (base, small, medium-q5, large-v3-turbo-q5, large-v3-q5) | `ggerganov/whisper.cpp` on Hugging Face, pinned to one revision | MIT |

## Libraries

Linked as NuGet packages; none of their source is copied here.

| Package | Licence |
|---|---|
| Avalonia, Avalonia.Desktop, Themes.Fluent, Fonts.Inter, AvaloniaEdit, AvaloniaEdit.TextMate | MIT |
| CommunityToolkit.Mvvm | MIT |
| Material.Icons.Avalonia, MessageBox.Avalonia | MIT |
| DiffPlex | Apache-2.0 |
| Microsoft.Data.SqlClient, System.Security.Cryptography.ProtectedData | MIT |
| Npgsql | PostgreSQL licence |
| Velopack | MIT |
| Terminal.Avalonia, TodoList.Avalonia, Notepad.Avalonia | ours |
| **Whisper.net**, **Whisper.net.Runtime** (bundles whisper.cpp) | MIT — Copyright (c) 2024 sandrohanea; whisper.cpp is MIT, Copyright (c) 2023-2024 The ggml authors |
| **Microsoft.ML.OnnxRuntime** | MIT — Copyright (c) Microsoft Corporation |
| **PortAudioSharp2** (bundles portaudio) | Apache-2.0; portaudio itself is under the PortAudio licence (MIT-style, with an additional clause asking that changes be contributed back) |

The three in bold carry native binaries into the published application, which is why they are called
out: what ships is not only managed code.
