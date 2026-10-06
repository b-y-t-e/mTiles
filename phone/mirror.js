// What is typed on the phone is mirrored into the tile's own box on the computer as it is typed, so the two
// never disagree: the computer shows what the phone is writing, and a send finds the same words already
// there. One at a time, the latest text winning; a refusal — somebody typing on the computer meanwhile —
// stops the mirror until the box is sent or cleared, rather than writing over their words.
//
// No DOM and no page state: what to send is asked of `capture`, where it lands is told to `onLanded` and
// `onLeft`, and the clock is handed in, so every order of events here can be driven from a test.

/** The code the computer answers with when its box holds words the phone has not seen. */
export const DRAFT_IN_THE_WAY = "draftInTheWay";

export class DraftMirror {
  /**
   * @param {object} o
   * @param {() => object|null} o.capture   the mirror the open tile asks for now:
   *   `{ tileId, machine, conn, text, seen, unchanged }`, or null when there is none to send
   * @param {(target: object) => boolean} o.isOpen  whether the target's tile is still the one on screen
   * @param {(target: object) => void} [o.onLanded] the open tile's box now holds `target.text`
   * @param {(target: object) => void} [o.onLeft]   it landed for a tile the phone has since left
   * @param {(message: string) => void} [o.onError] a failure that is not a refusal
   */
  constructor({ capture, isOpen, onLanded = () => {}, onLeft = () => {}, onError = () => {},
    delay = 300, timers = { set: (fn, ms) => setTimeout(fn, ms), clear: (id) => clearTimeout(id) } }) {
    Object.assign(this, { capture, isOpen, onLanded, onLeft, onError, delay, timers });
    this.timer = null;
    this.busy = false;
    this.pending = false;
    this.queued = null;
    /** The text the computer's box was last set to from here. */
    this.mirrored = null;
    /** The mirror on its way there now, whole, so what it is for is known. */
    this.sending = null;
    /** The computer refused: nothing more goes until `forget`. */
    this.refused = false;
  }

  /** Asks for a mirror after the debounce; a refused mirror asks for nothing. */
  soon() {
    this.timers.clear(this.timer);
    if (this.refused) return;
    this.pending = true;
    this.timer = this.timers.set(() => this.now(), this.delay);
  }

  /** Sends a mirror still waiting on its debounce straight away: the tile it is for is about to be left. */
  flush() {
    if (!this.pending) return;
    this.timers.clear(this.timer);
    this.now();
  }

  /** Drops a mirror still waiting: a send is about to set the computer's box itself. */
  cancel() {
    this.timers.clear(this.timer);
    this.pending = false;
    this.queued = null;
  }

  /** Starts over: what the computer's box holds is no longer anything this mirror put there. */
  forget() {
    this.mirrored = null;
    this.refused = false;
  }

  /** What the computer's box holds from here: a mirror still on its way lands before a send does. */
  holding(adopted) {
    return this.inFlight ?? this.mirrored ?? adopted;
  }

  /** The text on its way to the open tile's box; a mirror still landing for a tile left behind is not it. */
  get inFlight() {
    return this.sending && this.isOpen(this.sending) ? this.sending.text : null;
  }

  async now(target = this.capture()) {
    this.pending = false;
    if (!target || target.text === target.unchanged) return;
    if (this.busy) { this.queued = target; return; }
    this.busy = true;
    this.sending = target;
    let landed = false;
    let refusal = false;
    try {
      await target.conn.request({ type: "draft", tileId: target.tileId, text: target.text, seen: target.seen });
      landed = true;
      if (this.isOpen(target)) {
        this.mirrored = target.text;
        this.onLanded(target);
      } else {
        this.onLeft(target);
      }
    } catch (error) {
      refusal = error.code === DRAFT_IN_THE_WAY;
      if (refusal) { if (this.isOpen(target)) this.refused = true; }
      else this.onError(error.message);
    } finally {
      this.busy = false;
      this.sending = null;
      this.sendQueued(target, landed, refusal);
    }
  }

  sendQueued(previous, landed, refused) {
    const next = this.queued;
    this.queued = null;
    if (!next) return;
    const sameTile = next.tileId === previous.tileId && next.machine === previous.machine;
    // A later text for a tile whose mirror was just refused would be refused the same way.
    if (sameTile && landed) next.seen = next.unchanged = previous.text;
    // Only a refusal drops it: after a failed request the last text typed is still owed to the computer.
    if (!sameTile || !refused) this.now(next);
  }
}
