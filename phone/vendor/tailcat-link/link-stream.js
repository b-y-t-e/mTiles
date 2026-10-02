// The fourth shape a link carries: a two-way stream of bytes, each direction
// ended on its own, gone with the session that carries it.
//
// `docs/streams.md` is the specification and `PairedLinkStream.cs` the .NET
// half. On the wire it is a channel's framing both ways — length-prefixed
// pieces and a zero length that ends one direction — plus one length no piece
// can have, which abandons the lot. What makes the endings tell the truth is
// what each end does with the stream underneath: after ending its writes an end
// keeps it open for as long as it still reads, and after aborting it keeps it
// open until the other end has read the abort and let go.

import { Deferred, concat, readU32be, u32be, withTimeout } from "./bytes.js";
import { LinkStreamError } from "./errors.js";
import { PeerReleasedStreamError } from "./relay1.js";

/// The length that abandons a stream instead of carrying a piece.
export const ABORT_MARKER = 0xffffffff;

/// The most a piece carries; the same as a transfer's block, as on the .NET side.
export const PIECE_BYTES = 256 * 1024;

/// How a stream ended, when it did not end cleanly. `LinkStreamEnding` in .NET.
export const StreamEnding = {
  /// The other end closed the stream while this end was still writing.
  PeerClosed: "peer-closed",
  /// The other end abandoned it: it aborted, its handler threw, or its bytes
  /// stopped part-way. What was read may be incomplete.
  PeerAborted: "peer-aborted",
  /// This end aborted it.
  Aborted: "aborted",
  /// The session carrying it died. A stream is not resumed.
  SessionEnded: "session-ended",
};

export class LinkStream {
  #name;
  #transport;
  #patienceMs;
  #failure = null;
  #writesCompleted = false;
  #midPiece = false;
  #closed = false;
  #released = false;
  #sending = Promise.resolve();
  #stopWriting = new AbortController();
  #stopReading = new AbortController();
  #readAhead;
  // Resolved when a write hears the other end let go of the transport: nothing
  // more can arrive, so the read-ahead stops waiting for the page and reads on
  // to the marker saying which ending it was.
  #peerLetGo = new Deferred();
  // Resolved once the read side knows how the stream ended, or that it never will.
  #endingKnown = new Deferred();

  // What the read-ahead has handed over and nobody has read yet, at most one
  // piece: `#taken` resolves when a read empties it, which is what paces the
  // other end.
  #pending = null;
  #taken = null;
  #arrived = new Deferred();
  #peerEnded = false;

  /// @param transport the session's stream, already past the frame that opened it.
  /// @param patienceMs how long an ending waits to be heard.
  constructor(name, transport, { patienceMs }) {
    this.#name = name;
    this.#transport = transport;
    this.#patienceMs = patienceMs;
    this.#readAhead = this.#runReadAhead();
  }

  get name() {
    return this.#name;
  }

  /// `{ ending, detail }` once it has ended other than cleanly, and null otherwise.
  get failure() {
    return this.#failure && { ending: this.#failure.ending, detail: this.#failure.message };
  }

