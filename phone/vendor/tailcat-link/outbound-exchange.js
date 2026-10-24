// One exchange this browser is sending, one attempt at it on one session, and
// the loop that carries it across as many sessions as it takes.
// `OutboundExchange.cs`, `ExchangeAttempt.cs` and `ExchangeSender.cs` are the
// .NET half, and read the same way.

import { Deferred, str } from "./bytes.js";
import { LinkClosedError, LinkError, LinkTimeoutError, PairingRefusedError, RemoteHandlerError } from "./errors.js";
import {
  BLOCK_BYTES,
  ExchangeFlags,
  decodeAnswerHeader,
  decodeOffset,
  encodeExchangeHeader,
  writeBlocks,
} from "./exchange-frame.js";
import { IdleTimeout } from "./idle-timeout.js";
import { IncomingContent } from "./incoming-content.js";
import { Capabilities, FrameKind, FrameStatus, newExchange, readFrame, writeFrame } from "./link-frame.js";

/// One exchange, and how far it has got in both directions. It belongs to the
/// link rather than to a session: the id the other machine knows it by, where
/// the content has got to, and how much of the answer has arrived all survive
/// the session that dies under them.
export class OutboundExchange {
  #attempted = false;
  #lastProgress = Date.now();

  constructor(content, flags, patienceMs) {
    this.id = newExchange();
    this.content = content;
    this.flags = flags;
    /// How long nothing may move — no block either way, and no session to move
    /// one on — before the exchange is given up on. The only limit it has.
    this.patienceMs = patienceMs;
    this.sent = 0;
    this.answer = null;
    this.answerStarted = new Deferred();
    /// Sent once and never again: a notification to a machine that could not
    /// recognise one it has already had.
    this.onlyOnce = false;
  }

