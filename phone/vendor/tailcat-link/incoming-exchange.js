// One exchange this browser is receiving, and every exchange it is receiving.
// `IncomingExchange.cs` and `ExchangeRegistry.cs` are the .NET half.
//
// Everything that has to survive a session dying lives here, on the link: the
// content received so far, the handler's run, and the answer it gave. A session
// only moves blocks. So an exchange that comes back on a fresh session carries
// on mid-content, and a sender whose session died after the handler finished
// is answered rather than having the handler run a second time.

import { Deferred, hex, utf8, withAbort } from "./bytes.js";
import { LinkError } from "./errors.js";
import {
  ExchangeFlags,
  announcesPipelinedContent,
  decodeExchangeHeader,
  drainBlocks,
  encodeAnswerHeader,
  encodeOffset,
  writeBlocks,
} from "./exchange-frame.js";
import { IdleTimeout } from "./idle-timeout.js";
import { IncomingContent } from "./incoming-content.js";
import { LinkContent } from "./link-content.js";
import { FrameStatus, readFrame, writeFrame } from "./link-frame.js";

/// How long an exchange nobody comes back for is kept, and the answer of one
/// that was finished: `LinkProtocol.TransferRetention`, the same on both sides
/// because the sender decides when to come back and nothing on the wire says.
export const EXCHANGE_RETENTION_MS = 10 * 60 * 1000;

export class IncomingExchange {
  #id;
  #flags;
  #retentionMs;
  #ackPatienceMs;
  #forget;
  #timer = null;
  #run = null;
  #outcome = null;
  #delivering = null;
  #letGo = Promise.resolve();
  #expired = false;
  #finished = false;
  #answered;
  #unheardFailure;
  #announced = false;

  /// @param ackPatienceMs how long to wait for the sender to say it has the
  ///   outcome. Not hearing it only means the exchange is kept for the
  ///   retention window instead of being let go of at once.
  /// @param answered told `(request, answer)` the first time an answer has been
  ///   written in full, which is what a page watching its own answers leave waits for.
  /// @param unheardFailure told the error of a handler whose sender can no
  ///   longer hear it: a notification's, acknowledged on delivery.
  constructor({ id, header, retentionMs, ackPatienceMs, forget, answered = () => {}, unheardFailure = () => {} }) {
    this.#answered = answered;
    this.#unheardFailure = unheardFailure;
    this.#id = id;
    this.#flags = header.flags;
    this.#retentionMs = retentionMs;
    this.#ackPatienceMs = ackPatienceMs;
    this.#forget = forget;
    this.request = new IncomingContent({
      id,
      name: header.name,
      contentType: header.contentType,
      length: header.length,
      metadata: header.metadata,
    });
    this.#schedule();
  }

  /// Keeps what the handler is, to be run once, when the content is whole.
  start(run) {
    this.#run ??= run;
  }

  /// Takes one session's attempt: the content from wherever the last attempt
  /// got to, and the answer from wherever the sender says it got to.
  async deliver(stream, header, signal) {
    const { mine, letGo } = await this.#claim(signal);
    let acknowledged = false;
    try {
      acknowledged = await this.#deliverClaimed(stream, header, AbortSignal.any([signal, mine.signal]));
    } finally {
      this.#release(mine, letGo, acknowledged);
    }
  }

