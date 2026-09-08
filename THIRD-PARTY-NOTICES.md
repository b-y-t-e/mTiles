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

## Fonts shipped inside the application

**JetBrains Mono** — <https://github.com/JetBrains/JetBrainsMono>, version 2.304, Copyright 2020 The
JetBrains Mono Project Authors. **SIL Open Font License 1.1.**

Six faces (Regular, Italic, Medium, SemiBold, Bold, BoldItalic) are compiled into the executable as
Avalonia resources and registered as an embedded font collection, so the interface and the terminal
read the same on a machine that has never installed a font. It is the default for both, and both remain
a fallback list: a font family typed in Settings wins over it.

The OFL requires the licence to travel with the font, and a copy sitting in a git repository does not
reach whoever receives the software — hence the full text here, in the file that ships. Note the
reserved font name: a modified copy may not be called JetBrains Mono.

```
Copyright 2020 The JetBrains Mono Project Authors (https://github.com/JetBrains/JetBrainsMono)

This Font Software is licensed under the SIL Open Font License, Version 1.1.
This license is copied below, and is also available with a FAQ at:
https://scripts.sil.org/OFL


-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded, 
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply
to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software components as
distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting,
or substituting -- in part or in whole -- any of the components of the
Original Version, by changing formats or by porting the Font Software to a
new environment.

"Author" refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed, modify,
redistribute, and sell modified and unmodified copies of the Font
Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components,
in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the corresponding
Copyright Holder. This restriction only applies to the primary font name as
presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any
Modified Version, except to acknowledge the contribution(s) of the
Copyright Holder(s) and the Author(s) or with their explicit written
permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created
using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
OTHER DEALINGS IN THE FONT SOFTWARE.
```

## Libraries

Linked as NuGet packages; none of their source is copied here.

| Package | Licence |
|---|---|
| Avalonia, Avalonia.Desktop, Themes.Fluent, Fonts.Inter, AvaloniaEdit, AvaloniaEdit.TextMate | MIT |
| CommunityToolkit.Mvvm | MIT |
| Material.Icons.Avalonia | MIT |
| DiffPlex | Apache-2.0 |
| Microsoft.Data.SqlClient, System.Security.Cryptography.ProtectedData | MIT |
| Npgsql | PostgreSQL licence |
| Velopack | MIT |
| QRCoder | MIT — Copyright (c) 2013-2018 Raffael Herrmann |
| Microsoft.AspNetCore.App (framework reference — Kestrel, for the phone bridge) | MIT — Copyright (c) .NET Foundation |
| Terminal.Avalonia, TodoList.Avalonia, Notepad.Avalonia | ours |
| **Whisper.net**, **Whisper.net.Runtime** (bundles whisper.cpp) | MIT — Copyright (c) 2024 sandrohanea; whisper.cpp is MIT, Copyright (c) 2023-2024 The ggml authors |
| **Microsoft.ML.OnnxRuntime** | MIT — Copyright (c) Microsoft Corporation |
| **PortAudioSharp2** (bundles portaudio) | Apache-2.0; portaudio itself is under the PortAudio licence (MIT-style, with an additional clause asking that changes be contributed back) |

The three in bold carry native binaries into the published application, which is why they are called
out: what ships is not only managed code.
