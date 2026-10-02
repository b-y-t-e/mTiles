// A deadline that measures silence rather than duration: it fires only when
// nothing has moved for its whole limit. `IdleTimeout.cs` is the same thing on
// the .NET side, and the reasoning is the same.
//
// An exchange may legitimately take far longer than one request window — a
// sixteen-megabyte payload through a shared relay is minutes, not seconds —
// while a peer that has gone away is recognised by nothing arriving at all. A
// total deadline on the exchange would confuse the two and make large messages
// impossible: every retry would resend from the start and run out of time in
// exactly the same place.

export class IdleTimeout {
  #limitMs;
  #controller = new AbortController();
  #timer = null;

  /// @param limitMs how long nothing may happen before the exchange is given up on.
  constructor(limitMs) {
    this.#limitMs = limitMs;
    this.restart();
  }

  /// Aborts once the limit passes with nothing having moved. This is what the
  /// stream reads and writes are given, so they stop with it.
  get signal() {
    return this.#controller.signal;
  }

  /// Whether the silence, rather than anything else, ended the exchange.
  get expired() {
    return this.#controller.signal.aborted;
  }

  get limitMs() {
    return this.#limitMs;
  }

  /// Reports progress, which starts the limit again from now.
  restart() {
    if (this.expired) return;
    clearTimeout(this.#timer);
    this.#timer = setTimeout(
      () => this.#controller.abort(new Error(`nothing arrived for ${this.#limitMs} ms`)),
      this.#limitMs,
    );
  }

  /// The exchange is over. Without this the timer outlives it and keeps the
  /// page awake for one more window.
  stop() {
    clearTimeout(this.#timer);
    this.#timer = null;
  }
}