  /// Resolves with the next bytes, in order, or with an empty array once the
  /// other end has ended its writes and everything it sent has been read — the
  /// clean end, and the only one. Anything else rejects with a `LinkStreamError`
  /// whose `ending` says what happened.
  async read() {
    for (;;) {
      // Inside the loop: a read waiting when the stream is closed wakes here.
      if (this.#closed) throw new LinkStreamError(StreamEnding.Aborted, `the "${this.#name}" stream was closed`);
      if (this.#failure?.ending === StreamEnding.Aborted) throw this.#copy();
      if (this.#pending) {
        const bytes = this.#pending;
        this.#pending = null;
        this.#taken?.resolve();
        return bytes;
      }
      if (this.#peerEnded) return new Uint8Array(0);
      if (this.#failure) throw this.#copy();
      await this.#arrived.promise;
    }
  }

  /// Sends bytes, in pieces, queued behind any write still going out.
  async write(bytes) {
    if (this.#closed) throw new LinkStreamError(StreamEnding.Aborted, `the "${this.#name}" stream was closed`);
    if (!bytes.length) return; // a piece of no bytes is what ends a direction on the wire
    const sent = this.#sending.then(async () => {
      this.#throwUnlessWritable();
      for (let at = 0; at < bytes.length; ) {
        const piece = bytes.subarray(at, Math.min(at + PIECE_BYTES, bytes.length));
        await this.#writeMarked(concat(u32be(piece.length), piece));
        at += piece.length;
      }
    });
    this.#sending = sent.catch(() => {});
    return this.#failingFromTransport(sent);
  }

  /// Says there is nothing more to write, while this end can still read: the
  /// half-close of a socket. Calling it again does nothing.
  async finish() {
    const sent = this.#sending.then(async () => {
      if (this.#writesCompleted) return;
      this.#throwUnlessWritable();
      await this.#writeMarked(u32be(0));
      this.#writesCompleted = true;
    });
    this.#sending = sent.catch(() => {});
    return this.#failingFromTransport(sent);
  }

  /// Abandons the stream both ways; the other end is told it did not finish.
  async abort() {
    if (this.#closed) return;
    if (this.#fail(StreamEnding.Aborted, `this end aborted the "${this.#name}" stream`).ending === StreamEnding.Aborted) {
      await this.#sayAborted();
    }
    await this.#letGo();
  }

  /// Ends this end's writes cleanly and stops reading, as closing a socket
  /// does. Past the patience it was given, the stream is abandoned instead.
  async close() {
    if (this.#closed) return;
    if (!this.#failure && !this.#writesCompleted) {
      try {
        await withTimeout(this.finish(), this.#patienceMs, "the end marker");
      } catch (error) {
        if (!(error instanceof LinkStreamError)) {
          const detail = `the "${this.#name}" stream could not be ended cleanly within ${this.#patienceMs} ms`;
          if (this.#fail(StreamEnding.Aborted, detail).ending === StreamEnding.Aborted) await this.#sayAborted();
        }
      }
    }
    this.#closed = true;
    await this.#letGo();
  }

  // Marks a piece as under way until it is written: one cut part-way leaves the
  // stream out of step, and no marker can follow it.
  async #writeMarked(bytes) {
    this.#midPiece = true;
    await this.#transport.write(bytes, this.#stopWriting.signal);
    this.#midPiece = false;
  }

  async #failingFromTransport(sent) {
    try {
      await sent;
    } catch (error) {
      if (error instanceof LinkStreamError || error instanceof TypeError) throw error;
      if (error instanceof PeerReleasedStreamError) throw await this.#failFromPeerLettingGo();
      this.#fail(StreamEnding.SessionEnded, `the "${this.#name}" stream ended with its session: ${error.message}`);
      throw this.#copy();
    }
  }

  // The transport says only that the other end let go, the same for a close
  // and an abort. Which it was is in the marker that end wrote first, maybe
  // behind pieces the page has not taken: the read-ahead is let past the
  // pacing and asked, and one that cannot say leaves the stream abandoned.
  async #failFromPeerLettingGo() {
    if (!this.#failure) {
      this.#peerLetGo.resolve();
      try {
        await withTimeout(this.#endingKnown.promise, this.#patienceMs, "the ending");
      } catch {
        // The read-ahead is somewhere no marker will reach it from.
      }
      this.#fail(StreamEnding.PeerAborted, `the other machine let go of the "${this.#name}" stream without saying how it ended`);
    }
    return this.#copy();
  }

  #throwUnlessWritable() {
    if (this.#failure) throw this.#copy();
    if (this.#writesCompleted) throw new TypeError(`writes on the "${this.#name}" stream were already completed`);
  }

  async #sayAborted() {
    try {
      await withTimeout(
        (async () => {
          await this.#sending;
          if (this.#midPiece) return;
          await this.#transport.write(u32be(ABORT_MARKER));
          // The read-ahead discards from here, and ends when the other end
          // lets go — which is it having read the marker.
          await this.#readAhead;
        })(),
        this.#patienceMs,
        "the other end",
      );
    } catch {
      // Not heard in time, or the session went: letting go says the rest.
    }
  }

  async #letGo() {
    this.#stopWriting.abort(new Error("the stream was let go of"));
    this.#stopReading.abort(new Error("the stream was let go of"));
    this.#arrived.resolve();
    this.#taken?.resolve();
    await this.#readAhead;
    await this.#release();
  }

  async #release() {
    if (this.#released) return;
    this.#released = true;
    await this.#transport.close().catch(() => {});
  }

  async #runReadAhead() {
    const signal = this.#stopReading.signal;
    // The last piece handed over, while the page has yet to take it. The next
    // header is read meanwhile — that is how an end that only writes still
    // hears an abort — but no piece's body until it is taken.
    let handing = Promise.resolve();
    try {
      for (;;) {
        const header = await this.#readHeader(signal);
        if (!header) {
          if (this.#peerEnded) this.#fail(StreamEnding.PeerClosed, `the other machine closed the "${this.#name}" stream`);
          else this.#fail(StreamEnding.PeerAborted, `the other machine abandoned the "${this.#name}" stream`);
          return;
        }

        const length = readU32be(header);
        if (length === ABORT_MARKER || this.#peerEnded) {
          this.#fail(
            StreamEnding.PeerAborted,
            length === ABORT_MARKER
              ? `the other machine aborted the "${this.#name}" stream`
              : `the other machine sent more on the "${this.#name}" stream after ending it`,
          );
          // Letting go is what tells the aborting end it was heard.
          await this.#release();
          return;
        }
        if (length === 0) {
          // Not waiting for the page: reads hand over what is pending before
          // the end, and the transport ending after the marker is how a writer
          // hears the close, however much is unread.
          this.#peerEnded = true;
          this.#arrived.resolve();
          continue;
        }

        for (let remaining = length; remaining > 0; ) {
          // Waits for the page to read: this is where the other end is paced.
          // Letting go and aborting both resolve it, so it cannot hold either up.
          // Once the other end has let go, waiting protects nothing and only
          // delays the marker saying how it ended; the rest queues behind.
          await Promise.race([handing, this.#peerLetGo.promise]);
          if (signal.aborted) return;
          const part = await this.#transport.read(signal, Math.min(remaining, PIECE_BYTES));
          if (!part.length) {
            this.#fail(
              StreamEnding.PeerAborted,
              `the other machine abandoned the "${this.#name}" stream part-way through a write`,
            );
            return;
          }
          remaining -= part.length;
          // Discarded once this end has aborted, until the other end lets go.
          if (this.#failure?.ending === StreamEnding.Aborted) continue;
          this.#pending = this.#pending ? concat(this.#pending, part) : part;
          this.#taken = new Deferred();
          this.#arrived.resolve();
          this.#arrived = new Deferred();
          handing = this.#taken.promise;
        }
      }
    } catch (error) {
      if (signal.aborted) return; // this end let go; whoever did has said why
      this.#fail(StreamEnding.SessionEnded, `the "${this.#name}" stream ended with its session: ${error.message}`);
    } finally {
      this.#endingKnown.resolve();
    }
  }

  // Null on a clean end before any byte of a header.
  async #readHeader(signal) {
    const first = await this.#transport.read(signal, 4);
    if (!first.length) return null;
    return first.length === 4 ? first : concat(first, await this.#transport.readExactly(4 - first.length, signal));
  }

  // The first ending wins; the one returned is whichever that was.
  #fail(ending, detail) {
    this.#failure ??= new LinkStreamError(ending, detail);
    this.#stopWriting.abort(this.#failure);
    this.#arrived.resolve();
    if (this.#failure.ending === StreamEnding.Aborted) {
      // Nobody will read what is held, and the read-ahead must go on to
      // discard until the other end lets go.
      this.#pending = null;
      this.#taken?.resolve();
    }
    return this.#failure;
  }

  #copy() {
    return new LinkStreamError(this.#failure.ending, this.#failure.message);
  }
}
