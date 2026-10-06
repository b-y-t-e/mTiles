// The relay, reached the only way a browser can: a WebSocket.
//
// A DERP relay routes packets between public keys and reads none of them. It
// is the meeting place, and for this client it is also the whole transport —
// there is no direct path to move onto.

import { Queue, concat, equal, randomBytes, readU32be, str, u32be, utf8 } from "./bytes.js";
import { nacl } from "./nacl.js";

export const DerpFrame = {
  ServerKey: 0x01,
  ClientInfo: 0x02,
  ServerInfo: 0x03,
  SendPacket: 0x04,
  RecvPacket: 0x05,
  KeepAlive: 0x06,
  PeerGone: 0x08,
  PeerPresent: 0x09,
  Ping: 0x12,
  Pong: 0x13,
  Health: 0x14,
  Restarting: 0x15,
};

const MAGIC = utf8("DERP\u{1F511}");
const PROTOCOL_VERSION = 2;
const KEY_LEN = 32;
const NONCE_LEN = 24;

// The largest frame this client will read: the DERP packet limit plus the
// small headers a frame can carry, matching DerpFrameStream on the .NET
// side. A length beyond this is a broken or hostile relay, and the socket
// goes rather than the buffer — the buffer is what such a length is for.
const MAX_PACKET_SIZE = 64 * 1024;
const MAX_FRAME_LEN = MAX_PACKET_SIZE + 1024;

/// How a connection that stopped carrying bytes without ending is noticed:
/// `DerpLiveness.cs`, with the same timings and for the same reason.
///
/// A firewall that loses track of the flow under a WebSocket drops everything
/// on it both ways while the socket stays open, and a browser reports nothing
/// until TCP gives up, which can take minutes. Until then the link's own
/// heartbeat was the only thing to notice — 20 s plus a request timeout of
/// silence — and every request waited that long. So the connection asks the
/// relay: after a send that got nothing back for `probeAfterSendMs`, or after
/// `probeWhenIdleMs` of silence regardless, it pings; if nothing at all
/// arrives within `timeoutMs` of the ping leaving, the connection is closed,
/// and the link dials a new one straight away.
///
/// There is no resend here, unlike on the .NET side. There the relay1 session
/// outlives the relay connection under it, so what a dead connection swallowed
/// is worth sending again; here a session ends with its WebSocket, and the
/// link resumes every exchange on the next one from where each end got to.
export const DERP_LIVENESS = Object.freeze({
  probeAfterSendMs: 1_000,
  probeWhenIdleMs: 15_000,
  timeoutMs: 2_000,
  checkIntervalMs: 500,
});

export class DerpConnection {
  #socket;
  #privateKey;
  #publicKey;
  #buffer = new Uint8Array(0);
  #frames = new Queue();
  #closed = false;

  // What the liveness check knows: when anything last arrived, whether a
  // packet went out since, and the ping it is waiting on.
  #lastHeardAt = Date.now();
  #sentSinceHeard = false;
  #probe = null;
  #watch = null;
  // Every byte handed to the socket, so that `bufferedAmount` says how many of
  // them have left it.
  #queuedBytes = 0;

  /// @param liveness how a silent connection is noticed; `null` turns it off,
  ///   for a test that needs the connection left exactly as the relay leaves it.
  constructor(socket, privateKey, publicKey, { liveness = DERP_LIVENESS } = {}) {
    this.#socket = socket;
    this.#privateKey = privateKey;
    this.#publicKey = publicKey;
    this.packets = new Queue();
    this.onclose = null;

    socket.binaryType = "arraybuffer";
    socket.onmessage = (event) => this.#onBytes(new Uint8Array(event.data));
    socket.onclose = (event) =>
      this.#end(new Error(`the relay closed the connection (code ${event.code}${event.reason ? `, ${event.reason}` : ""})`));
    socket.onerror = () => this.#end(new Error("the relay connection failed"));

    if (liveness) {
      this.#watch = setInterval(() => this.#checkLiveness(liveness), liveness.checkIntervalMs);
      // A check pending is no reason for Node to stay up; the socket is. A
      // browser's timer id has no such method.
      this.#watch.unref?.();
    }
  }

