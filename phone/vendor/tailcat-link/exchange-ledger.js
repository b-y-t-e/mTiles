// Remembers, for as long as the sender may still be retrying, what each
// request was answered with — so a request that arrives twice is answered
// twice but only ever *run* once.
//
// The far end retries a request across sessions under the id it already had,
// and a session can die between the handler finishing and its answer reaching
// the sender. Without this the retry would run the handler a second time,
// which for the commands a link carries — restart a service, write a file,
// take a payment — is not an acceptable way to lose an answer.
//
// It belongs to the link rather than the session, because the retry that has
// to be recognised arrives on the *next* session by definition. The .NET half
// is `ExchangeLedger.cs`, and this keeps its retention and its cap.

import { hex } from "./bytes.js";

// A ceiling on memory for a peer that floods ids. The oldest answered
// exchanges go first, which are also the ones least likely to still be
// retried; an exchange still running is never one of them.
const MAX_REMEMBERED = 4096;

export class ExchangeLedger {
  #retention;
  #now;
  #answers = new Map();

  constructor({ retention, now = () => Date.now() }) {
    this.#retention = retention;
    this.#now = now;
  }

  /// Returns the answer to `exchange`, producing it with `produce` the first
  /// time and recalling it after that. A request still in flight is joined
  /// rather than started again.
  answer(exchange, produce) {
    const id = hex(exchange);
    this.#forgetStale();

    const seen = this.#answers.get(id);
    if (seen) return seen.answer;

    if (!this.#makeRoom()) {
      // Everything remembered is still running, so nothing can be dropped
      // without risking a handler running twice — which is the one thing this
      // ledger exists to prevent. Refusing is recoverable: the exchange dies
      // without an answer, and the sender's retry finds room once one of them
      // finishes.
      throw new Error(`the other machine has ${MAX_REMEMBERED} requests still running`);
    }

    const answer = (async () => {
      try {
        return await produce();
      } catch (error) {
        // No answer was produced, so there is nothing to recall: a retry must
        // be allowed to try again rather than inherit this failure.
        this.#answers.delete(id);
        throw error;
      }
    })();
    // An answer nobody awaits twice must not look unhandled while it waits to
    // be recalled.
    answer.catch(() => {});
    this.#answers.set(id, { answer, at: this.#now(), done: false });
    answer.then(
      () => this.#markDone(id),
      () => {},
    );
    return answer;
  }

  #markDone(id) {
    const remembered = this.#answers.get(id);
    if (remembered) remembered.done = true;
  }

  /// Drops what the sender can no longer be retrying.
  #forgetStale() {
    const now = this.#now();
    for (const [id, remembered] of this.#answers) {
      if (now - remembered.at > this.#retention) this.#answers.delete(id);
    }
  }

  /// Frees a slot for one more exchange by forgetting the oldest ones that
  /// have already been answered, and reports whether it managed to.
  #makeRoom() {
    if (this.#answers.size < MAX_REMEMBERED) return true;

    const answered = [...this.#answers]
      .filter(([, remembered]) => remembered.done)
      .sort(([, a], [, b]) => a.at - b.at)
      .slice(0, this.#answers.size - MAX_REMEMBERED + 1);
    for (const [id] of answered) this.#answers.delete(id);
    return this.#answers.size < MAX_REMEMBERED;
  }
}
