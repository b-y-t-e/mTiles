# Dictation

The voice-dictation feature in full: the pipeline, the decisions behind it, and the bugs that shaped
them. Split out of `CLAUDE.md`, which loads into every session and where this one feature had grown
longer than the description of the rest of the application put together.

Speak into a tile instead of typing: a microphone button in the terminal tile's header, and a
push-to-talk shortcut (**Alt+Space** by default). Recognition runs **entirely on this machine** — no
audio and no transcript leaves it. Ported from [cjpais/Handy](https://github.com/cjpais/Handy), which
is where the pipeline, the thresholds and the model list come from.

**Pipeline:** microphone → 16 kHz mono `float` → speech engine → cleaned text → `TerminalControl.SendText`.

- **Capture** — `PortAudioCapture`, on `PortAudioSharp2` because it is the one wrapper that ships
  prebuilt portaudio for **both** RIDs this app targets. The device is opened at its **own** sample rate
  (asking a driver for 16 kHz is how a capture fails on hardware that will not resample) and the callback
  does nothing but hand its buffer to a consumer thread — it runs on the driver's realtime thread, where
  filtering audio means dropping it. That hand-off sits inside a **bare `catch`**: the callback is
  reached through a native function pointer, so any exception escaping it ends the process rather than
  the recording — and a consumer that stopped early has *disposed* the queue, which is an
  `ObjectDisposedException` and not the `InvalidOperationException` of adding to a completed one.
  Channels are averaged to mono.
  Everything one recording needs — channel count, resampler, buffer, queue — lives in a private
  `Recording` object rather than in fields — the stream included, so a detached recording carries
  everything needed to close it — and that is load-bearing: `Stop` only *waits* two seconds for
  the consumer before giving the microphone up, so a consumer that runs over has to keep writing
  somewhere harmless. Held in fields, it wrote into whatever recording had started since, appending the
  tail of one utterance to the front of the next.
- **`AudioResampler`** — windowed-sinc, stateful across chunks and deliberately with **no way to clear
  that state**: one instance belongs to one recording, which is what makes it impossible for the end of
  an utterance to bleed into the start of the next. It is the piece nothing downstream can check — a bad
  resampler produces audio that transcribes into plausible nonsense — which is why the tests assert on
  aliasing (a 9 kHz tone must be filtered out, not folded down to 7 kHz) rather than on length alone,
  and why it was checked against real speech: 44.1 kHz in, transcribed word for word.
- **A recording is capped at five minutes.** Push-to-talk ends when the key comes up; toggle mode ends
  only when somebody presses again, and a recording nobody stops grows at 64 KB a second towards a
  transcription of an hour of audio. The cap **stops and transcribes** rather than discarding — the
  words already spoken are still worth having.
- **Ending a recording is two steps, and only the slow one is asynchronous.** `IAudioCapture.Detach`
  takes the live recording out under the capture's own lock — instant — and `Finish` does the native
  closing and the drain. `Stop` and `Cancel` detach synchronously and hand the rest to the thread pool,
  so the microphone is free the moment either returns. While the whole of it was on the pool the capture
  still believed it was recording for up to two seconds, and `Start` in that window opened **nothing**:
  it refuses a second stream rather than replacing one, so the service reported Recording, not a sample
  arrived, and the user was told their microphone had produced no audio. Cancel-then-start-again is
  exactly the gesture of thinking better of a sentence, so that window was reachable by trying. The
  capture now **throws** rather than silently returning when a recording is still attached — and a start
  that fails partway (a device busy, or gone since it was listed) leaves nothing behind: the stream is
  disposed and the fields go back to null, because a half-built capture used to believe it was recording
  for ever with no recording to detach, and one busy microphone killed dictation until a restart. And
  `DictationService.Dispose` detaches too: closing the stream waits up to two seconds for the consumer,
  and on the way out of the application nobody is waiting for those samples.
- **Nothing on the UI thread ends a dictation.** Closing the capture stream is 50–150 ms of native work
  with a two-second worst case behind it, so both `Stop` and `Cancel` hand it to the thread pool; the
  transcription pipeline runs with `ConfigureAwait(false)` throughout. That last part is what makes
  `UnloadModel` safe to call at all — while the pipeline needed the dispatcher to finish, blocking the
  UI thread on the engine semaphore stopped the very continuation that would have released it.
- **An empty transcript says nothing.** The engine ran and had nothing to report — a pause, a cough, a
  false start — and a modal demanding a click in exchange for that is an interruption carrying no
  information. The tile's border already showed the recording and the transcription; nothing appearing
  where the words should have gone says the rest. **An empty capture is different and does speak up**:
  no samples at all means the microphone delivered nothing, which is a fault rather than a result. That
  one is judged by the **clock**, from when the microphone was opened rather than by the sample count —
  those are the same thing only while audio is arriving, and a microphone yielding nothing is exactly
  when they part — so a stray tap on the shortcut stays silent and a held key does not.
- **Known limitation: the microphone opens when the key goes down.** Measured here: 208–301 ms for
  `Start`, and 394 ms the first time, when portaudio is initialised. So roughly a quarter-second of the
  first word is lost, whichever thread does it — start speaking a beat after pressing. The real fix is
  Handy's `always_on_microphone`: hold the stream open while dictation is enabled and pay nothing at
  press time. Not done here because an always-live microphone in a terminal app is the user's decision
  to make (privacy indicator, battery), not a default to slip in.
- **No VAD.** Handy uses Silero to trim silence; push-to-talk already bounds the recording. The cost is
  paid in `TranscriptPostProcessor` instead — measured here, two seconds of silence through `ggml-base`
  transcribes as **"[muzyka]"**, so whisper's non-speech annotations (`[...]`, `*...*`, and a wholly
  parenthesised transcript) are stripped and an empty result is never delivered. Filler words are one
  compiled alternation per language, cached: a pattern per word meant sixteen freshly-built regexes per
  transcript against a fifteen-entry cache. The cleaner is told the language **the model actually worked
  in**, as evidence rather than as a preference: whisper is told the setting and decodes in it, so for
  whisper the setting is the answer; Parakeet detects for itself, never says which language it chose,
  and has the language control hidden from it in Settings — so whatever is stored there says nothing
  about this transcript, and it is told `auto`.

  The list is then **two-tiered, and an unknown language fails closed** — Handy's rule
  (`UNIVERSAL_FILLER_WORDS` against `gated_filler_words_for_language`), and it arrived here the hard
  way. The universal tier is only what is not a word in *any* of these languages (`uh`, `uhm`, `hmm`,
  `mmm`, `хм`…); everything that means something somewhere is gated behind knowing the language — `um`
  is Portuguese for "a", `ah` and `eh` are ordinary interjections, and our own Polish list holds `aaa`
  and `ee`, which are **AAA** and **EE** to anyone dictating in English. Applying every list on a lookup
  miss, which is what this did, was therefore not a safe default but the least safe one: with Parakeet
  the language is *always* unknown, so the shipped configuration was stripping acronyms out of English
  prompts. Failing closed costs a `yyy` left standing in a Polish one, which costs nothing. (Handy's
  English tier also has `ha`; left out here, because `ha` is a hectare in most of the languages offered.)