  static async connect({ url, privateKey, publicKey, signal, liveness }) {
    const socket = new WebSocket(url, "derp");
    await new Promise((resolve, reject) => {
      socket.onopen = resolve;
      socket.onerror = () => reject(new Error(`could not reach ${url}`));
      signal?.addEventListener("abort", () => {
        socket.close();
        reject(signal.reason ?? new Error("aborted"));
      }, { once: true });
    });

    const conn = new DerpConnection(socket, privateKey, publicKey, { liveness });
    await conn.#login();
    return conn;
  }

  get closed() {
    return this.#closed;
  }

  // A WebSocket delivers messages, not a stream: one DERP frame may span
  // several of them and several may share one, so everything is buffered and
  // frames are cut out as they complete.
  #onBytes(chunk) {
    if (this.#closed) {
      // The close is already under way; whatever still arrives — a WebSocket
      // keeps delivering until the handshake completes — is not for a
      // connection anybody is reading.
      return;
    }
    // Any bytes at all, not only a pong: a busy connection proves itself.
    this.#lastHeardAt = Date.now();
    this.#sentSinceHeard = false;
    this.#probe = null;
    this.#buffer = concat(this.#buffer, chunk);
    for (;;) {
      if (this.#buffer.length < 5) return;
      const length = readU32be(this.#buffer, 1);
      if (length > MAX_FRAME_LEN) {
        this.#end(new Error(`the relay announced a ${length}-byte frame, over the ${MAX_FRAME_LEN} limit`));
        this.#shutSocket();
        return;
      }
      if (this.#buffer.length < 5 + length) return;

      const type = this.#buffer[0];
      const payload = this.#buffer.slice(5, 5 + length);
      this.#buffer = this.#buffer.slice(5 + length);
      this.#dispatch(type, payload);
    }
  }

  #dispatch(type, payload) {
    switch (type) {
      case DerpFrame.Ping:
        // The relay measures liveness with these and nothing above cares.
        this.send(DerpFrame.Pong, payload);
        return;
      case DerpFrame.KeepAlive:
      case DerpFrame.PeerGone:
      case DerpFrame.PeerPresent:
      case DerpFrame.Health:
      case DerpFrame.Restarting:
        return;
      case DerpFrame.RecvPacket:
        if (payload.length >= KEY_LEN) {
          this.packets.push({ source: payload.slice(0, KEY_LEN), payload: payload.slice(KEY_LEN) });
        }
        return;
      default:
        this.#frames.push({ type, payload });
    }
  }

  // One check: asks a connection that has been silent too long, closes one
  // that did not answer.
  #checkLiveness({ probeAfterSendMs, probeWhenIdleMs, timeoutMs }) {
    if (this.#closed) return;
    const now = Date.now();

    if (this.#probe) {
      const probe = this.#probe;
      // Timed from when the ping left the socket, not from when it was queued:
      // one waiting behind a large upload has not gone unanswered while it
      // waited. But a ping that cannot leave at all within the timeout is the
      // same verdict, as a stuck write is on the .NET side — and nothing came
      // in either, or there would be no probe.
      if (probe.leftAt === null && this.#queuedBytes - this.#socket.bufferedAmount >= probe.queuedThrough) {
        probe.leftAt = now;
      }
      const waited = now - (probe.leftAt ?? probe.sentAt);
      if (waited >= timeoutMs) {
        this.#end(new Error(`the relay connection went silent: nothing arrived within ${timeoutMs} ms of a ping`));
        this.#shutSocket();
      }
      return;
    }

    const silent = now - this.#lastHeardAt;
    if (silent < (this.#sentSinceHeard ? probeAfterSendMs : probeWhenIdleMs)) return;
    this.send(DerpFrame.Ping, randomBytes(8));
    this.#probe = { sentAt: now, queuedThrough: this.#queuedBytes, leftAt: null };
  }

  #end(error) {
    if (this.#closed) return;
    this.#closed = true;
    clearInterval(this.#watch);
    this.#frames.close(error);
    this.packets.close(error);
    this.onclose?.(error);
  }

  // The socket may already be gone — a close event raced us here — which is
  // not an error.
  #shutSocket() {
    try {
      this.#socket.close();
    } catch {
      // Already gone; there is nothing to close.
    }
  }

  async #login() {
    const greeting = await this.#frames.next();
    if (greeting.type !== DerpFrame.ServerKey) {
      throw new Error(`expected a ServerKey frame, got 0x${greeting.type.toString(16)}`);
    }
    if (!equal(greeting.payload.slice(0, MAGIC.length), MAGIC)) {
      throw new Error("the greeting is not DERP");
    }
    this.serverKey = greeting.payload.slice(MAGIC.length, MAGIC.length + KEY_LEN);

    const info = utf8(JSON.stringify({ version: PROTOCOL_VERSION, CanAckPings: true }));
    const nonce = nacl.randomBytes(NONCE_LEN);
    const sealed = nacl.box(info, nonce, this.serverKey, this.#privateKey);
    this.send(DerpFrame.ClientInfo, concat(this.#publicKey, nonce, sealed));

    const answer = await this.#frames.next();
    if (answer.type !== DerpFrame.ServerInfo) {
      throw new Error(`expected ServerInfo, got 0x${answer.type.toString(16)}`);
    }
    const opened = nacl.box.open(
      answer.payload.slice(NONCE_LEN),
      answer.payload.slice(0, NONCE_LEN),
      this.serverKey,
      this.#privateKey,
    );
    if (!opened) throw new Error("the relay's ServerInfo would not open");
    this.serverInfo = JSON.parse(str(opened));
  }

  send(type, payload) {
    if (this.#closed) throw new Error("the relay connection is closed");
    const frame = concat(new Uint8Array([type]), u32be(payload.length), payload);
    this.#socket.send(frame);
    this.#queuedBytes += frame.length;
  }

  sendPacket(destination, packet) {
    this.send(DerpFrame.SendPacket, concat(destination, packet));
    // Only packets expect an answer: a pong to the relay's ping does not.
    this.#sentSinceHeard = true;
  }

  /// Resolves once everything sent has left the socket, or the wait runs out.
  ///
  /// `send()` hands bytes to the WebSocket and returns; they leave later, and
  /// closing — or exiting the process — in that window discards whatever is
  /// still queued. `bufferedAmount` is the only progress a WebSocket reports,
  /// and it fires no event when it drains, so this polls it.
  async flush(timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    while (!this.#closed && this.#socket.bufferedAmount > 0) {
      if (Date.now() >= deadline) return false;
      await new Promise((resolve) => setTimeout(resolve, 5));
    }
    return !this.#closed;
  }

  close() {
    this.#end(new Error("closed locally"));
    this.#shutSocket();
  }
}

/// Turns a region id into the relay to dial. Same-origin by default: the
/// control plane will not serve its map to a page from another origin, which
/// is why the upstream browser build ships its own copy too.
export async function relayHostFor(derpMapUrl, regionId) {
  const response = await fetch(derpMapUrl);
  if (!response.ok) {
    throw new Error(`the DERP map at ${derpMapUrl} answered ${response.status}`);
  }
  const map = await response.json();
  const region = map.Regions?.[String(regionId)];
  if (!region) throw new Error(`the DERP map has no region ${regionId}`);

  const node = region.Nodes?.find((n) => !n.STUNOnly);
  if (!node?.HostName) throw new Error(`region ${regionId} names no usable relay`);
  return node.HostName;
}
