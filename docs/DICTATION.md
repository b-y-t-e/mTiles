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
setting whose failure is silent: a wrong device yields no audio rather than an error), **test** (hold
the shortcut, say a sentence and see it come back, which is the only step that proves the others). The
rules live in
`SpeechSetupFlow`, pure and away from the window, so "what comes next" and "may they leave this step"
are a table test rather than a click-through. Closing it mid-sentence cancels the recording — the
service outlives the window, and an abandoned recording holds the microphone to its five-minute cap.
While it is open **it does the talking**: it subscribes to `DictationService.Error` and shows failures
in its own body, and `MainWindow` stands down whenever it owns a window, because a message box owned by
the main window opens *behind* a modal and cannot be read or dismissed.

It replaced a single screen that asked which model to download and nothing else; that one could not
answer the only question that matters, which is whether dictation works on this machine.

**The shortcut is taught on the last step rather than configured on one of its own**, and that is why
there are still three steps. The page reads *Hold `Alt` `Space` and say something* — the keys drawn as
keycaps (`Border.keycap`, from `HotkeyGesture.GetParts()`) because a shortcut written into a text box reads
as a value to edit and two keycaps read as an instruction. The transcript that comes back then proves
the model, the microphone **and** the shortcut at once, and leaves the user having done the thing they
will do every day. A page that asked them to type a combination and click Next would prove nothing about
it, which is the failure the whole wizard exists to avoid.

**The instruction is written from `Speech.Mode`, not assumed.** It said *Hold* to everybody, and in toggle
mode `DictationHotkeyMachine` ignores the release — so somebody following it held the keys, spoke, let go,
and the recording ran on to its five-minute cap with *Listening…* on screen and no transcript ever
arriving. The step that exists to prove dictation works was proving the opposite, in its own words. Toggle
gets *Press* and a second line saying to press again when finished; the mode itself stays in Settings,
because offering it here would double the explanation for a preference almost nobody has on a first run,
while telling the truth about it costs a verb.

Around that instruction are the three answers somebody might actually have. **Use different keys** puts
the page into a capture mode that lasts *exactly one keystroke* and then returns — an explicit, visible
mode, unlike the Speech tab's "for as long as this box has focus", because here the same keys mean two
different things and an invisible mode would start a recording with the key being bound. **No shortcut**
is offered out loud, because without it the step reads as a demand; the tile's own microphone button
does not go away. Entering the capture **ends a recording of ours first**, and the button row hides while
it is on: "Listening…" beside "Press the keys you want" contradicted itself, and every key pressed to end
that recording was bound as a shortcut instead, leaving it running to its five-minute cap.

**A shortcut that cannot be listened for is not the same as no shortcut**, and the step used to answer both
with *No shortcut — dictation runs from the microphone button*: a hand-edited settings file therefore told
the user they had made a choice they had not made, while the Speech tab, looking at the same value, called
it unusable. `HasHotkey` (there are keys to show) and `IsShortcutBlank` (the setting really is empty)
differ in exactly that case, and `HotkeyAdvice.ForSetting` supplies the sentence. And **Record** stays as the fallback for a shortcut the desktop has taken — it calls
the same `StartTest`/`StopTest` the held keys do, so the two cannot drift into two different trials.

What the keystrokes *mean* is `HotkeyCapture.Interpret`, shared with the Speech tab and pure: a bare
modifier is not an answer (every combination starts with one, so acting on it would store "Alt" the
instant somebody reached for Alt+Space), Tab and Escape are left alone, Backspace and Delete mean "none"
— but only unmodified, or `Ctrl+Backspace` would be impossible to bind and would silently switch the
shortcut off instead. A keystroke is marked handled **only where it is taken**; the reverse was a real
bug that left the settings dialog unclosable. `HotkeyAdvice` holds the one sentence about a shortcut with
no modifier, so the tab and the wizard cannot describe the same choice differently.

The wizard runs **its own `DictationHotkeyMachine`** over its own tunnelling handlers, and emphatically
not `DictationHotkeys`: that is a static bound to one window, so attaching it here would tear the main
window's shortcut down and detaching on close would leave the application with none until it restarted.
Being a window of its own also means the main window's handler never sees these keys, which is why the
tab needs `BeginRebinding` and this does not. Escape is layered rather than overloaded — capturing, it
abandons that; recording, it throws the recording away, as Escape does during dictation everywhere else;
otherwise it closes the window as it always did.

