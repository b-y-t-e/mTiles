// One session, for as long as it lasts.
//
// Everything here is about a session that is up: serving what the host opens,
// one attempt at an exchange, and the heartbeat that decides the session has
// quietly died. Nothing here reconnects — that is `link.js`, and keeping the
// two apart is what makes either of them understandable. `LinkSession.cs` is
// the .NET half.

import { concat, delay, str, utf8 } from "./bytes.js";
import { LinkTimeoutError, RemoteHandlerError } from "./errors.js";
import { IncomingContent } from "./incoming-content.js";
import { asContent } from "./link-content.js";
import {
  FrameKind,
  FrameStatus,
  THIS_CLIENT,
  decodeCapabilities,
  encodeCapabilities,
  newExchange,
  readFrame,
  writeFrame,
} from "./link-frame.js";
import {
  ChannelCloseReason,
  ChannelReader,
  ChannelWriter,
  decodeChannelName,
  encodeChannelName,
} from "./link-channel.js";
import { IdleTimeout } from "./idle-timeout.js";
import { LinkStream } from "./link-stream.js";

export class LinkSession {
  #connection;
  #handler;
  #notifyHandler;
  #channels;
  #streams;
  #ledger;
  #options;
  #emit;
  #answering = new Set();
  #open = new Set();
  #beating = false;
  #lastMoved = Date.now();
  #exchanges;
  #capabilities = null;
  // Aborted the moment the session stops carrying anything, which is what an
  // exchange attempt riding on it has to hear.
  #alive = new AbortController();

  /// @param handler read on every inbound request rather than captured, so an
  ///   application that sets its handler after connecting still answers.
  /// @param channels finds what serves a channel of a given name, or nothing
  ///   when this browser is not listening for that name. Read on arrival for
  ///   the same reason the request handler is.
  /// @param streams the same, for streams.
  /// @param ledger shared with every other session of the same link, because
  ///   that is where a request retried after this session dies will arrive.
  /// @param exchanges shared for the same reason, and for longer: an exchange
  ///   resumed on a later session carries on from what arrived on this one.
  constructor({ connection, handler, notifyHandler, channels, streams, ledger, exchanges = null, options, emit }) {
    this.#connection = connection;
    this.#exchanges = exchanges;
    this.#handler = handler;
    this.#notifyHandler = notifyHandler;
    this.#channels = channels ?? (() => null);
    this.#streams = streams ?? (() => null);
    this.#ledger = ledger;
    this.#options = options;
    this.#emit = emit;
  }

  get closed() {
    return this.#connection.closed;
  }

  /// Answers what the host opens, and keeps a heartbeat going so that a
  /// session that has quietly died is noticed. Writing into a dead relayed
  /// session succeeds, so silence is the only symptom there is. Resolves when
  /// the session ends, which is what the link above waits on to reconnect.
  async serve() {
    this.#beating = true;
    this.#startHeartbeat();
    try {
      for (;;) {
        this.#answer(await this.#connection.accepted.next());
      }
    } finally {
      this.#beating = false;
      this.#alive.abort(new Error("the session ended"));
    }
  }

  /// What one exchange attempt needs of this session. `ExchangeAttempt.cs`
  /// calls the same thing a carrier.
  get carrier() {
    return {
      alive: this.#alive.signal,
      openStream: () => this.#watch(this.#connection.openStream()),
      failUnlessBusy: (reason, silence) => this.failUnlessBusy(reason, silence),
    };
  }

  /// Ends the session for a failed attempt, unless what failed was one
  /// exchange's silence on a session still moving other bytes: that says
  /// nothing about the host, and every other exchange on the session would be
  /// ended for it.
  failUnlessBusy(reason, silence) {
    if (silence && this.#movedWithin(this.#options.requestTimeout)) return;
    this.close(new Error(reason));
  }

  /// What the host can take, asked once per session and only when something
  /// needs to know. `PeerCapabilitiesQuery.cs` is the .NET half.
  async capabilities() {
    this.#capabilities ??= this.#askCapabilities();
    try {
      return await this.#capabilities;
    } catch (error) {
      // Asked again rather than remembered: a failure remembered would fail
      // every exchange on this session for the rest of its life.
      this.#capabilities = null;
      throw error;
    }
  }

  // A ping outpaced by the session's own bytes is asked again rather than
  // failed: on a saturated link that is the normal case, and failing it would
  // stop every exchange from starting until the link went quiet.
  async #askCapabilities() {
    for (;;) {
      try {
        return await this.#ping();
      } catch (error) {
        if (error instanceof LinkTimeoutError && this.#movedWithin(this.#options.requestTimeout) && !this.closed) {
          continue;
        }
        throw error;
      }
    }
  }