  /// How long nothing has moved, in either direction.
  get stalledMs() {
    return Date.now() - Math.max(this.#lastProgress, this.answer?.lastArrived ?? 0);
  }

  /// Whether this attempt may send the content without waiting to be told
  /// where to start — the first attempt, with content that fits one block,
  /// where the answer could only have been zero.
  takeFirstAttempt() {
    const first = !this.#attempted;
    this.#attempted = true;
    return first && this.sent === 0 && this.content.length <= BLOCK_BYTES;
  }

  headerFor(pipelined) {
    return {
      flags: pipelined ? this.flags | ExchangeFlags.Pipelined : this.flags,
      length: this.content.length,
      answerOffset: this.answer?.bytesReceived ?? 0,
      name: this.content.name,
      contentType: this.content.contentType,
      metadata: this.content.metadata,
    };
  }

  /// Puts the content back to where the other machine says it got to. The
  /// receiver decides: it is the one that knows which blocks made it out of a
  /// session that then died.
  rewindTo(offset) {
    this.sent = offset;
    this.moved();
  }

  advance(bytes) {
    this.sent += bytes;
    this.moved();
    this.content.progress?.(this.sent, this.content.length);
  }

  /// Takes the answer's header, the first time it arrives.
  startAnswer(header) {
    this.answer ??= new IncomingContent({ id: this.id, ...header });
    this.moved();
    this.answerStarted.resolve(this.answer);
    return this.answer;
  }

  /// Takes a whole answer, from a machine that sends answers in one frame.
  takeWholeAnswer(bytes) {
    this.answer ??= IncomingContent.whole(this.id, bytes);
    this.moved();
    this.answerStarted.resolve(this.answer);
  }

  moved() {
    this.#lastProgress = Date.now();
  }
}

/// Moves as much of an exchange, in both directions, as one session lives long
/// enough to move.
///
/// @param carrier what the attempt needs of its session: `alive`, a signal
///   aborted when the session stops; `openStream()`, a stream whose movement
///   counts towards the session being alive; and `failUnlessBusy(reason,
///   silence)`, which ends the session unless what failed was one exchange's
///   silence on a session still moving other bytes.
export async function runExchangeAttempt(carrier, exchange, signal) {
  const idle = new IdleTimeout(exchange.patienceMs);
  const alive = signal ? AbortSignal.any([carrier.alive, signal]) : carrier.alive;
  const moving = { signal: AbortSignal.any([alive, idle.signal]), restart: () => idle.restart() };
  let stream;
  try {
    stream = carrier.openStream();
    const header = await sendContent(stream, exchange, moving);
    await receiveOutcome(stream, exchange, header, moving, alive);
  } catch (error) {
    // An answer about the exchange, a protocol the peer broke, or a caller who
    // gave up: none of them says anything about the session.
    if (error instanceof LinkError || signal?.aborted) throw error;
    const reason = idle.expired ? `the exchange moved nothing for ${exchange.patienceMs} ms` : error.message;
    carrier.failUnlessBusy(reason, idle.expired);
    throw new LinkError(reason, { cause: error });
  } finally {
    idle.stop();
    await stream?.close().catch(() => {});
  }
}

async function sendContent(stream, exchange, moving) {
  const pipelined = exchange.takeFirstAttempt();
  const header = exchange.headerFor(pipelined);
  moving.restart();
  await writeFrame(stream, FrameKind.Exchange, exchange.id, encodeExchangeHeader(header), moving);
  if (pipelined) await writeContent(stream, exchange, moving);

  const taken = await readFrame(stream, moving);
  throwIfRefused(taken.tag, taken.payload, exchange.flags);
  const offset = decodeOffset(taken.payload, exchange.content.length);
  if (!pipelined) {
    exchange.rewindTo(offset);
    await writeContent(stream, exchange, moving);
  }
  return header;
}

const writeContent = (stream, exchange, moving) =>
  writeBlocks(stream, exchange.content.bytes, exchange.sent, moving, (bytes) => exchange.advance(bytes));

async function receiveOutcome(stream, exchange, header, moving, alive) {
  if (exchange.flags & ExchangeFlags.Answer) {
    await receiveAnswer(stream, exchange, header.answerOffset, alive);
    return;
  }
  // Without the silence bound unless the exchange asked to be told on
  // delivery: otherwise what is waited for is the receiving application's
  // handler finishing with the content, which may take as long as it takes.
  // A peer that has gone away is the heartbeat's business.
  const done =
    exchange.flags & ExchangeFlags.AckOnDelivery
      ? await readFrame(stream, moving)
      : await readFrame(stream, { signal: alive, restart() {} });
  throwIfRefused(done.tag, done.payload, exchange.flags);
  await acknowledge(stream, exchange, alive);
}

async function receiveAnswer(stream, exchange, answerOffset, alive) {
  // The handler is working, which is not the peer being silent, so the wait is
  // bounded without condemning a session other exchanges may be sharing.
  const handling = new IdleTimeout(exchange.patienceMs);
  let outcome;
  try {
    outcome = await readFrame(stream, { signal: AbortSignal.any([alive, handling.signal]), restart() {} });
  } catch (error) {
    if (handling.expired && !alive.aborted) {
      throw new LinkError(`no answer within ${exchange.patienceMs} ms`, { cause: error });
    }
    throw error;
  } finally {
    handling.stop();
  }
  throwIfRefused(outcome.tag, outcome.payload, exchange.flags);

  const answer = exchange.startAnswer(decodeAnswerHeader(outcome.payload));
  await answer.readBody(stream, answerOffset, { silenceMs: exchange.patienceMs, signal: alive });
  await acknowledge(stream, exchange, alive);
}

// Tells the other machine this end has the outcome, so it can let the exchange
// go now rather than at the end of its retention. Never retried: the outcome is
// already here, and a retry would reach a machine that may have forgotten the
// exchange and would run its handler again.
async function acknowledge(stream, exchange, alive) {
  try {
    await writeFrame(stream, FrameStatus.Ok, exchange.id, new Uint8Array(0), { signal: alive, restart() {} });
  } catch {
    // Nothing is lost if it never arrives.
  }
}

function throwIfRefused(tag, payload, flags) {
  if (tag !== FrameStatus.Failed) return;
  const what =
    flags & ExchangeFlags.Transfer
      ? "would not take the transfer"
      : flags & ExchangeFlags.Answer
        ? "could not answer"
        : "would not take the notification";
  throw new RemoteHandlerError(`the other machine ${what}: ${str(payload)}`);
}

/// Carries exchanges to the host across as many sessions as they take, each in
/// the shape the host said it takes.
export class ExchangeSender {
  #sessions;
  #minReconnectDelay;

  /// @param sessions `current()`, resolving to the session that is up;
  ///   `ended()`, resolving when that session, or the next, ends; and
  ///   `stopping`, a signal aborted once the link is closed.
  constructor({ sessions, minReconnectDelay }) {
    this.#sessions = sessions;
    this.#minReconnectDelay = minReconnectDelay;
  }

  /// Sends a request and resolves with the whole answer.
  async request(content, patienceMs, signal) {
    const exchange = new OutboundExchange(content, ExchangeFlags.Answer, patienceMs);
    await this.#run(exchange, signal);
    // Settled already; awaited so that an answer which ended short of its
    // announced length is the failure it is, rather than a result to read.
    return exchange.answer.complete;
  }

