// Hold to talk: the microphone, as 16-bit PCM, on a channel of its own for each utterance.
//
// The channel is ordered, which is the whole reason it is used: its first frame says which tile and at
// what rate, the audio follows, and closing it says "that was the sentence". A one-byte frame of zero
// throws the recording away. Recognition happens on the computer; nothing here transcribes anything.
//
// Carried over from the page this replaced, and each one paid for there: no sample rate is requested
// (iOS ignores it and the samples would not match the number sent), the microphone is released after a
// while idle (holding it keeps the phone's recording light on), and a release during the permission
// prompt must not start a recording afterwards with no gesture left to end it.

const IDLE_RELEASE_MS = 30000;

export class Dictation {
  #getLink;
  #onError;
  #ctx = null;
  #stream = null;
  #tap = null;
  #opening = null;
  #idle = null;
  #held = false;
  #recording = false;
  #queue = Promise.resolve();
  #channel = null;
  #pending = [];

  constructor({ getLink, onError }) {
    this.#getLink = getLink;
    this.#onError = onError;
  }

  get recording() { return this.#recording; }

  /** Recording, or asked to and still opening the microphone — what a second tap has to stop rather
   *  than start again. */
  get active() { return this.#held || this.#recording; }

  /** Starts a recording aimed at `tileId`. Called from the press itself, so the audio context is
   *  created inside a user gesture — which is what iOS demands of it. With `toDraft` the computer hands
   *  the sentence back for this page's own text box and types it into nothing. */
  async begin(tileId, { toDraft = false } = {}) {
    if (this.active) return;
    this.#held = true;
    this.#prime();

    try {
      await this.#ensureAudio();
    } catch (error) {
      this.#held = false;
      this.#release();
      this.#onError(error?.name === "NotAllowedError"
        ? "Microphone access was refused. Allow it in the browser's site settings."
        : "This browser would not open the microphone.");
      return;
    }

    if (!this.#held) return; // let go while the browser was asking for permission

    const link = this.#getLink();
    if (!link) return;

    this.#recording = true;
    this.#pending = [];
    this.#channel = null;
    const header = new TextEncoder().encode(JSON.stringify({ tileId, sampleRate: this.#ctx.sampleRate, toDraft }));

    // Frames spoken while the channel opens are held and sent after the header, in order — otherwise the
    // first syllable is lost, which in push-to-talk is the first word.
    this.#queue = link.openChannel("audio").then(async (channel) => {
      this.#channel = channel;
      await channel.send(header);
      for (const frame of this.#pending.splice(0)) await channel.send(frame);
    }).catch(() => {
      this.#recording = false;
      this.#onError("mTiles is not reachable right now, so nothing was recorded.");
    });
  }

  /** Ends the utterance: sent for transcription, or — when `cancel` — thrown away. */
  finish(cancel = false) {
    this.#held = false;
    if (!this.#recording) return;
    this.#recording = false;

    this.#queue = this.#queue.then(async () => {
      const channel = this.#channel;
      this.#channel = null;
      if (!channel) return;
      if (cancel) await channel.send(new Uint8Array([0])).catch(() => {});
      await channel.close().catch(() => {});
    });

    this.#scheduleRelease();
  }

  #send(samples) {
    if (!this.#recording) return;
    const pcm = new Int16Array(samples.length);
    for (let i = 0; i < samples.length; i++) {
      const s = Math.max(-1, Math.min(1, samples[i]));
      pcm[i] = s < 0 ? s * 0x8000 : s * 0x7fff;
    }
    const frame = new Uint8Array(pcm.buffer);

    if (!this.#channel) {
      this.#pending.push(frame);
      return;
    }
    const channel = this.#channel;
    this.#queue = this.#queue.then(() => channel.send(frame)).catch(() => {});
  }

  #prime() {
    if (!this.#ctx) this.#ctx = new (window.AudioContext || window.webkitAudioContext)();
    if (this.#ctx.state === "suspended") this.#ctx.resume().catch(() => {});
  }

  #ensureAudio() {
    if (this.#idle) { clearTimeout(this.#idle); this.#idle = null; }
    if (this.#tap) return this.#ctx.state === "suspended" ? this.#ctx.resume() : Promise.resolve();
    if (this.#opening) return this.#opening;
    this.#opening = this.#open().finally(() => { this.#opening = null; });
    return this.#opening;
  }

  async #open() {
    this.#stream = await navigator.mediaDevices.getUserMedia({
      audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
    });
    await this.#ctx.resume();
    const mic = this.#ctx.createMediaStreamSource(this.#stream);

    try {
      await this.#ctx.audioWorklet.addModule(new URL("tap-worklet.js", import.meta.url));
      this.#tap = new AudioWorkletNode(this.#ctx, "tap");
      this.#tap.port.onmessage = (event) => this.#send(event.data);
    } catch {
      // Older Safari and a few Android browsers. Deprecated, on the main thread, and adequate.
      this.#tap = this.#ctx.createScriptProcessor(4096, 1, 1);
      this.#tap.onaudioprocess = (event) => this.#send(event.inputBuffer.getChannelData(0));
    }

    // A muted sink: a node that reaches no destination is never pulled and delivers nothing.
    const silent = this.#ctx.createGain();
    silent.gain.value = 0;
    mic.connect(this.#tap);
    this.#tap.connect(silent);
    silent.connect(this.#ctx.destination);

    if (!this.#held && !this.#recording) this.#release();
  }

  #scheduleRelease() {
    if (this.#idle) clearTimeout(this.#idle);
    this.#idle = setTimeout(() => this.#release(), IDLE_RELEASE_MS);
  }

  /** Stopping the tracks is what clears the phone's recording light; closing the context alone does not. */
  #release() {
    if (this.#recording) return;
    if (this.#opening) { this.#opening.then(() => this.#release()).catch(() => {}); return; }
    for (const track of this.#stream?.getTracks() ?? []) track.stop();
    this.#stream = null;
    try { this.#ctx?.close(); } catch { /* already closed */ }
    this.#ctx = null;
    this.#tap = null;
  }
}