**The release of the gesture's main key is swallowed, and it has to be.** A focused `Button` raises its
Click from the key-**up** of Space and does not care that the key-down was marked handled — measured in a
headless test, not assumed. Nobody reaches the last step except by clicking **Next**, so that button holds
the focus, and on the last step it says **Done**: with the default `Alt+Space`, letting go of the shortcut
shut the whole wizard, on the first attempt anybody ever made to use dictation, at the moment the
transcript was about to arrive. Binding `Alt+Space` in the capture mode did the same thing. An earlier
version of the comment there said buttons do not care about a key-up; they do, and it took a user to find
it, because every rule involved is right on its own and the failure is in how two of them meet in
Avalonia's routing.

**A release is swallowed only when this handler took the matching press** — which is not the same as every
release of the gesture's key, and the first version of the fix got that wrong. With `Alt+Space` bound, a
bare Space is not the shortcut: its press correctly goes through to the focused button, and swallowing its
release meant the button never fired, so somebody with a Space shortcut could not press **Done**, or
anything else, from the keyboard. The test beside it was too weak to see it, because it cleared the
shortcut before pressing Space. The window therefore remembers the one key whose press it claimed, and a
fresh press of that same key settles the claim again — otherwise a release that never arrived (Alt+Tab away
mid-hold) would leave Space swallowed for the life of the window. Only that key: resetting on *any* press
would drop the claim the moment somebody touched another key while holding the shortcut, which is the
original bug back again. It is a **set**, not one slot: two claimed presses overlap — hold `Alt+Space`,
then press **Escape** to abandon the recording, which this handler also takes — and a single field let the
second overwrite the first, so letting go of Space was no longer ours, reached **Done** and shut the
wizard. The reported bug down a different path. Binding a new shortcut while the old one is held did the
same.

**Leaving the step abandons the recording**, and the release is not gated on the step either. Holding the
shortcut and clicking **Back** with the other hand moved the page out from under a live recording:
*Listening…* belongs to the step that has just gone, so the microphone stayed open with nothing anywhere on
screen to say so until the five-minute cap closed it — and because the release *was* gated on the step, the
machine never learnt the key had come up and went on believing a push-to-talk was in progress. Abandoned
rather than stopped: the transcript would arrive in a box the user has navigated away from, so the only
thing worth doing is giving the device back. `SpeechSetupWizardKeyTests` pins all seven cases, including
that a `SettingsChanged` listener throwing while a shortcut is bound does not travel up out of the key
handler — the same guard `DictationHotkeys` has always had on the identical path. (The application would
survive it either way: `CrashHandler` marks dispatcher exceptions handled. It is about reporting the fault
as what it is, and about two copies of one handler shape not disagreeing.)

**Dictation being switched off is answered on the page.** *Set up dictation…* is deliberately not gated on
the switch — configuring before enabling is a reasonable order to work in — so somebody who has turned
dictation off used to reach a step reading *Hold `Alt` `Space` and say something*, an instruction that
cannot succeed, answered by the service's own refusal pointing at Settings → Speech: the window this modal
is covering. A dead end made of two correct components. The step now says so and offers **Turn dictation
on** in place, and **hides the instruction while it is off** — *Hold `Alt` `Space` and say something*
directly beneath *nothing here will record* is an instruction and its own refutation, one line apart. The
hint stays down too, because blaming the desktop for taking a shortcut when the feature is simply off
sends the user to fix the wrong thing.

The capture claims key **presses** and never key **releases**, and the asymmetry is deliberate: a press is
an instruction the capture is waiting for, a release is a fact about the keyboard and belongs to whoever
was told the key went down. Gating the release too breaks a reachable case — holding the shortcut in
push-to-talk while speaking, clicking *use different keys* with the other hand, then letting go: the
machine never hears the release, goes on believing the key is held, and records to its five-minute cap.
Every case such a gate would supposedly cover ends at `!IsRecording` inside the machine and does nothing.