  /// Sends an exchange nothing waits on an answer for.
  async deliver(content, flags, patienceMs, signal) {
    await this.#run(new OutboundExchange(content, flags, patienceMs), signal);
  }

  /// Deliberately without a deadline on the whole: any total limit would be a
  /// limit on how large content this client can send. What is bounded is
  /// silence, and waiting for a session counts as silence too, so an exchange
  /// with a host that has gone for good ends in bounded time.
  async #run(exchange, signal) {
    let last;
    for (;;) {
      const left = exchange.patienceMs - exchange.stalledMs;
      if (left <= 0 || signal?.aborted || this.#sessions.stopping.aborted) break;

      // Taken before the attempt: a session that dies during it ends after it.
      const ended = this.#sessions.ended();
      try {
        const ready = await this.#readyWithin(left, signal);
        if (!ready) break;
        await this.#sendInTheShapeTaken(ready.session, ready.capabilities, exchange, signal);
        return;
      } catch (error) {
        // The host decided, the pairing is gone, or the link was closed: none
        // of them is changed by sending again.
        if (error instanceof RemoteHandlerError || error instanceof PairingRefusedError || error instanceof LinkClosedError) {
          throw error;
        }
        if (signal?.aborted || this.#sessions.stopping.aborted) break;
        if (exchange.onlyOnce) throw error;
        last = error;
      }

      // Never straight round again: the session is usually already ending, and
      // waiting for that is what stops the next attempt spinning on it.
      await Promise.race([ended, pause(this.#minReconnectDelay)]);
    }

    if (signal?.aborted) throw signal.reason ?? new LinkClosedError("the exchange was given up on");
    if (this.#sessions.stopping.aborted) throw this.#sessions.fatal() ?? new LinkClosedError("the link is closed");
    throw new LinkTimeoutError(
      exchange.flags & ExchangeFlags.Transfer
        ? `the transfer moved nothing for ${exchange.patienceMs} ms, after ${exchange.sent} bytes`
        : exchange.flags & ExchangeFlags.Answer
          ? `no answer within ${exchange.patienceMs} ms`
          : `the notification moved nothing for ${exchange.patienceMs} ms`,
      { cause: last },
    );
  }

  /// The session up now and what its host can take, or nothing if they do not
  /// both come within `left`. Asking what a host can take is a ping, which a
  /// saturated link can outpace, and waiting for it is silence all the same.
  async #readyWithin(left, signal) {
    const patience = new IdleTimeout(left);
    const either = signal ? AbortSignal.any([signal, patience.signal]) : patience.signal;
    try {
      const session = await abortable(this.#sessions.current(), either);
      const capabilities = await abortable(session.capabilities(), either);
      return { session, capabilities };
    } catch (error) {
      if (patience.expired && !signal?.aborted) return null;
      throw error;
    } finally {
      patience.stop();
    }
  }

  async #sendInTheShapeTaken(session, capabilities, exchange, signal) {
    if (capabilities & Capabilities.Exchanges) {
      await runExchangeAttempt(session.carrier, exchange, signal);
      return;
    }
    if (capabilities & Capabilities.LargeFrames) {
      await sendAsFrames(session, exchange);
      return;
    }
    throw new RemoteHandlerError("the other machine says it takes neither exchanges nor frames");
  }
}

/// Sends an exchange as the single frames a host without exchanges takes.
async function sendAsFrames(session, exchange) {
  if (exchange.flags & ExchangeFlags.Transfer) {
    throw new RemoteHandlerError("the other machine does not take transfers");
  }
  const whole = exchange.content.bytes;
  if (exchange.flags & ExchangeFlags.Answer) {
    exchange.takeWholeAnswer(await session.exchange(FrameKind.Request, exchange.id, whole));
    return;
  }
  // That host cannot recognise a notification it has already had, so it gets
  // one attempt.
  exchange.onlyOnce = true;
  await session.exchange(FrameKind.Notify, exchange.id, whole, { expectAnswer: false });
}

const pause = (ms) =>
  new Promise((resolve) => {
    const timer = setTimeout(resolve, ms);
    timer.unref?.();
  });

// A wait given up on when the signal fires, reporting the signal's reason.
function abortable(promise, signal) {
  if (signal.aborted) return Promise.reject(signal.reason);
  return new Promise((resolve, reject) => {
    const onAbort = () => reject(signal.reason);
    signal.addEventListener("abort", onAbort, { once: true });
    promise.then(
      (value) => {
        signal.removeEventListener("abort", onAbort);
        resolve(value);
      },
      (error) => {
        signal.removeEventListener("abort", onAbort);
        reject(error);
      },
    );
  });
}