  /// Forgets the exchange: one nobody came back for, one whose outcome has
  /// been taken, or one on a link that is closing.
  expire() {
    if (this.#expired) return;
    this.#expired = true;
    this.#forget();
    clearTimeout(this.#timer);
    if (this.#delivering) {
      // It lets go when aborted, and it finishes, because it owns the writing.
      this.#delivering.abort(new LinkError("the exchange was forgotten"));
      return;
    }
    this.#finish();
  }

  async #deliverClaimed(stream, header, signal) {
    const along = { signal, restart() {} };
    const pipelined = (header.flags & ExchangeFlags.Pipelined) !== 0;
    if (pipelined) {
      // Sent before this end was asked where to start, which only ever happens
      // from the very beginning; what overlaps is dropped.
      await this.request.readBody(stream, 0, { signal });
    }

    const offset = this.request.bytesReceived;
    await writeFrame(stream, FrameStatus.Ok, this.#id, encodeOffset(offset), along);
    if (!pipelined) await this.request.readBody(stream, offset, { signal });

    const outcome = this.#outcomeOnce();
    if (header.flags & ExchangeFlags.AckOnDelivery) {
      // A notification: its sender waits for nothing the handler does.
      await writeFrame(stream, FrameStatus.Ok, this.#id, encodeOffset(this.request.bytesReceived), along);
      return this.#acknowledged(stream, signal);
    }

    const { refusal, answer } = await withAbort(outcome, signal);
    if (refusal !== null) {
      await writeFrame(stream, FrameStatus.Failed, this.#id, utf8(refusal), along);
      return false;
    }
    if (!(header.flags & ExchangeFlags.Answer)) {
      await writeFrame(stream, FrameStatus.Ok, this.#id, encodeOffset(this.request.bytesReceived), along);
      return this.#acknowledged(stream, signal);
    }

    const content = answer ?? LinkContent.empty;
    await writeFrame(stream, FrameStatus.Ok, this.#id, encodeAnswerHeader(content), along);
    // Content in a page is always in memory, so an answer can always be read
    // again from wherever the sender says it got to.
    await writeBlocks(stream, content.bytes, Math.min(header.answerOffset, content.length), along);
    this.#announceAnswered(content);
    return this.#acknowledged(stream, signal);
  }

  // Once per exchange, not once per attempt: an answer resumed on a later
  // session is written again, but it is still one request answered.
  #announceAnswered(content) {
    if (this.#announced) return;
    this.#announced = true;
    this.#answered(this.request, content);
  }

  /// Whether the sender says it has the outcome. Only then is the exchange let
  /// go of at once; without it every notification would be held for the whole
  /// retention window after it was delivered.
  async #acknowledged(stream, signal) {
    const patience = new IdleTimeout(this.#ackPatienceMs);
    try {
      const { tag } = await readFrame(stream, { signal: AbortSignal.any([signal, patience.signal]), restart() {} }, { limit: 0 });
      return tag === FrameStatus.Ok;
    } catch (error) {
      if (signal.aborted) throw error;
      return false;
    } finally {
      patience.stop();
    }
  }

  /// The handler's run, started the first time the content is whole and
  /// joined by every attempt after that.
  #outcomeOnce() {
    this.#outcome ??= (async () => {
      try {
        await this.request.complete;
        const answer = await this.#run(this.request);
        return { refusal: null, answer: this.#flags & ExchangeFlags.Answer ? answer : null };
      } catch (error) {
        // The application refused it, or the content was broken. That is an
        // answer, and the sender hears it rather than retrying something that
        // would fail the same way — except a notification's sender, which was
        // acknowledged before the handler finished, so the page is told instead.
        if (this.#flags & ExchangeFlags.AckOnDelivery) this.#unheardFailure(error);
        return { refusal: error?.message ?? String(error), answer: null };
      } finally {
        // A resumed attempt needs only the outcome and how much arrived, and
        // an unacknowledged exchange is kept for the whole retention window.
        this.request = this.request.receipt();
      }
    })();
    return this.#outcome;
  }

  /// Makes this attempt the one delivering the exchange, taking over from one
  /// still unwinding. The newest wins because the sender runs one attempt at a
  /// time: a new one means the old one is abandoned.
  async #claim(signal) {
    if (this.#expired) throw new LinkError(`the exchange was not resumed within ${this.#retentionMs} ms`);
    const mine = new AbortController();
    const letGo = new Deferred();
    const previous = this.#delivering;
    const previousLetGo = this.#letGo;
    this.#delivering = mine;
    this.#letGo = letGo.promise;
    clearTimeout(this.#timer);

    previous?.abort(new LinkError("a newer attempt took the exchange over"));
    try {
      await withAbort(previousLetGo, AbortSignal.any([signal, mine.signal]));
    } catch (error) {
      this.#release(mine, letGo, false);
      throw error;
    }
    return { mine, letGo };
  }

  #release(mine, letGo, acknowledged) {
    const held = this.#delivering === mine;
    if (held) this.#delivering = null;
    if (held && !this.#expired && !acknowledged) this.#schedule();
    letGo.resolve();

    if (acknowledged) this.expire();
    else if (held && this.#expired) this.#finish();
  }

  #schedule() {
    clearTimeout(this.#timer);
    this.#timer = setTimeout(() => this.expire(), this.#retentionMs);
    // Ten minutes of nothing must not be what keeps a Node process alive after
    // everything else has finished; a browser's timers have no such thing.
    this.#timer.unref?.();
  }

  #finish() {
    if (this.#finished) return;
    this.#finished = true;
    this.request.fail(new LinkError(`the content stopped and was not resumed within ${this.#retentionMs} ms`));
  }
}

/// The exchanges this browser is receiving. `ExchangeRegistry.cs` is the .NET half.
export class ExchangeRegistry {
  #exchanges = new Map();
  #requests;
  #notifications;
  #transfers;
  #retentionMs;
  #ackPatienceMs;
  #answered;
  #unheardFailure;

  /// Each handler finder is read on every arrival, so a page that sets its
  /// handler after connecting still answers. Each returns `(content) =>
  /// LinkContent`, or nothing when nothing takes that kind.
  constructor({
    requests,
    notifications = requests,
    transfers,
    retentionMs = EXCHANGE_RETENTION_MS,
    ackPatienceMs,
    answered = () => {},
    unheardFailure = () => {},
  }) {
    this.#answered = answered;
    this.#unheardFailure = unheardFailure;
    this.#requests = requests;
    this.#notifications = notifications;
    this.#transfers = transfers;
    this.#retentionMs = retentionMs;
    this.#ackPatienceMs = ackPatienceMs;
  }

  /// Takes one session's attempt at an exchange.
  async deliver(exchange, headerPayload, stream, signal) {
    const header = this.#readHeader(headerPayload);
    if (header instanceof LinkError) {
      // Every attempt would be read the same way, so the sender hears that
      // rather than a broken stream it would take for a broken session.
      await this.#refuse(stream, exchange, header.message, announcesPipelinedContent(headerPayload), signal);
      return;
    }
    const transfer = (header.flags & ExchangeFlags.Transfer) !== 0;
    const run = this.#handlerFor(header.flags);
    if (!run) {
      // Refused rather than left to time out, and as an answer rather than an
      // error, so the sender stops instead of retrying into a machine that will
      // never take it.
      const reason = transfer ? "the other machine is not receiving transfers" : "the other machine is not handling requests";
      await this.#refuse(stream, exchange, reason, (header.flags & ExchangeFlags.Pipelined) !== 0, signal);
      return;
    }

    const key = hex(exchange);
    let incoming = this.#exchanges.get(key);
    if (!incoming) {
      const created = new IncomingExchange({
        id: exchange,
        header,
        retentionMs: this.#retentionMs,
        ackPatienceMs: this.#ackPatienceMs,
        answered: this.#answered,
        unheardFailure: this.#unheardFailure,
        // Only that one: an id forgotten and then sent again is a new exchange
        // under the same key, and must not be dropped with it.
        forget: () => {
          if (this.#exchanges.get(key) === created) this.#exchanges.delete(key);
        },
      });
      created.start(run);
      this.#exchanges.set(key, created);
      incoming = created;
    }
    await incoming.deliver(stream, header, signal);
  }

  /// How many exchanges are being held, for a test to see them let go of.
  get size() {
    return this.#exchanges.size;
  }

  /// Ends every exchange still in flight, on a link that is closing.
  expireAll() {
    for (const exchange of [...this.#exchanges.values()]) exchange.expire();
  }

  /// The header, or the `LinkError` saying why it cannot be read.
  #readHeader(payload) {
    try {
      return decodeExchangeHeader(payload);
    } catch (error) {
      if (error instanceof LinkError) return error;
      throw error;
    }
  }

  /// Content already on its way is read first, or the refusal would sit behind
  /// it unread while its sender waits for room to write the rest.
  async #refuse(stream, exchange, reason, contentFollows, signal) {
    if (contentFollows) await drainBlocks(stream, signal);
    await writeFrame(stream, FrameStatus.Failed, exchange, utf8(reason), { signal, restart() {} });
  }

  #handlerFor(flags) {
    if (flags & ExchangeFlags.Transfer) return this.#transfers();
    if (flags & ExchangeFlags.AckOnDelivery) return this.#notifications() ?? this.#requests();
    return this.#requests();
  }
}