`HotkeyAdvice.ForSetting` answers the same question about a shortcut **as stored**, which is how one
arrives from a file or from this wizard. The Speech tab used to work its warning out only in the property
setter — the path taken when somebody types in the box — while both the settings load and the wizard's
return write the backing field so as not to save everything back. A bare key chosen in the wizard was
therefore accepted in silence, a warning from before it stayed up afterwards, and a shortcut the
application cannot listen for opened the tab with nothing said about why the feature was dead.

**Held the keys and nothing happened** is the one failure here with nothing on screen to read, and it has
a real cause: shortcuts get taken by the desktop before any application sees them, and `Alt+Space` is the
window menu on Windows. After `SpeechSetupFlow.ShortcutHintDelay` (12 s) on the step without the gesture
ever arriving, a hint says so and points at the two things that work regardless. It is armed only when
there *is* a shortcut, cancelled the moment one arrives — whether or not the recording then starts,
because the hint is about the keys reaching us and nothing else — and carries a generation counter so one
scheduled for a visit the user has left cannot fire over a later one.

**The shortcut is not restored when the wizard is closed**, unlike the model. That restore exists because
choosing a model can be left half-done — picked but not downloaded — and closing on that leaves dictation
pointing at a file that is not on the machine. A captured gesture has no half-done state: it is usable
the moment it is pressed. The symmetry is tempting and there is a test pinning it shut.

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


---

## Dictating from a phone

The microphone is next to *you*. Over Remote Desktop, mTiles is not — which makes dictation unusable
exactly where it would help most. The QR button beside Settings opens a panel; the phone that scans a
code there becomes the microphone, and everything downstream of the samples is the pipeline described
above, unchanged. It is push-to-talk on the phone instead of Alt+Space on the keyboard, and the text lands
in the same place either way: the tile that is active when you speak — and, like the shortcut, into the
focused text box first when there is one, so dictating into a Note from a phone works. That focus is
resolved on the UI thread when the recording starts, for the same reason the shortcut resolves it then:
the words belong where the user was looking when they spoke.

The button is **window-level rather than per-tile**. It started in the tile header next to the microphone,
which read as "dictate into *this* tile" — something the feature neither promises nor could deliver, since
the destination is resolved when the recording starts, not when the panel was opened. Next to Settings it
says what it actually is: a way to reach the application.

Everything lives in `Services/Phone/`.

### The seam is `IAudioCapture`

`PhoneAudioCapture` implements it, so `DictationService` gained a second input without gaining a line of
code — it asks for 16 kHz mono samples and has no opinion about which continent they were spoken on.
`RoutedAudioCapture` sits in front of the local capture and the phone one and picks per recording.

**Handles carry their own owner.** `Detach` and `Finish` are split so the slow half runs off the UI
thread, which means a new recording can legally start on the *other* backend while the previous one is
still being closed. Routing `Finish` by "whichever backend is current" would then finish the wrong
recording on the wrong device: silence delivered to the tile, and the real audio dropped. Tagging the
handle at `Detach` makes that unrepresentable.

**Audio is buffered before the recording starts.** The phone announces its sample rate on a socket
thread; starting a dictation has to happen on the UI thread; audio is already arriving in between.
`PrepareForStream` opens the buffer that `Start` later adopts. Without it, push-to-talk loses the first
word of every utterance.

**`IsAvailable` is the router's, not the microphone's.** `DictationService.Start` refuses outright when
the capture reports unavailable, so answering with the local microphone's state meant that a machine with
no working audio backend could not dictate *from a phone* either — despite needing nothing from that
machine's hardware. Those are precisely the machines this feature exists for. While a phone stream is
armed the answer is yes; otherwise it is the microphone's, so Settings still reports honestly that this
machine has no audio input.

**The microphone is handed back thirty seconds after the last utterance.** Left open, the phone shows its
recording indicator for as long as the page is on screen — in a feature whose whole selling point is that
nothing is listening to you, that is the most alarming thing it could possibly do, and it is not even
true. Closed immediately, every press pays `getUserMedia` again (200–300 ms), which in push-to-talk is
the first word. Stopping the *MediaStream tracks* is what clears the indicator; closing the `AudioContext`
alone does not. Going to the background releases it at once rather than waiting out the delay.