  /// Opens a channel on this session, once the host has said it has a handler
  /// for the name. The channel it returns ends with this session and is not
  /// resumed on the next one, which is the whole of what a channel promises.
  ///
  /// @throws {RemoteHandlerError} if the host is not listening for that name.
  async openChannel(name) {
    const stream = await this.#openNamed(FrameKind.Channel, encodeChannelName(name));
    // From here the channel owns the stream, and that is what it sends its
    // frames on.
    return this.#hold(new ChannelWriter(name, stream));
  }

  /// Opens a two-way stream on this session, once the host has said it has a
  /// handler for the name. Like a channel it ends with this session.
  ///
  /// @throws {RemoteHandlerError} if the host is not listening for that name.
  async openStream(name) {
    const transport = await this.#openNamed(FrameKind.Stream, encodeChannelName(name, "stream"));
    return new LinkStream(name, transport, { patienceMs: this.#options.requestTimeout });
  }

  // A channel and a stream open the same way: the name, and an answer saying
  // whether anything takes it.
  async #openNamed(kind, encodedName) {
    const idle = new IdleTimeout(this.#options.requestTimeout);
    let stream;
    try {
      stream = this.#watch(this.#connection.openStream());
      idle.restart();
      await writeFrame(stream, kind, newExchange(), encodedName, idle);

      const answer = await readFrame(stream, idle);
      if (answer.tag === FrameStatus.Failed) throw new RemoteHandlerError(str(answer.payload));
      return stream;
    } catch (error) {
      await stream?.close().catch(() => {});
      throw idle.expired ? new LinkTimeoutError(`the host sent nothing for ${idle.limitMs} ms`) : error;
    } finally {
      idle.stop();
    }
  }

  /// One attempt at an exchange, on this session. The exchange id comes from
  /// the caller because it outlives any one session: a retry carries the id of
  /// the original, which is how the far end recognises it.
  ///
  /// Failing does not necessarily end the session — see the silence below — so
  /// a caller that wants to know must ask `closed` rather than assume.
  async exchange(kind, exchange, payload, { expectAnswer = true } = {}) {
    // Silence rather than duration, so that a payload too large to move in
    // one window is not confused with a peer that has gone away. The .NET
    // end measures the same thing the same way; see IdleTimeout.cs.
    const idle = new IdleTimeout(this.#options.requestTimeout);
    // openStream() belongs inside the try: the session can close between the
    // caller asking for it and here, and that must be reported like any other
    // broken session rather than thrown at the application.
    let stream;
    try {
      stream = this.#watch(this.#connection.openStream());
      idle.restart();
      await writeFrame(stream, kind, exchange, payload, idle);
      // A notification is not answered — the far end runs the handler and
      // closes the stream — so waiting for one would look exactly like a peer
      // that has gone away.
      if (!expectAnswer) return new Uint8Array(0);

      const answer = await readFrame(stream, idle);
      if (answer.tag === FrameStatus.Failed) {
        // The handler on the far end failed. That is an answer, not a broken
        // session: retrying would only run it again.
        throw new RemoteHandlerError(str(answer.payload));
      }
      return answer.payload;
    } catch (error) {
      if (error instanceof RemoteHandlerError) throw error;

      // Silence during a request says nothing about the session: a handler on
      // the far end is allowed to take longer than one request window, and
      // condemning the session for it would break every other exchange
      // sharing it and cost a whole reconnect. Deciding that the peer is gone
      // is the heartbeat's job — its ping is answered by the frame loop, so
      // its silence really does mean silence. The caller asks again on this
      // same session and spends another whole window waiting, so this is
      // patience rather than a spin.
      if (idle.expired) throw new LinkTimeoutError(`the host sent nothing for ${idle.limitMs} ms`);

      this.close(error);
      throw error;
    } finally {
      idle.stop();
      await stream?.close().catch(() => {});
    }
  }

  /// Lets the answers already produced leave before the session is taken
  /// down. A handler returns its answer well before the answer is on the wire
  /// — sealing a record is asynchronous, and the socket sends what it was
  /// given later still — and closing in that window turns a delivered answer
  /// into silence the host can only resolve by timing the request out.
  ///
  /// Bounded, because a handler of the page's own is allowed to hang and
  /// closing must still happen.
  async drain(timeout) {
    // The channels first, and without waiting for them: a channel is not
    // durable and nothing is owed to it, while an answer already produced is
    // — and a handler sitting inside a channel's frames would otherwise hold
    // the whole drain open until its timeout.
    await this.#endChannels("the link was closed");
    if (this.#answering.size) {
      await Promise.race([Promise.allSettled([...this.#answering]), delay(timeout)]);
    }
    // Written is not sent: waiting only for the handlers leaves the sealed
    // records sitting in the socket's buffer, which closing then throws away.
    await this.#connection.flush(timeout);
  }

  close(error = new Error("the session was closed")) {
    this.#beating = false;
    this.#alive.abort(new Error(error.message));
    this.#endChannels(error.message).catch(() => {});
    this.#connection.close(error);
  }

  // Held so that the session can end them: a channel that outlived the
  // session carrying it would be exactly the durability a channel does not
  // promise, and its reader would be parked on a stream nothing can arrive on.
  #hold(channel) {
    // Pruned as new ones arrive rather than through `onclose`, which belongs
    // to the application: a session that opens thousands of channels must not
    // keep every closed one, and must not lose the page's own listener either.
    for (const held of this.#open) if (!held.open) this.#open.delete(held);
    this.#open.add(channel);
    return channel;
  }

  async #endChannels(detail) {
    await Promise.allSettled([...this.#open].map((channel) => channel.close(ChannelCloseReason.SessionEnded, detail)));
    this.#open.clear();
  }

  #answer(accepted) {
    // The host's ping is not counted, for the reason this client's own is not:
    // it must never excuse a ping's silence.
    const stream = this.#watch(accepted, (tag) => tag !== FrameKind.Ping);
    // Held so that drain() can wait for it.
    const answering = (async () => {
      try {
        const { tag, exchange, payload } = await readFrame(stream);
        switch (tag) {
          case FrameKind.Ping:
            // Answered with what this client can take. An older host reads
            // the answer and ignores what is in it, as it always has.
            await writeFrame(stream, FrameStatus.Ok, exchange, encodeCapabilities(THIS_CLIENT));
            break;
          case FrameKind.Notify: {
            // No answer, for the same reason this end does not wait for one.
            const handler = this.#notifyHandler() ?? this.#handler();
            await handler?.(str(payload), payload, IncomingContent.whole(exchange, payload));
            break;
          }
          case FrameKind.Exchange:
            // Not through the ledger: what makes an exchange arrive once is the
            // registry, which keeps its content, its handler's run and its
            // answer across sessions.
            if (!this.#exchanges) {
              await writeFrame(stream, FrameStatus.Failed, exchange, utf8("this browser takes no exchanges"));
              break;
            }
            await this.#exchanges.deliver(exchange, payload, stream, this.#alive.signal);
            break;
          case FrameKind.Channel:
            await this.#serveChannel(stream, exchange, payload);
            break;
          case FrameKind.Stream:
            await this.#serveStream(stream, exchange, payload);
            break;
          case FrameKind.Request: {
            // Through the ledger, so that a request the host is retrying after
            // a session died is answered from what its first arrival produced
            // rather than run a second time.
            const answer = await this.#ledger.answer(exchange, () => this.#runHandler(exchange, payload));
            await writeFrame(stream, answer.status, exchange, answer.payload);
            // Announced here rather than where the handler returned, so that
            // it says what it appears to say: the answer left this end. A
            // write that throws reaches "answer-failed" below instead, which
            // it could not if the handler had already claimed success.
            // The same shape an exchange's answer is announced in, so a page
            // reads one event whichever frame the host happened to send.
            this.#emit("answered", { name: "", requestLength: payload.length, length: answer.payload.length });
            break;
          }
          default:
            await writeFrame(stream, FrameStatus.Failed, exchange, utf8(`unexpected frame ${tag}`));
        }
      } catch (error) {
        // The stream went away mid-exchange, which the session notices. The
        // far end does not: an answer that never left looks to it like a peer
        // that said nothing, so this is reported rather than only dropped.
        this.#emit("answer-failed", error);
      } finally {
        await stream?.close().catch(() => {});
      }
    })();
    this.#answering.add(answering);
    answering.finally(() => this.#answering.delete(answering)).catch(() => {});
  }

  /// Hands a channel the host opened to whatever is listening for the name,
  /// and refuses it outright when nothing is: swallowing the frames into a
  /// browser that will never read them is the one answer that helps nobody.
  ///
  /// The stream is the caller's to close, which it does once this returns —
  /// so a handler that means to keep receiving stays inside `read()`.
  async #serveChannel(stream, exchange, payload) {
    const name = decodeChannelName(payload);
    const handler = this.#channels(name);
    if (!handler) {
      await writeFrame(stream, FrameStatus.Failed, exchange, utf8(`this browser has no "${name}" channel`));
      return;
    }

    await writeFrame(stream, FrameStatus.Ok, exchange, new Uint8Array(0));
    const channel = this.#hold(new ChannelReader(name, stream));
    try {
      await handler(channel);
    } finally {
      await channel.close(ChannelCloseReason.LocalClosed, "the handler returned");
    }
  }

  /// Hands a stream the host opened to whatever is listening for the name. The
  /// stream ends when the handler returns — closed, which ends this end's
  /// writes cleanly — and is aborted if the handler throws: a clean end would
  /// tell the host that everything it was owed arrived.
  async #serveStream(transport, exchange, payload) {
    const name = decodeChannelName(payload, "stream");
    const handler = this.#streams(name);
    if (!handler) {
      await writeFrame(transport, FrameStatus.Failed, exchange, utf8(`this browser has no "${name}" stream`));
      return;
    }

    await writeFrame(transport, FrameStatus.Ok, exchange, new Uint8Array(0));
    const stream = new LinkStream(name, transport, { patienceMs: this.#options.requestTimeout });
    try {
      await handler(stream);
    } catch (error) {
      console.warn(`the "${name}" stream handler threw`, error);
      await stream.abort();
    } finally {
      await stream.close();
    }
  }

  /// Runs the application's handler and turns whatever it does into an answer.
  /// Everything it can produce, a refusal included, is an answer the ledger
  /// remembers: a retry is told the same thing rather than running again.
  async #runHandler(exchange, payload) {
    try {
      const handler = this.#handler();
      if (!handler) throw new Error("this browser answers no requests");
      const answer = await handler(str(payload), payload, IncomingContent.whole(exchange, payload));
      return { status: FrameStatus.Ok, payload: asContent(answer).bytes };
    } catch (error) {
      // Application code threw. The host is waiting and deserves to be told
      // why rather than left to time out, and this link must survive its own
      // handler's bugs.
      return { status: FrameStatus.Failed, payload: utf8(error.message) };
    }
  }

  /// One ping, on a stream nobody counts as movement: the ping itself must
  /// never be taken for the other bytes that would excuse its silence.
  async #ping() {
    const idle = new IdleTimeout(this.#options.requestTimeout);
    let stream;
    try {
      stream = this.#connection.openStream();
      await writeFrame(stream, FrameKind.Ping, newExchange(), new Uint8Array(0), idle);
      return decodeCapabilities((await readFrame(stream, idle)).payload);
    } catch (error) {
      throw idle.expired ? new LinkTimeoutError(`the host sent nothing for ${idle.limitMs} ms`) : error;
    } finally {
      idle.stop();
      await stream?.close().catch(() => {});
    }
  }

  /// Whether bytes moved on any stream of this session within `windowMs`.
  #movedWithin(windowMs) {
    return Date.now() - this.#lastMoved < windowMs;
  }

  /// The same stream, telling this session whenever bytes move on it.
  ///
  /// Writes count only once the host has sent something on this same stream:
  /// relay1 credit is per stream, so every new one has a window a host that has
  /// gone never granted, and sending into it would look like traffic.
  /// `countsStreamStartingWith` decides from the first byte the host sends
  /// whether the stream counts at all. `MovingStream.cs` is the .NET half.
  #watch(stream, countsStreamStartingWith = () => true) {
    let heardFromPeer = false;
    let ignored = false;
    const moved = () => {
      if (!ignored) this.#lastMoved = Date.now();
    };
    const heard = (chunk) => {
      if (!chunk.length) return;
      if (!heardFromPeer) {
        heardFromPeer = true;
        ignored = !countsStreamStartingWith(chunk[0]);
      }
      moved();
    };
    return {
      write: async (payload, signal) => {
        await stream.write(payload, signal);
        if (heardFromPeer) moved();
      },
      read: async (signal, limit) => {
        const chunk = await stream.read(signal, limit);
        heard(chunk);
        return chunk;
      },
      // A chunk at a time, as the stream underneath reads it, so that one
      // large frame arriving slowly still counts as movement all the way in.
      readExactly: async (count, signal) => {
        const parts = [];
        let have = 0;
        while (have < count) {
          const chunk = await stream.read(signal, count - have);
          if (!chunk.length) throw new Error(`the peer stopped after ${have} of ${count} bytes`);
          heard(chunk);
          parts.push(chunk);
          have += chunk.length;
        }
        return concat(...parts);
      },
      finish: () => stream.finish(),
      close: () => stream.close(),
    };
  }

  #startHeartbeat() {
    (async () => {
      while (this.#beating && !this.closed) {
        await delay(this.#options.heartbeatInterval);
        if (!this.#beating || this.closed) return;

        try {
          const said = await this.#ping();
          // A heartbeat that got there first saves the next exchange its ping.
          this.#capabilities ??= Promise.resolve(said);
        } catch (error) {
          // The one exchange whose silence does condemn the session: a ping is
          // answered by the peer's frame loop rather than by application code,
          // so nothing legitimate slows it — except the session's own bytes.
          // On a link saturated by a large message the answer queues behind
          // them, and those bytes are as good a sign of life as the answer.
          // Closing here is what used to end the upload that kept it busy.
          if (error instanceof LinkTimeoutError && this.#movedWithin(this.#options.requestTimeout)) continue;
          this.close(error);
        }
      }
    })();
  }
}

// Re-exported because it was raised from here before the errors had a module
// of their own, and applications import it from this one.
export { RemoteHandlerError };