- **Two engines behind `ISpeechToTextEngine`**, chosen by `SpeechModel.Kind` in **one place**
  (`SpeechEngines`: which engine runs a kind, and what that kind looks like on disk). Both questions used
  to be answered where they were asked — the service tested the kind to build an engine, the store asked
  `ParakeetSpeechEngine` directly whether an unpacked directory was complete — which left a class about
  HTTP and file names naming an engine, and a third engine needing edits in places nobody would look.

  **`SpeechEngineHost`** owns the loaded model: which engine holds it, the path it was loaded from, the
  idle timer that drops it, and the semaphore that makes the three safe. It builds an engine on first use
  and replaces the one held when the user switches kind, so two half-gigabyte models are never resident at
  once. It was lifted out of `DictationService` **whole** — that is the condition on a split like this
  one, because the semaphore, the engine, the loaded path and the timer are a single invariant and
  leaving any of them behind would have left the rule spanning two classes. What did *not* move is the
  decision the host is not entitled to make: whether the model is ever dropped at all (zero minutes means
  never) is a setting, so the service reads it and either asks for a timer or cancels one. Loading and
  transcribing are one call on the host rather than two, because the gap between them is exactly where
  the idle timer used to be able to unload the model that was about to be used.
  - **`ParakeetSpeechEngine`** — the default, and the model Handy recommends. Straight onto
    `Microsoft.ML.OnnxRuntime`, because no .NET wrapper exists and none is needed: the model is three
    ONNX graphs (NeMo preprocessor → encoder → joint decoder) and a greedy transducer loop, ported from
    `onnx/parakeet/mod.rs` in [transcribe-rs](https://github.com/cjpais/transcribe-rs). Time advances
    only on a **blank**: a non-blank token is emitted, the decoder state moves on, and the same frame is
    asked again (up to 10 times). Two details are easy to get wrong and are pinned by tests —
    the argmax must ignore the **duration logits** that follow the vocabulary, and the decoder state
    must **not** advance on a blank. Both are genuinely covered: the greedy loop takes its decoder as a
    delegate, so a test drives it with scripted answers and asserts what moves — the clock on a blank,
    the state on a token, and neither the wrong way round. The argmax one is not theoretical either:
    v3's vocabulary is 8193 tokens and the joint decoder emits **8198** logits, so five duration buckets
    sit past the end and a winning one is read as a token id that does not exist. 250 ms of silence is prepended, as transcribe-rs does.
    Parakeet v3 detects its own language across 25 including Polish, cannot translate, and takes no
    initial prompt; the Settings tab hides all three controls — language, translation and vocabulary —
    rather than leaving settings live that it would quietly ignore.

    **Measured for Polish** on this machine, ten dictated sentences of the kind this application is for
    (CPU only, whisper told `pl`, Parakeet left to work it out):

    | model | WER | speed |
    |---|---|---|
    | Parakeet TDT 0.6B v3 | **8.0%** | **20.5× realtime** |
    | Whisper Large v3 Turbo (q5) | 7.0% | 0.5× realtime |
    | Whisper Small | 14.0% | 2.1× |
    | Whisper Base | 25.0% | 7.2× |

    Turbo is one point more accurate and **forty times slower** — below real time, so a ten-second
    sentence takes twenty to transcribe. And the gap is not what it looks like: Parakeet's eight errors
    are `Chain Policy` for "ChainPolicy", `Linuxa` for "Linuksa", `Encoder-decoder` for "enkoder
    dekoder", `5` for "pięciu", and one missing diacritic. Two genuinely misheard words in a hundred.
    456 MB to download, **640 MB on disk** once unpacked. (Medium q5 and Large q5 are in the catalogue
    but have not been measured — neither is downloaded here.)
  - **`WhisperSpeechEngine`** — Whisper.net (whisper.cpp), for the ggml models. `CustomWords` go in as
    whisper's **initial prompt**, biasing the decoder rather than editing the text afterwards, and
    language and translation are honoured here — which is the whole of what Parakeet hides.

  Whichever is loaded is kept between utterances and dropped after `ModelUnloadMinutes` idle (default
  **30**, zero meaning never): it is hundreds of megabytes of resident memory, but the two costs are not
  symmetrical — holding it is only a problem if something else wants the memory, while dropping it costs
  a second or two at the start of the next dictation, and that one lands on somebody who is waiting.
  Five minutes expired between one prompt and the next. Loading, transcribing and unloading are serialised by
  a semaphore inside `SpeechEngineHost` — **both engines are native**, so unloading one mid-inference
  frees memory the native code is still reading, and that is an access violation that takes the process
  with it rather than an exception anybody can catch. Asking the state machine in the timer is not
  enough on its own: a dictation can start between the check and the call, which is why the check
  happens *under* the semaphore. The timer takes it with a zero timeout and gives up if it is held,
  because a transcription in flight means the model is wanted.
- **Padding** — a recording shorter than a second is zero-padded to 1.25 s (Handy's numbers): the models
  produce nonsense from a fragment, and a tap on the key is a tap, not an error.
- **`DictationTextSink`** — the focused `TextBox`/AvaloniaEdit first, **if it is still on screen**: the
  focused element is captured when the key goes down and used seconds later, by which time its dialog may
  have closed, and writing into a detached control while reporting success is the one answer the caller
  must not be given (which covers a note, a commit
  message and the Goal tile's prompt box without knowing any of them exist), otherwise the tile's
  terminal. The transcript is **sanitised**: control characters and newlines become spaces, because
  `SendText` writes bytes straight to the child — 0x03 is an interrupt for the whole pseudo-console and
  a newline is a submitted command line. Enter is appended only when `AutoSubmitEnter` is on, and it is
  **off by default**: a misheard word is a typo to correct, and an executed command otherwise.
  **Busy is said out loud**: a press or a click while another tile is recording, or while the previous
  sentence is still being transcribed, is refused *and* reported — the tile's button used to answer a
  click with nothing at all, which is indistinguishable from a broken one.
  Delivery **returns a bool and the service acts on it**: the tile may have closed or its shell exited
  while the user was speaking, and a dropped paragraph must not be reported as silence. The failure
  message quotes the first words back, so the transcript is at least readable before it is gone. A
  callback that *throws* counts as false rather than escaping — it runs on the dispatcher, so an
  exception there ends the application over one undelivered sentence, and to the user both mean the same
  thing. **Nothing left after sanitising is not a delivery failure**, though: false means "there was
  nowhere to put the text", which sends the user looking at a tile that closed, when in fact the
  destination was fine and the transcript was a cough. The service already treats an empty transcript as
  a silence worth a trace line and nothing more, and the sanitiser can produce that same emptiness one
  step later — a result of nothing but control characters passes `IsNullOrWhiteSpace` and comes out of
  `Compose` empty — so it gets the same answer rather than the opposite one. **A selection is replaced, not written around**: dictation stands in for typing, and typing
  overwrites what is selected. Getting that wrong is silent — the user selects a sentence to say again
  and ends up with both.

**Setting dictation up is a three-step wizard** (`SpeechSetupWizard` + `SpeechSetupViewModel`), and
there is exactly one of it: the first run shows it, and Settings → Speech → **Set up dictation…** is how
somebody starts over. The steps are a dependency chain, not a preference — **model** (nothing can be
tried without one, and the step will not let you past until it is on disk), **microphone** (the only
setting whose failure is silent: a wrong device yields no audio rather than an error), **test** (say a
sentence and see it come back, which is the only step that proves the other two). The rules live in
`SpeechSetupFlow`, pure and away from the window, so "what comes next" and "may they leave this step"
are a table test rather than a click-through. Closing it mid-sentence cancels the recording — the
service outlives the window, and an abandoned recording holds the microphone to its five-minute cap.
While it is open **it does the talking**: it subscribes to `DictationService.Error` and shows failures
in its own body, and `MainWindow` stands down whenever it owns a window, because a message box owned by
the main window opens *behind* a modal and cannot be read or dismissed.

It replaced a single screen that asked which model to download and nothing else; that one could not
answer the only question that matters, which is whether dictation works on this machine.

The first run is gated by `DictationService.ShouldOfferModelDownload`: dictation on, audio backend
present, nothing downloaded, never asked. `Speech.ModelPromptAnswered` is set **before** the wizard
opens, because closing it with the title bar is an answer too and a prompt that returns every launch is
one people dismiss without reading. It is offered after the window is up, never during construction — a
modal in front of a window that has not drawn is a dialog over nothing — and the gate itself runs on the
thread pool, since asking whether audio exists is what initialises portaudio.

What the first step offers is **editorial**, taken from Handy (`37a26fd`): a `Recommended` flag and a
`Rank` in the catalogue, best first, everything else in Settings. Checked rather than assumed — **there is no RAM,
CPU or disk probe anywhere in Handy's model choice**, and its own default model setting is the empty
string, with onboarding doing the picking. Nothing here would justify a hardware probe either: the whole
catalogue is 148 MB to 1 GB and every model runs on a CPU; what separates them is accuracy against speed
and which languages they know. Our first pick stays **Parakeet v3** even though Handy's has moved on to
`parakeet-unified-en-0.6b` — that one is English-only, and every model in Handy's current catalogue is
GGUF for `transcribe-cpp`, which this app cannot load at all (see *Why not Nemotron Streaming 3.5*).

**Opening the wizard changes nothing.** The list is the three recommended models of six, so somebody who
chose Whisper Large in Settings has a working configuration it cannot represent — and the opening
selection went through the property that *writes* the choice, so the fallback to "the first row"
replaced a model on disk with one that was not, and dictation stopped working because a window had been
opened. The chosen model now appears as a row of its own whatever its rank, the preselection is written
to the field rather than the property, and it is adopted only when there is nothing to lose: no model
configured, or one whose file is missing. What satisfies the first step is what dictation would actually
load (`DictationService.SelectedModel` on disk), not what happens to be in the list — otherwise the same
user is held on step one by a model that works.

Downloading a model **adopts it** when nothing usable is selected. Otherwise the natural first run —
open Settings, download a model that is not the catalogue default — ends with the shortcut inert and the
microphone button reporting that no model has been downloaded, next to the one on disk. The message also
names the model it is missing rather than claiming there are none. **Deleting** runs the same rule the
other way: removing the model in use falls back to any other model on disk, since the alternative is the
same dead end reached from the opposite direction. Neither ever overrides a choice that works.

The microphone list has the matching trap and does not fall into it: rebuilding the bound collection
makes the combo box push a **null** selection through, which is a write to settings — so the chosen
device is read before the list is cleared, *and* an empty selection is refused in the setter. Either
alone would do; both mean the order stops being load-bearing. Without them, "Rescan devices" silently
erased the user's microphone. Refreshes are also **serialised**: the tab starts one when it opens and
the Rescan button can start another while the first is still inside its await — which on the first call
is portaudio initialising, the better part of a second — and interleaved they clear and refill the same
bound collection from two continuations.

**"Rescan" means rescan, and that took a change in the backend.** portaudio enumerates the machine's
devices once, inside `Pa_Initialize`, and everything afterwards reads that snapshot — so a headset
plugged in while the application was running could never appear, however many times the button was
pressed, and the only honest options were to make it work or to take it away. `GetInputDevices(rescan:
true)` terminates the library and initialises it again, which is portaudio's own way of asking a second
time. Two guards on it: only when **no stream is open anywhere in the process**, because `Pa_Terminate`
closes every open stream, including from under a callback on the driver's realtime thread, which is a
crash and not an exception; and if re-initialising throws, the library is left marked unavailable so the
next call starts from scratch rather than using a terminated one.

The first guard lives in `PortAudioRuntime` — the count of open streams with it — and not in the capture
that holds one. The resource being protected is process-wide, so "is *this* capture recording" is a
different question from the one that has to be answered, and while the check sat in the caller the next
caller could forget it. It is a **count**, not a flag: a stop hands the stream to the thread pool to be
closed and clears the field naming it, so the next recording legitimately overlaps the last one's
teardown by 50–150 ms — up to two seconds when the consumer has to drain — and a flag would have been
cleared by whichever finished first. Opening a stream and counting it happen under the same lock, so
there is no instant in which a live stream is invisible to the guard; `PortAudioRuntime.Generation`
exists only so a test can assert that a rescan was actually refused, which is otherwise visible only to
portaudio itself. The button and the wizard's microphone step ask for the
rescan; filling the tab in does not, because tearing the audio library down to answer a question nobody
asked would be absurd.

Deleting a model asks first — and **refuses when there is nothing to ask with**, saying so, at every
link in the chain: the row, the tab that wires the row, and the view's own `ConfirmAction` when there is
no window to show a dialog in. That is the opposite of the `ConfirmAction` convention elsewhere in this
application, where an unwired dialog lets the action through; for a click that discards hundreds of
megabytes and hours of somebody's connection, the default belongs the other way round. All three had to
change together — a `?? Task.FromResult(true)` in the middle made the row's own refusal unreachable. It discards a download measured in hundreds of
megabytes and, on a slow connection, in hours — then **unloads it**, because a loaded model has its files open and the delete
otherwise fails with nothing on screen to say so, and finally reports if the files are still there.

**Models** (`SpeechModelCatalog`) — Parakeet from Handy's own mirror, plus the whisper ggml files from
upstream `ggerganov/whisper.cpp` (digests checked against both and matching), **pinned to a revision
rather than to `main`**. The digest already makes a substituted file a refusal rather than a bad model;
what the pin prevents is the refusal itself — a file republished upstream would turn every download into
"does not match the published checksum" with no way to get the model at all. Verified against the pinned
revision: all four resolve and their sizes match the catalogue to the byte. `SpeechModelStore`
downloads to `<AppData>/MTerminal/models/<file>.partial`, resumes with `Range`, verifies the digest and
only then moves the file into place — with files this size, "download it again" is not a recovery plan.
An archive model (`Kind == ParakeetOnnx`) is then unpacked through a `.unpacking` staging directory;
`IsDownloaded` asks `ParakeetSpeechEngine.HasRequiredFiles` — **every** file the engine opens (the
vocabulary and all three graphs, `.int8` variants included), from the same `RequiredFiles` the loader
reads, so the store's idea of "downloaded" and the engine's idea of "loadable" cannot drift apart. "A vocabulary and any `.onnx`" passed with one graph on disk, so a
half-extracted model counted as downloaded: shortcut armed, no warning anywhere, and the failure
arriving after the user had spoken. `Delete` therefore reports on whether the files are **gone**, not on
`!IsDownloaded` — those stopped being the same question, and a delete that removed two graphs and hit a
locked third would otherwise report success with half a gigabyte still there. The archive is deleted **only after** the model is in place,
and a complete archive still sitting there is unpacked rather than fetched again — an hour of somebody's
bandwidth must not be thrown away because the unpacking needs fixing.

**The published size is the only bound on the write loop**, and it is enforced at both ends: a
`Content-Length` that disagrees with the catalogue is refused before a byte is written, a server that
keeps sending past the expected size is stopped, and a body that ends **short** fails with the partial
file left in place. That last one matters most — a dropped connection reaches the read loop as a clean
end of stream, so without the byte count it went to the digest, failed it, and deleted the partial:
an interruption at 90% threw away 90% of a download, which is what resuming exists to prevent. Without it a broken or hostile server writes until the disk is full, and
the digest that would have caught the wrong file is never reached.

**One download of a model at a time, whoever asked.** There are two lists of models over one store — the
Speech tab and the setup wizard — each with its own row and its own "already downloading" flag, so
nothing between them stops both starting the same file; the second writer then cannot open the
`.partial` at all (`FileShare.None`) and the user gets a Windows sharing message naming a path they have
never seen. A semaphore per file makes the second caller wait and then find the model downloaded, which
is what it asked for. **Deleting takes the same gate** (five seconds, then it gives up and reports
failure rather than waiting on an hour-long download): deleting into a running download removes the
archive that download is about to move into place, or fails on the `.partial` it is writing and reports
success on a file that is still growing. A download also **unloads that model first**, the same way deleting does and for
the same reason: adopting it replaces the very files the engine holds open. Scoped to the one model, so
fetching a second never drops the one in use.

A `.partial` that is **already the full size** is hashed and adopted rather than truncated: it is a
download interrupted between its last byte and the digest check, and `FileMode.Create` on the next
attempt threw away half a gigabyte that was about to be accepted. Whole but wrong is deleted — there is
nothing to resume onto.

A 206 is checked against what was asked for (`Content-Range`), not taken on trust: a proxy or mirror
answering from a different offset would otherwise have its body appended to the partial file, making a
file of exactly the right length out of the wrong bytes — which the digest catches only after the whole
download has finished.

Two answers to a resume the server will not honour, and they are different. **Ignored** (a plain 200
where a 206 was asked for) restarts the file rather than appending a whole body to a partial one.
**Refused** (416 — the partial is at or past the end of what the server has, because a published size
moved or the file is a leftover) deletes the partial and downloads from zero: letting the 416 out left
that file in place, so every later attempt sent the same impossible range and the model was
**permanently un-downloadable** short of deleting a file by hand.

The client's own timeout is infinite and has to be — it covers the whole request, and half a gigabyte
over a slow line legitimately takes an hour — so the watchdog is **per read**: sixty seconds without a
single byte fails the download rather than leaving it at 43% for ever behind a progress bar that still
looks alive. A slow connection is never punished, only a silent one, and what is already on disk stays
for the next attempt to resume from.

Every await in the store is `ConfigureAwait(false)` and the unpacking runs through `Task.Run`, because
the command that starts a download is invoked on the UI thread and would otherwise own every
continuation. Measured on the released Parakeet archive: **0.7 s** to hash 455 MB and **2.4 s** to
expand it to 639 MB — three seconds of frozen window on a fast disk, more on a slow one, during the one
operation the user is watching. The same reasoning puts deleting a model (640 MB across several files)
and enumerating microphones (the first call loads native portaudio) on the thread pool.

**`TarGzExtractor` exists because `System.Formats.Tar` cannot read these archives.** They are built on
macOS, so every file carries a PAX extended header of `LIBARCHIVE.xattr.com.apple.*` records, and the
framework rejects the block outright — *"The extended header contains invalid records"* — on the first
entry. Measured on the released file, and reproduced in the tests, which assert the framework still
fails so the reason this reader exists cannot quietly evaporate. Ours skips extended headers except a
`path=` record, drops AppleDouble `._` stubs, and refuses any entry that resolves outside the
destination — comparing paths the way the filesystem underneath does, because ignoring case on Linux
would accept `/tmp/Model/x` as being inside `/tmp/model` and then write it to a genuinely different
directory. An extended or long-name header claiming more than a megabyte is skipped rather than read:
its length is an octal field the archive controls, eleven digits of it, and the entry is allocated whole.

**The default is Parakeet TDT 0.6B v3** — the only entry Handy's own table marks `is_recommended`
(`managers/model.rs`). Faster on a CPU than any whisper of comparable accuracy, 25 languages including
Polish worked out by itself. Nothing ships with the application: 456 MB is a download the user starts
from Settings → Speech, and until it finishes dictation says so rather than failing under their finger.

**Why not Nemotron Streaming 3.5**, which Handy ranks second and this app cannot use: it ships as GGUF
only — `handy-computer` publishes 68 models and not one of them is ONNX — and Handy runs it through
`transcribe-cpp`, a native C++ library behind a Rust crate with no path in from .NET. Community ONNX
conversions exist but none matches the layout this engine reads: they split the decoder and joint into
separate graphs and, decisively, omit the NeMo preprocessor graph, which is the only reason no
mel-spectrogram frontend had to be written here. Getting a 128-band log-mel subtly wrong does not fail,
it quietly degrades recognition. The gain would have been small in any case — by Handy's own scoring,
accuracy 82 against 80 and speed 84 against 85 — and its streaming headline buys nothing under
push-to-talk, where the finished utterance is transcribed after the key comes up. Revisit only if
`istupakov` (whose export convention this follows) publishes one.

**Dictation is on by default**, with the system default microphone. Neither costs anything until
somebody dictates — no device is opened and no model loaded — so the switch only gates the UI.

But on by default and no model on disk is the state **every** installation starts in, and the shortcut
answers for that: it claims the key only when `DictationService.IsReady` — switched on, audio present,
model downloaded. Otherwise Alt+Space belongs to the shell. That check runs **last**, after the key has
already matched, because it stats the model file and this handler sees every keystroke the window does;
the gesture itself is parsed once per setting rather than once per key, for the same reason. It used to match anyway, swallow the key
before the terminal saw it, and raise a dialog per auto-repeat while the key was held. The microphone
button still explains itself on a click, because a click asked; and `MainWindow` shows one dictation
dialog at a time regardless.

**The shortcut is not a global hotkey.** It is a handler on this window, so it works while mTiles has
the keyboard and not otherwise — speaking into a browser or an editor does nothing. Deliberate for now:
a system-wide hook is a per-platform affair (a Windows low-level keyboard hook, an X11/Wayland grab) and
an application that quietly watches every keystroke on the machine is a different proposition from one
that listens to its own window. Handy is the global kind, which is why its shortcut defaults differ.

**Shortcut** — `DictationHotkeys.Attach(window)`, tunnelling with `handledEventsToo`, next to
`TerminalClipboardCoordinator.Attach`. Both are required: terminals consume keys, and `KeyBindings` fire
only on key *down* while holding a key is the whole gesture. `DictationHotkeyMachine` holds the rules
(30 ms press debounce, **50 ms release grace**) and takes its clock and its scheduler as parameters, so
the awkward cases are testable without a dispatcher. The grace period is load-bearing: a held key
produces auto-repeat that on some systems arrives as release/press bursts, and without it a held key
stops and restarts the recording several times a second (Handy's issue #1539). Escape cancels, but only while **recording**: during transcription there is nothing on screen to
abandon and the key belongs to whatever the user has moved on to. It also stands down while the
**settings dialog** is open: that dialog is an overlay in this window rather than a window of its own, so
this handler tunnels past it, cancels the recording and swallows the key — leaving the dialog open with
no way to close it. Dictating into a settings box is a feature, so the two are on screen together by
design, and of the two meanings Escape has there only one has no alternative; the recording can still be
ended with the shortcut. Releasing **either** the key or any of
its modifiers ends the gesture — the two orders are equally likely — and a release is marked handled
only in push-to-talk, the one mode that acts on it. Swallowing them in toggle mode ate every space and
alt release on its way to the terminal, which sees key-up events too whenever the child asked for
win32 input.

**Auto-repeat is not a second press**, and the machine knows it because it tracks whether the key is
physically down — which is why `DictationHotkeys` forwards releases in **both** modes even though only
push-to-talk acts on one. Without that, holding the shortcut in toggle mode ended its own recording at
the first repeat, about half a second in, silently, with the user still speaking; push-to-talk never
noticed because its repeat branch did nothing anyway. A press more than a second after the last repeat
counts regardless, so a release lost to a focus change cannot leave the shortcut permanently dead, and a
**debounced** press deliberately does not mark the key held — it is one press arriving twice, and
marking it would make the next real press look like repeat.

**An empty shortcut is how the shortcut is switched off**, and there is no second control saying the
same thing: `HotkeyEnabled` was a toggle beside a text box that could only ever agree with it, and
disagree confusingly — a shortcut set but switched off looks configured. The box has a Clear button and
takes Backspace; `SettingsService.MigrateLegacySettings` turns an old "off" into an empty shortcut, so
nobody who had said no gets Alt+Space swallowed again by an update.

**The language starts at the system's**, seeded once on a genuinely new settings file
(`SeedSpeechLanguage`) and only if the catalogue offers that code. It is a first guess, not a preference
to re-apply: overwriting a deliberate `auto` on every start would be the same bug as the microphone list
erasing the chosen device. It matters only for whisper, which is told the language and does better for
it — its own detection reads the first seconds of audio and a dictated sentence is often shorter than
that — while Parakeet ignores the setting entirely.

**A recording the shortcut started can always be stopped by it**, whatever changed in between: matching
the key and being *allowed* to claim it are separate questions (`TryGetGesture` parses, `IsHotkeyLive`
gates), and a live recording answers the second one itself. Switching dictation off, disabling the
shortcut or focusing the rebinding box mid-recording used to leave a toggle-mode recording nobody could
end — running to the five-minute cap, with the tile's microphone button hidden by the very setting that
caused it.

The machine also listens to `DictationService.StateChanged` and resets when a recording ends by some
other route — the tile's microphone button, an error. Without that, in toggle mode the next press is
spent switching off something that already stopped, and only the press after it records.

**Attribution is a licence obligation here, not a courtesy.** Handy and transcribe-rs are both MIT, and
substantial parts of this feature are ports of their code — `THIRD-PARTY-NOTICES.md` carries their
copyright and permission notices, along with the licences of the native-carrying packages and of the
models themselves (**Parakeet is CC-BY-4.0**, so the attribution has to exist somewhere the user can
reach; the whisper ggml files are MIT). It **ships with the application** (`Content` in the csproj,
copied to output *and* publish) and Settings → General → About opens it — a notice in a git repository
reaches nobody who installed a build, which is the only audience either licence is about. The release
workflow fails if it is missing from the publish output, next to the check for the native binaries.

**`DictationHotkeys.IsRebinding` is why the shortcut can be changed at all.** The handler tunnels from
the window, so it sees Alt+Space *before* the shortcut-capture box in Settings does: it would match the
binding being replaced, start recording, and swallow the keystroke, leaving the transcript in a terminal
behind the modal. The box raises the flag while it holds focus, and the handler additionally checks that
focus really is still in a text box, so a dialog dismissed without a `LostFocus` cannot leave dictation
switched off with nothing on screen to say so. It is one explicit exemption rather than a blanket
"ignore text boxes", because dictating into a text box is a feature.

The flag is also **put down when the settings dialog is hidden** (`SettingsView.OnPropertyChanged` on
`IsVisible`, plus on detach): closing it while the box still holds the keyboard need not raise
`LostFocus`, and a flag left up is a shortcut that never records again. By visibility, because the
dialog is an overlay that is hidden rather than removed from the tree.

Inside that box, **`Tab` and `Escape` are let through untouched** along with bare modifiers, and the
event is marked handled only where a key is actually recorded. Marking it first, before the early
exits, was a trap with no way out: Tab could not move focus, and Escape both bound the cancel key to
starting a recording and stopped the dialog closing.

**Alt+Space is safe here, and that was measured rather than assumed.** It is Windows' own window-menu
chord, so the obvious worry is that pressing it opens that menu instead. It does not: sending Alt+Space
to this window produces no `#32768` menu window either with the shortcut enabled *or* disabled, while
the same keystroke to Notepad produces one — Avalonia's window simply does not act on the chord. Nothing
in the app suppresses it, so nothing can regress. Ctrl+Space was rejected for taking PSReadLine's
MenuComplete; the shortcut is configurable and can be switched off either way.

**The tile says which of the two things is happening.** A border overlay (`DictationBorder` in
`LeafTileView`, classes `recording` / `processing`) breathes slowly in `DangerText` while the microphone
is open and pulses three times faster in the accent while the transcript is being worked out — told
apart by rhythm as well as by colour. Only **opacity** is animated: a border whose thickness changed
would move the content under the cursor, and a colour interpolation could not come from
`DynamicResource`, which is how both brushes follow the terminal-derived theme. It is an overlay, so it
costs no layout.

**One marker at a time.** The active strip goes dark while its tile is being dictated into
(`LeafTileNodeViewModel.ShowsActiveStrip`, which is `IsActive && !IsDictating`; the view subscribes to
**that** property, not to the two it is computed from — listening to the inputs repainted the strip
while one of them was still stale, which left it lit through the whole recording and then dark for good
once the last change was to a property nothing was watching): the border frames the
same tile, so it already answers "which one", and the dictated tile is nearly always the active one, so
both would light at the same edge. The strip returns when the transcript lands, which is also when it
starts meaning something again. The **toolbar** keeps its lift throughout — it is the quiet half of the
active signal, it is not at the tile's edge, and flickering it as the microphone opens would change the
background under the buttons the user is about to click.

Because the strip stands down, the border spans the whole tile and closes around it. Leaving its two
pixels out — which is where this started, with the border overlaying the second grid row only — gives a
frame with a notch in its top edge, and against a dark strip that reads as a gap rather than an inset.
**Square corners** for the same class of reason: a tile is a rectangle in a grid of splitters, and a
rounded frame pulls away from its corners leaving a notch at each one. The classes and the visibility are covered by tests; the pulse
itself is not — the application's styles are not applied under the headless session and the animation
clock does not advance there, both measured, so that part is left as something a person looks at.

**The tile the shortcut aims at is the last active one, or nothing.** `WorkspaceViewModel.ActiveTile`
does not fall back to "whatever tile is first" — `FocusActiveTile` does, because focus has to land
somewhere, and dictation must not, because the first leaf is a tile the user is not looking at and with
`AutoSubmitEnter` on, delivering there does not paste a sentence, it **runs a command** in a terminal
nobody chose. Null instead: the sink tries the focused text control, and failing that the transcript is
reported undeliverable and quoted back.

The service is one per application (one microphone, one destination — a second tile cannot take it over
mid-sentence), built in `App.axaml.cs` and handed to every tile through `WorkspaceViewModel`. Because it
outlives every tile, **`LeafTileNodeViewModel` is `IDisposable`** and `WorkspaceViewModel.DisposeTree`
disposes the tile as well as its content: a tile still subscribed to `StateChanged` is a tile that can
never be collected, along with its terminal. Closing one tile always went through `CloseAsync` and was
fine; closing a workspace was not, and leaked every tile in it.

The same asymmetry ran the other way on creation. `Split` copied the callbacks it knew about by hand, so
a new tile got whatever somebody had remembered to add to that list — and it only appeared to work
because splitting the **root** rebuilds the whole tree through `ConfigureRoot`. Splitting anything else
took the other branch: from the second split onwards a tile had no dictation service (no microphone
button, ever) and never registered as the active tile the shortcut aims at. `ConfigureNewLeaf` now hands
each new tile back to `WorkspaceViewModel.ConfigureLeafCallbacks`, so there is one place that decides
what a tile needs instead of a list to keep in step — **including `LayoutChanged`**, which was the last
one `Split` still copied by hand and so the last way for the two to drift apart. A tile whose own
`ConfigureNewLeaf` is null (one built without a workspace, which in practice means a test) inherits its
parent's callbacks instead, so splitting it does not produce a tile that silently never saves. It is
built unconditionally: it opens no device and loads no model until somebody dictates, so the switch in
Settings only has to gate the UI.