**Raw PCM, no codec.** `MediaRecorder` gives webm/opus on Android and mp4/aac on iOS, so accepting its
output would mean shipping two container decoders. An `AudioWorklet` hands the page float samples
directly; it converts to 16-bit and sends them — 32 KB/s on a LAN or a WireGuard tunnel. The page does
**not** request a sample rate (iOS ignores the request and runs the hardware's anyway); it reports what
it got, and `AudioResampler` — the same one the desktop microphone uses — converts.

The buffer is capped at five minutes. `DictationService` already caps a recording by time, but that
timer is armed only when the recording starts through it; here the samples come from another device over
a network, so "the phone stopped sending" is a thing that can simply never happen — a pocketed phone
with a wedged page holds the connection open.

### TLS is not optional

**A browser hands out no microphone outside a secure context.** That single fact shapes the rest:

- **`HttpListener` cannot serve this.** It only does HTTPS against a certificate bound to the port with
  `netsh http sslcert`, which needs administrator rights. Hence **Kestrel**, via a `FrameworkReference`,
  for this one server. The database bridge stays on `HttpListener`: it serves loopback over plain HTTP,
  which `HttpListener` does without ceremony. The remaining option was a hand-written HTTP and WebSocket
  implementation over `SslStream`, on the one socket in this application that faces the network.
- **The sources are not alternatives.** Each certifies a different subset of the addresses this one
  socket is answering for, so `PhoneCertificateProvider` collects them all and TLS picks per connection
  by SNI (`PhoneTlsMaterial.Select`). Taking the first source that answered was a bug with teeth: on a
  machine running Tailscale, a phone connecting over the LAN was served the `.ts.net` certificate — a
  *name mismatch*, which is a worse warning than the self-signed one it replaced, while the panel
  cheerfully promised no warning at all. "Will this warn?" is therefore asked per host, because with two
  certificates in play the two QR codes on screen genuinely differ.
- **`TailscaleCertificateSource`** asks `tailscale cert` for a real Let's Encrypt certificate for the
  machine's MagicDNS name. The only source that produces a page a phone opens without complaint.
- **`SelfSignedCertificateSource`** is the fallback and the only option on a LAN, because no public
  authority will certify `192.168.1.20`. It is **kept on disk**: a new certificate every launch would
  mean a new warning every launch, and a user trained to dismiss certificate warnings is a worse outcome
  than the warning. Regenerated when the address set changes, because a certificate is only accepted for
  a host in its SANs — and on a laptop that set changes with every network joined. It covers **every**
  advertised address, not any: one covering three of four passes an "any" check and then fails on the
  fourth, in the browser, as a warning the user cannot act on.
- PEM-loaded certificates are round-tripped through PKCS#12 on Windows. `CreateFromPemFile` produces a
  private key SChannel refuses, and it fails during the handshake rather than at load.

### The socket

**Sends are serialised per connection.** A `WebSocket` permits exactly one send at a time and *throws* on
a second rather than queueing. Broadcasting without awaiting was right — a stalled phone must not hold up
the dictation service's state change — but broadcasting without serialising was not: a state change and
the transcript that follows it are milliseconds apart, so on any connection slower than a LAN the
transcript was the message thrown away. The one thing the user was waiting for, lost exactly when the
network was bad enough to make them stare at the phone. A continuation chain rather than a semaphore,
because order matters as much as exclusion: "transcribing" arriving *after* the transcript reads as a
phone that never finished.

**A second `begin` from a connection that is already recording is ignored.** Assigning the outcome to the
ownership flag lost the recording for good: the manager refuses the second request *because* it is already
recording, so the flag went false — and from then on nothing could stop what was running. Not `end`, not
`cancel`, not disconnecting. It ran to the five-minute cap with the tile stuck in "recording" and the
phone unable to do anything about it.

**The transcript goes to the phone that spoke, not to everyone.** Broadcasting it put one person's
dictation on every paired device — a second phone in the room, or the browser left open on the near
machine. State messages still go to all of them, because "mTiles is busy" is true for all of them.

**Ending a pairing ends the socket it opened.** Membership is tested at the handshake and never again,
so revoking a session only forgot it: the phone stayed connected and could keep dictating into the
terminal, which is precisely what the panel's "Disconnect this device" button claims to stop. Each
connection remembers its session id, `PhonePairing` raises `SessionEnded`, and the server — the only
object that knows which socket belongs to which pairing — closes it. Expiry takes the same path.

**A recording belongs to the connection that started it.** Only its owner may end or cancel it, only its
frames are written, and only its disconnection cancels. The panel supports several paired devices, so a
second phone dropping off the network is ordinary use — and it used to cancel whatever the first one was
in the middle of saying.

**The pairing URL never becomes the page's address.** `/p/{token}` sets the cookie and answers `302 →
/`. Serving the page directly left the token in the address bar, in the browser history and in whatever
the phone syncs that to — a spent token, but one readable over the shoulder for as long as the page
stayed open. An HTTP redirect leaves no history entry of its own, so the trail really does end there.

**A paired device can reload the page.** `GET /` serves the page to a valid session cookie. Without that
route the only way in was `/p/{token}`, and pairing tokens are single-use by design — so a phone that
refreshed, or was locked and reopened its browser, held a perfectly good session and could reach nothing
with it. It had to be handed a fresh QR code from a machine that might be in another building, which made
"keep running so a paired phone reconnects on its own" a promise the server could not keep.

### Which address the QR code points at

A developer machine has half a dozen: LAN, Tailscale, Hyper-V, WSL, Docker. A QR code holds one URL.

`PhoneEndpointRanker` is pure — no network card, no phone, no server — because it is the one part of
this feature whose behaviour is an opinion. `PhoneEndpointRankingTests` is where that opinion is argued.

The signals, in order of how much they are trusted:

1. **What actually worked last time.** Reported by the phone itself when it loads the page, and the only
   measured fact here — which is why it outranks every heuristic.
2. **Where the user is sitting.** `SM_REMOTESESSION` says whether this is a console or an RDP session.
   The phone is next to the *user*, so at the console a LAN address is the answer and over RDP it cannot
   work at all. Nearly free, and it is the whole difference between a good guess and a coin toss.
3. **Whether the adapter has a default route.** This alone separates real network cards from the pile of
   virtual ones, without a name list that goes stale — a virtual switch has no default route because
   nothing behind it routes anywhere. The name list is a backstop that only demotes what the route test
   already demoted.

IPv4 only. An IPv6 link-local address carries a zone index (`fe80::1%14`) that no phone browser can be
given, and a global IPv6 is rare on the home networks this is for — so offering them would add rows that
mostly do not work.

**The pin is per session location, not global.** One machine gets used both ways. Sitting at it, the LAN
address is right; connected to it over RDP, only a tunnel reaches the phone in the user's hand. The
machine is identical in both cases, so a single remembered winner would have each day's answer overwrite
the other's and be wrong every time the user switched.

**Both audiences are always shown.** The panel draws one code for "phone on this Wi-Fi" and one for
"phone anywhere else"; the session location decides the *order*, never what is on screen. Being wrong
about the order costs a glance; being wrong about what to show costs the whole feature. That is also why
`PhonePairing` allows several live codes at once — a single-token scheme would silently invalidate the
code most likely to be scanned *after* the first one failed.

Adding a way of reaching the machine — a reverse tunnel, a mesh VPN with its own naming, an SSH forward
— is a new `IPhoneEndpointSource` in the list `PhoneEndpointDirectory` is built with. Neither the
ranking nor the server nor the panel changes, because none of them enumerates kinds: they know only the
two audiences.

### The thing being protected is the keyboard

Not the audio. Anything that reaches this server can type into the terminal the user is looking at, so
an unauthenticated bridge on a LAN or a tailnet is a remote shell for everyone on that network.

Two tokens. The **pairing** token is the one in the QR code: short-lived and single-use, because a QR
code is displayed on a screen other people can see and lives in a phone's camera history for months.
Redeeming it yields a **session** token that never appears in a URL, a QR code, or on screen — so a
photographed code is worthless once the owner has scanned it, and worthless anyway two minutes later.
Comparisons are constant-time, including the lookup, which is scanned rather than hashed for that
reason. Closing the panel withdraws every displayed code, which makes closing it a way to revoke.

**Pairings survive a restart, and what is stored is a digest.** The session file holds SHA-256 of each
token, never the token — a bearer credential at rest is a standing grant of terminal access to whoever
reads the file or an old backup of it, and the digest is enough to answer both questions that matter
(is this device paired, and what did it call itself). It doubles as the handle the panel revokes by, so
nothing anywhere needs the raw value once the cookie has been handed out. Shutting down does **not**
forget the devices; turning the bridge off does. Getting that backwards would have made the file
pointless, since every run would erase what the last one wrote — and it is what "keep running so a paired
phone reconnects on its own" actually rests on.

**The session cookie is `SameSite=Lax`, not `Strict`.** It is set on a response that is also a redirect,
at the end of a navigation that began outside the browser entirely — a camera app opening a scanned URL —
and `Strict` is defined against exactly that shape; several browsers withhold the cookie on the redirect
that follows. The cost of being wrong is not a retry but the end of the road, because the pairing token
has already been spent by the time the redirect is issued. `Lax` still withholds the cookie from
cross-site subresource requests and cross-site POSTs, and everything this page does afterwards is
same-site. *Not verified on iOS Safari here* — worth checking on the first real device.

An empty allow-list refuses everything rather than allowing it. Unreachable in practice — the set is
filled before the socket opens — but a security check whose degenerate case is "let it through" is the
wrong way round however unreachable that case looks today.

The `Host` header is checked against the addresses we advertised — defence in depth against DNS
rebinding — and the page carries a CSP whose `connect-src` pins the socket to this server. That
directive names the socket's own `wss://` origin as well as listing `'self'`, deliberately: whether
`'self'` extends to a `wss:` URL on the same host is a corner of CSP browsers have disagreed about, and
WebKit blocked it for several releases. iOS Safari is the first platform this feature is used from, and
getting it wrong fails in the worst available way — the page loads, the microphone opens, the user
speaks, and nothing arrives, with the reason in a console nobody has open on a phone. The origin is
spelled out rather than a bare `wss:` scheme, which would have allowed a socket to any server anywhere
and given away the one thing the directive is here for. The device label the phone sends is stripped of
control characters before it reaches the screen: it is attacker-controlled text, and a terminal
application is the last place an escape sequence should arrive by accident.

**What the self-signed certificate does and does not buy you.** On a LAN there is no alternative — no
public authority will ever certify `192.168.1.20` — so the phone shows a warning and the user accepts
it. Accepting it pins nothing: the browser trusts *that* certificate for *that* host, and it has no way
to tell one self-signed certificate from another issued for the same name. So the guarantee is
**encryption without authentication**. Somebody already positioned to answer for that address on that
network — ARP spoofing on the Wi-Fi, a rogue access point, a hostile router — can present their own
certificate, and the phone will show the same warning the user has been trained by this feature to
accept. What they gain by it is the audio and the ability to type into the terminal, which is the whole
of what the bridge does.

Three things bound that. It is not a passive attack: the traffic is encrypted, so listening is not
enough — the attacker has to intercept and terminate the connection, from a position on the local
network. The session token is the second lock, and it never crosses the wire in the clear or appears in
a QR code, so impersonating the server is not by itself impersonating a paired device; the useful window
is a phone actively pairing or reconnecting. And the bridge is off by default and stops listening once
the panel closes and no device is paired, so the exposure is minutes on a network the user chose, not a
standing service.

The honest summary is that **the LAN code trusts the LAN**. That is why Tailscale is the recommended
path rather than a convenience: its MagicDNS name carries a real certificate from a real authority, so
the phone shows no warning at all, and the identity of the far end is actually checked. If the network
is one where the above matters — a shared office, a conference, anywhere the user would not hand
somebody a terminal — the Tailscale code is the one to scan, and it is always on screen beside the other
one for exactly that reason.

*Not verified on a real device here.* The self-signed handshake on Android and on iOS, and how each
presents the warning, are the first things to check on the first real phone.

The bridge is **off by default** and, unless Settings says otherwise, listens only while the panel is
open or a phone is still paired. Every other server here binds loopback; this one has to accept
connections from the network to be of any use, so it runs when it is being used rather than because the
application is open.

### Starting and stopping

**Every start and stop is serialised.** Three callers reach `StartAsync` without coordinating: the panel
opening, the application starting with the setting on, and any settings change — which includes the one
this class makes itself when it pins the address a phone arrived on, *during that phone's own pairing
request*. Two overlapping starts both saw no running server, both built one, and the second failed to
bind; its error path then called `StopAsync` and disposed **the first one's** server. The user was told
"port already in use", naming a port nothing but this application was using, and left with a bridge that
had just stopped listening. `StartAsync`/`StopAsync` take a semaphore and delegate to `…CoreAsync`, so the
error path cannot deadlock on the lock its caller holds.

**Restarting is keyed on the address set, not only the port.** The server fixes its allowed `Host` values
and its certificate's names when it starts, so a bridge left running across a change of network — exactly
what "keep running" invites, on a laptop — kept answering for addresses this machine no longer has and
rejecting the one it now does. The panel would draw a perfectly good QR code for the new address and the
phone that scanned it met a bare `400`: no page, no explanation, nothing in the panel suggesting anything
was wrong. `PhoneBridgeManagerTests` asserts that one through what the server *answers*, because every
assertion about what the manager believes passed against the broken version too.

**A restart for reconfiguration keeps the pairings**; only a real stop revokes them. Connecting a VPN
only ever *adds* a way to reach this machine, and having that silently unpair the phone in the user's
hand would be the feature undoing itself. The phones reconnect with the cookie they already hold — which
only works because of the `GET /` route above.

**"Look again" reconfigures, it does not merely redraw.** Re-ranking alone produced codes for addresses
the running server had never been told about — its allowed hosts and its certificate are fixed when it
starts — so the button that exists for "this is not working, look again" handed back a perfectly good QR
code that answers `400`.

**Closing the panel waits for the start it interrupted.** Releasing the hold while the bridge was still
coming up meant the "may this stop now" check found nothing to stop, and the bridge then finished
starting with nothing holding it: listening to the network against a setting that says not to.

**A change of address restarts the bridge, panel or no panel.** `NetworkAddressChanged` is what makes
"keep running" survive a laptop: the address set was otherwise only re-read when the panel opened, and
the whole point of that setting is that the panel never has to be. A machine that joined another network
kept a server configured for the old one — answering for addresses it no longer had, holding a
certificate that did not name the one it did, and turning away the paired phone trying to come back, with
nothing on screen to say so because nothing was on screen. The event arrives in bursts, several times per
Wi-Fi handover, so it shares the debounce with Settings.

**A failed start does not unpair anything** — not on disk and not in memory. The failure path went
through the same code as "the user switched this off", which forgets the stored devices, so a machine
that had just woken with no address yet permanently unpaired every phone. Keeping the file but still
clearing the list in memory was only half the fix, and a worse kind of wrong: the phone was paired
according to one and not the other, so it could not reconnect until mTiles was restarted. There is no
server at that point, so a live session can do nothing anyway.

**Expiry is swept for, every five minutes while the bridge runs.** Nothing else notices one: a session
that timed out went on counting as "a phone is paired", which is one of the two things keeping this
listening — so with the setting off and the panel closed, one phone paired at breakfast held the socket
open for the rest of the day. The sweep drops what is stale, raises `Changed` so the panel stops showing
a device that is gone, and re-asks whether the bridge is still needed. A dictionary scan against an
eight-hour timeout costs nothing, and it is what makes the "least exposure" promise true rather than
nearly true.

**The reaction to Settings is gated on the two values it can act on.** This listens to the whole settings
file, so without the gate every keystroke in any settings box scheduled a reconfiguration — and a
reconfiguration re-reads the machine's addresses, which shells out to `tailscale status`. Typing a font
name was spawning processes. Addresses are now only re-read when `NetworkAddressChanged` says they may
have moved.

**The configured port is a preference, not a demand.** On Windows the kernel reserves blocks of ports for
Hyper-V, WSL and Docker at boot — `netsh interface ipv4 show excludedportrange protocol=tcp` lists them —
and a port inside one can never be bound, however free it looks. It is not a collision with another
program either: `netstat` attributes it to PID 4, the kernel. The default 18091 landed inside such a block
on the first machine this ran on, and the panel reported "port already in use" about a port nothing was
using. So a bind failure falls back to a free port and the panel says which one was taken; `0` in Settings
means "choose one" outright. Nobody types this number anywhere — the QR code carries it — so defending it
at the cost of the feature would be the wrong way round.

Two consequences worth knowing. The **firewall rule is scoped to the executable rather than to a port**,
because a rule naming one port would stop matching the moment the fallback fired, and re-approving it
means a UAC prompt per launch. And the restart check compares the **requested** port against the setting,
never the active one: after a fallback those two differ on purpose, and comparing the active port would
restart the bridge for ever. Detecting "that port is unavailable" needs all three shapes the failure
takes — Kestrel reports a plain collision as `AddressInUseException`, which derives from
`InvalidOperationException` and so is missed entirely by a check for socket errors. That one was found by
the test, not by reading.

**The reaction to Settings is debounced** by 750 ms. The port is a spinner bound straight to the stored
value, so raising it from 18091 to 18095 saves five times on the way; without the debounce each
intermediate number tore the server down and bound it again — four pointless rebinds, four chances to
lose a race with the operating system over a port still in `TIME_WAIT`, and a paired phone dropped in the
middle. The timer's callback is wrapped, because an exception escaping a thread-pool timer ends the
process.

One rule decides when it may stop, in `StopIfUnneededAsync`: the setting does not ask it to stay up, no
panel is open, no phone is paired. The panel holds it up with a scope (`HoldOpen`) rather than restating
that rule, because three different things can want it stopped and each restatement is a chance to
disagree with the others. Without watching the setting at all, the switch was write-only in the direction
that matters: turning it *off* left a server listening on the network until the application was
restarted.

**The tile name is cached, not read on demand.** `DescribeState` runs on a socket thread and the name
lives in an Avalonia view-model tree — the one place this class reached into the UI graph from the
network. It is refreshed on the dispatcher whenever the dictation state changes and when a phone
connects. A stale name costs a wrong caption for a fraction of a second; a torn read costs something
nobody has bounded.

**The pin is written on the UI thread**, even though it is learned on a Kestrel request thread. The
settings graph is plain `Dictionary`, and the debounced save walks it from elsewhere: writing a key
during that walk throws inside the save, on a thread-pool thread, at a moment nobody would connect to
somebody having scanned a QR code. Every other writer of that file is already on the UI thread.

Certificates are disposed when the bridge stops. They hold key handles the operating system keeps until
released, and restarting for a port change is an ordinary act.

### The firewall

Windows raises its own "allow this app to communicate" prompt the first time a process listens on a
non-loopback address, and **if the user dismisses it, Windows writes a block rule and never asks
again**. The feature then fails for ever with no message anywhere. That, not the absence of an allow
rule, is what `WindowsFirewallGuide` exists to undo: it removes every inbound rule pointing at this
executable before adding one, because an existing block would win over anything added after it.

The shell is launched by absolute path (`%SystemRoot%\System32\WindowsPowerShell1.0\powershell.exe`).
This runs with `runas`, so resolving the name through `PATH` would be a way to get somebody else's binary
elevated by a user who thought they were fixing a firewall rule. The script also checks afterwards that
the rule exists and that at least one network is classified **Private**: a Private-profile rule on a
machine Windows considers Public is inert, and reporting success there would send the user hunting for a
different fault entirely.

Offered, never silent — the UAC prompt is the consent — and only after twenty seconds with nothing
connected, so somebody scanning at a normal pace never sees it. **Private profile only**: a bridge meant
for the user's own Wi-Fi has no business listening on café networks. Cancelling the UAC prompt is
reported as a decision rather than a failure. On Linux the guide hands over a command instead of running
it: there is no desktop-wide consented-elevation prompt to invoke, and a GUI that shells out to `sudo`
either finds no terminal to prompt in or teaches the user to grant root to whatever asked politely.

### What this does to the privacy promise

Recognition still runs on the machine mTiles is on, and nothing reaches a third party. What changed is
that the audio crosses from one device you own to another, encrypted. Said plainly in the README rather
than left for someone to discover.
