// The relay1 transport: streams carried by the relay itself.
//
// Everything QUIC provides for a .NET peer is here in its smallest honest
// form. Encryption from the ephemeral exchange in the hello; ordering from
// the relay's own connection plus a strict record counter; framing and
// multiplexing from one frame per record. What is missing is retransmission —
// a record the relay drops ends the session, and the layer above reconnects.

import { Deferred, Queue, concat, u32be, u64be, utf8, readU32be, readU64be, str, withAbort } from "./bytes.js";
import { HEADER_LEN, MAGIC, PeerMessageType } from "./peer.js";
import { nacl } from "./nacl.js";

const COUNTER_LEN = 8;
const TAG_LEN = 16;
// Sized against the relay's WebSocket read limit of 32768 bytes, not DERP's
// 64 KiB packet limit: exceed it and the relay closes the connection with
// "read limited at 32769 bytes". What is left over covers the DERP frame
// header, the destination key, the record header and counter, and the tag.
export const MAX_PLAINTEXT = 32256;

/// The two traffic keys, from the two ephemeral halves. The static keys only
/// vouch for those halves — the sealed hellos are what make that true — so a
/// static key stolen later does not open a session recorded now.
export async function deriveKeys({ ephemeralPrivate, peerEphemeral, sessionId, dialerPublic, hostPublic }) {
  const raw = await keySchedule({
    shared: nacl.scalarMult(ephemeralPrivate, peerEphemeral),
    sessionId,
    dialerPublic,
    hostPublic,
  });
  const importAes = (bytes) => crypto.subtle.importKey("raw", bytes, "AES-GCM", false, ["encrypt", "decrypt"]);
  return {
    dialerToHost: await importAes(raw.dialerToHost),
    hostToDialer: await importAes(raw.hostToDialer),
  };
}

/// The schedule alone: shared secret in, one traffic key per direction out.
///
/// Kept apart from the exchange above so that both implementations can be
/// held to the same salt and the same labels from a fixed secret, which a
/// test cannot do through an ephemeral key it did not choose. Returns raw
/// bytes; `deriveKeys` is what turns them into keys that cannot be read back.
export async function keySchedule({ shared, sessionId, dialerPublic, hostPublic }) {
  // The salt names who is talking to whom and which session, so one pair of
  // ephemeral keys could not produce the same traffic keys twice.
  const salt = concat(u64be(sessionId), dialerPublic, hostPublic);

  const saltKey = await crypto.subtle.importKey("raw", salt, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const prk = new Uint8Array(await crypto.subtle.sign("HMAC", saltKey, shared));

  // One block of HKDF-Expand is all that is asked for, so the counter byte is
  // always 1 and there is no previous block to chain.
  const expand = async (label) => {
    const key = await crypto.subtle.importKey("raw", prk, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
    const block = new Uint8Array(await crypto.subtle.sign("HMAC", key, concat(utf8(label), new Uint8Array([1]))));
    return block.slice(0, 32);
  };

  return {
    dialerToHost: await expand("tailcat relay1 v1 d2h"),
    hostToDialer: await expand("tailcat relay1 v1 h2d"),
  };
}

const nonceFor = (counter) => concat(new Uint8Array(4), u64be(counter));

export async function sealRecord(plaintext, key, counter) {
  const sealed = new Uint8Array(
    await crypto.subtle.encrypt({ name: "AES-GCM", iv: nonceFor(counter), tagLength: TAG_LEN * 8 }, key, plaintext),
  );
  return concat(MAGIC, new Uint8Array([PeerMessageType.Relay1Record]), u64be(counter), sealed);
}

export async function openRecord(record, key) {
  if (record.length < HEADER_LEN + COUNTER_LEN + TAG_LEN) throw new Error("a record that short cannot be one");
  const counter = readU64be(record, HEADER_LEN);
  const body = record.slice(HEADER_LEN + COUNTER_LEN);
  const plaintext = new Uint8Array(
    await crypto.subtle.decrypt({ name: "AES-GCM", iv: nonceFor(counter), tagLength: TAG_LEN * 8 }, key, body),
  );
  return { counter, plaintext };
}

export const FrameFlags = { None: 0, Fin: 1, Reset: 2, Window: 4 };

/// A write refused because the peer let go of the stream, which is what QUIC
/// says with `StreamAborted`; `PeerReleasedStreamException` in .NET.
export class PeerReleasedStreamError extends Error {}

export function writeVarint(value) {
  const out = [];
  while (value >= 0x80) {
    out.push((value & 0x7f) | 0x80);
    value = Math.floor(value / 128);
  }
  out.push(value);
  return new Uint8Array(out);
}

export function readVarint(bytes) {
  let value = 0;
  let shift = 0;
  let at = 0;
  for (;;) {
    if (at >= bytes.length) throw new Error("a varint runs past the end of its frame");
    const b = bytes[at++];
    value += (b & 0x7f) * 2 ** shift;
    if ((b & 0x80) === 0) return { value, read: at };
    shift += 7;
    if (shift > 63) throw new Error("a varint is too long to be one");
  }
}

export const encodeFrame = (streamId, flags, payload) =>
  concat(writeVarint(streamId), new Uint8Array([flags]), payload);

const headerLength = (streamId) => writeVarint(streamId).length + 1;

/// One stream. Reading and writing are independent, as on a QUIC
/// bidirectional stream: the peer's FIN ends what can be read and leaves
/// writing alone, because the layer above closes each direction as it
/// finishes with it.
export class Relay1Stream {
  static INITIAL_WINDOW = 256 * 1024;
  static #UPDATE_THRESHOLD = Relay1Stream.INITIAL_WINDOW / 4;

  #session;
  #inbound = new Queue();
  #leftover = new Uint8Array(0);
  #consumed = 0;
  #credit = Relay1Stream.INITIAL_WINDOW;
  #creditWaiter = null;
  #finSent = false;
  #finReceived = false;
  #reset = null;
  #resetByPeer = false;

  constructor(session, id) {
    this.#session = session;
    this.id = id;
  }

  /// Reads at most one queued chunk, and at most `limit` bytes of it. What is
  /// not taken stays queued, so only bytes actually handed to the caller
  /// count against the window. Returns an empty array once the peer has said
  /// FIN and everything it sent has been handed over.
  async read(signal, limit = Infinity) {
    for (;;) {
      if (this.#leftover.length) {
        const taken = Math.min(this.#leftover.length, limit);
        const chunk = this.#leftover.subarray(0, taken);
        this.#leftover = this.#leftover.subarray(taken);
        this.#consumed += taken;
        await this.#grantCreditIfDue();
        return chunk;
      }
      // After the peer's FIN everything it sent is on its way, whatever ends
      // the stream afterwards.
      if (this.#reset && !this.#finReceived) throw new Error(`the peer reset the stream: ${this.#reset}`);
      try {
        this.#leftover = await this.#inbound.next(signal);
      } catch (error) {
        if (this.#inbound.closed && (!this.#reset || this.#finReceived)) return new Uint8Array(0);
        throw error;
      }
    }
  }

  /// Reads exactly `count` bytes, which is what a length-prefixed frame needs.
  async readExactly(count, signal) {
    const parts = [];
    let have = 0;
    while (have < count) {
      const chunk = await this.read(signal, count - have);
      if (!chunk.length) throw new Error(`the peer stopped after ${have} of ${count} bytes`);
      parts.push(chunk);
      have += chunk.length;
    }
    return concat(...parts);
  }

  async write(payload, signal) {
    if (this.#finSent) throw new Error("this stream has already been finished");
    const max = MAX_PLAINTEXT - headerLength(this.id);
    let at = 0;
    while (at < payload.length) {
      const wanted = Math.min(payload.length - at, max);
      const allowed = await this.#reserveCredit(wanted, signal);
      await this.#session._sendFrame(this.id, FrameFlags.None, payload.slice(at, at + allowed));
      at += allowed;
    }
  }

  /// Says there is nothing more to send. The peer is waiting to read, and
  /// without this it waits for good.
  async finish() {
    if (this.#finSent) return;
    this.#finSent = true;
    try {
      await this.#session._sendFrame(this.id, FrameFlags.Fin, new Uint8Array(0));
    } catch {
      // The session is gone, which ends the stream more thoroughly than a FIN.
    }
  }

  async close() {
    await this.finish();
    await this.#tellAWriterWeLetGo();
    this.#session._forget(this.id);
    this.#inbound.close(new Error("the stream was closed"));
  }

  // What QUIC does for a stream closed before the peer's FIN: a writer parked
  // on credit this end will never grant would otherwise wait for the session
  // to end. Sent after the FIN, so what the peer reads is unchanged.
  async #tellAWriterWeLetGo() {
    if (this.#finReceived || this.#reset) return;
    try {
      await this.#session._sendFrame(this.id, FrameFlags.Reset, utf8("the peer let go of the stream"));
    } catch {
      // The session is gone, which ends the stream anyway.
    }
  }

  // ---- driven by the session's receive path --------------------------

  _onData(payload) {
    if (payload.length) this.#inbound.push(payload);
  }

  _onFin() {
    this.#finReceived = true;
    this.#inbound.close(new Error("the peer finished the stream"));
  }

  _onReset(reason) {
    this.#reset = reason || "no reason given";
    this.#resetByPeer = true;
    this.#inbound.close(new Error(this.#reset));
    this.#wakeCredit();
  }

  _onWindow(credit) {
    this.#credit += credit;
    this.#wakeCredit();
  }

  _onSessionClosed(error) {
    this.#reset ??= error.message;
    this.#inbound.close(error);
    this.#wakeCredit();
  }

  #wakeCredit() {
    const waiter = this.#creditWaiter;
    this.#creditWaiter = null;
    waiter?.resolve();
  }

  async #reserveCredit(wanted, signal) {
    for (;;) {
      if (this.#reset) {
        const message = `the stream is not writable: ${this.#reset}`;
        throw this.#resetByPeer ? new PeerReleasedStreamError(message) : new Error(message);
      }
      if (this.#credit > 0) {
        const taken = Math.min(wanted, this.#credit);
        this.#credit -= taken;
        return taken;
      }
      this.#creditWaiter ??= new Deferred();
      // withAbort, not a race built here: a signal that has already fired
      // will never raise "abort" again, and waiting for credit the peer has
      // stopped granting would then never end.
      await withAbort(this.#creditWaiter.promise, signal);
    }
  }

  async #grantCreditIfDue() {
    if (this.#consumed < Relay1Stream.#UPDATE_THRESHOLD) return;
    const grant = this.#consumed;
    this.#consumed = 0;
    try {
      await this.#session._sendFrame(this.id, FrameFlags.Window, u32be(grant));
    } catch {
      // The session ended; there is nobody left to grant credit to.
    }
  }
}

/// A session the relay carries. One record at a time on the way out, because
/// the counter has to increase in the order the records reach the relay.
export class Relay1Session {
  #derp;
  #peerPublic;
  #sendKey;
  #receiveKey;
  #sendCounter = 0n;
  #expectedCounter = 0n;
  #streams = new Map();
  #nextStreamId;
  #retiredPeerStreams = new Set();
  #retiredBelowPeerStream;
  #sending = Promise.resolve();
  #closed = null;

  constructor({ derp, peerPublic, keys, isDialer }) {
    this.#derp = derp;
    this.#peerPublic = peerPublic;
    this.#sendKey = isDialer ? keys.dialerToHost : keys.hostToDialer;
    this.#receiveKey = isDialer ? keys.hostToDialer : keys.dialerToHost;
    // Odd from the dialler, even from the host, so neither end has to ask
    // before opening one and the two can never pick the same id.
    this.#nextStreamId = isDialer ? 1 : 2;
    this.#retiredBelowPeerStream = (isDialer ? 2 : 1) - 2;
    this.accepted = new Queue();
    this.onclose = null;
    // Called for each record ignored because it would not open. One now and
    // then is the session before's; a steady stream with nothing else is two
    // ends whose keys disagree, which otherwise looks like a silent peer.
    this.onignored = null;
  }

  get closed() {
    return this.#closed !== null;
  }

  openStream() {
    if (this.#closed) throw this.#closed;
    const id = this.#nextStreamId;
    this.#nextStreamId += 2;
    const stream = new Relay1Stream(this, id);
    this.#streams.set(id, stream);
    return stream;
  }

  /// Takes one record. One that arrives out of order ends the session: there
  /// is no way back from a hole in the middle of a message, and pretending
  /// otherwise hands the layer above corruption.
  async handleRecord(record) {
    if (this.#closed) return;
    let counter;
    let plaintext;
    try {
      ({ counter, plaintext } = await openRecord(record, this.#receiveKey));
    } catch {
      // Not this session's, and not news: usually the session before's,
      // resent by a machine whose relay connection died after that session
      // had been replaced. Ending the session on it ended every new one built
      // straight after a cut. The tag still keeps a forgery out, and a genuine
      // record corrupted in flight leaves a gap the next one shows.
      // Relay1Connection.HandleRecord does the same, and reports it too.
      this.onignored?.();
      return;
    }

    // A record seen before was sent again by the other end after its relay
    // connection died, not knowing whether the first copy arrived. It opened,
    // so it is genuine; it is simply not news. Relay1Connection.HandleRecord
    // does the same.
    if (counter < this.#expectedCounter) return;
    if (counter !== this.#expectedCounter) {
      this.close(new Error(`record ${counter} arrived where ${this.#expectedCounter} was expected; one was dropped`));
      return;
    }
    this.#expectedCounter += 1n;

    let frame;
    try {
      const { value: streamId, read } = readVarint(plaintext);
      // The flags byte is not optional. A frame cut short of it must end the
      // session, as Relay1Frame.TryDecode does on the .NET side; treating it
      // as an empty data frame would let the two ends disagree about the
      // same bytes.
      if (read >= plaintext.length) throw new Error("a relay1 frame ended before its flags byte");
      frame = { streamId, flags: plaintext[read], payload: plaintext.slice(read + 1) };
    } catch (error) {
      this.close(error);
      return;
    }

    const stream = this.#streamFor(frame.streamId);
    if (!stream) return; // A late frame for a stream that has been closed.

    if (frame.flags & FrameFlags.Window) {
      if (frame.payload.length >= 4) stream._onWindow(readU32be(frame.payload));
      return;
    }
    if (frame.flags & FrameFlags.Reset) {
      stream._onReset(str(frame.payload));
      return;
    }
    stream._onData(frame.payload);
    if (frame.flags & FrameFlags.Fin) stream._onFin();
  }

  async _sendFrame(streamId, flags, payload) {
    if (this.#closed) throw this.#closed;
    const frame = encodeFrame(streamId, flags, payload);

    // Serialised: two records that swapped places would look to the far end
    // like one the relay dropped.
    const send = this.#sending.then(async () => {
      if (this.#closed) throw this.#closed;
      const record = await sealRecord(frame, this.#sendKey, this.#sendCounter);
      this.#sendCounter += 1n;
      this.#derp.sendPacket(this.#peerPublic, record);
    });
    this.#sending = send.catch(() => {});
    return send;
  }

  _forget(streamId) {
    this.#streams.delete(streamId);
    if (streamId % 2 !== this.#nextStreamId % 2) this.#retirePeerStream(streamId);
  }

  // A stream this end has finished with must not come back when the peer's own
  // FIN arrives afterwards, which it does on every request the peer makes.
  // Only the ids actually retired can say that: a higher id may well arrive
  // first, because the .NET end numbers a stream before taking the lock that
  // serialises its sending, so two of its concurrent requests can reach the
  // relay in the other order. Treating the highest id seen as a watermark
  // dropped the lower one's frames in silence.
  #retirePeerStream(streamId) {
    this.#retiredPeerStreams.add(streamId);
    // The peer allocates its ids in order, so the run starting at the oldest
    // collapses into a watermark and the set only holds what came out of turn.
    while (this.#retiredPeerStreams.delete(this.#retiredBelowPeerStream + 2)) {
      this.#retiredBelowPeerStream += 2;
    }
  }

  close(error = new Error("the session was closed")) {
    if (this.#closed) return;
    this.#closed = error;
    for (const stream of this.#streams.values()) stream._onSessionClosed(error);
    this.#streams.clear();
    this.accepted.close(error);
    this.onclose?.(error);
  }

  // A frame naming an id this end did not open, and has not seen, is the peer
  // opening a stream: that is the only announcement there is.
  #streamFor(streamId) {
    const known = this.#streams.get(streamId);
    if (known) return known;

    const mine = this.#nextStreamId % 2;
    if (streamId === 0 || streamId % 2 === mine) return null;

    // An id this end has already retired names a stream that has been closed
    // and forgotten; reviving it would hand the layer above a stream with
    // nothing in it. See #retirePeerStream.
    if (streamId <= this.#retiredBelowPeerStream) return null;
    if (this.#retiredPeerStreams.has(streamId)) return null;

    const stream = new Relay1Stream(this, streamId);
    this.#streams.set(streamId, stream);
    this.accepted.push(stream);
    return stream;
  }
}
