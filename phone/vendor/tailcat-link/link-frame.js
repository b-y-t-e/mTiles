// The layer the application actually speaks: one request per stream, with a
// length prefix, an exchange id, and an answer.
//
// The exchange id is not for routing — each exchange gets its own stream, so
// nothing needs demultiplexing here — but for identity across sessions. A
// request re-sent after a session died carries the id of the original, which
// is how the far end recognises it as the same request rather than a second
// one and answers from memory instead of running it again.

import { concat, randomBytes, readU32be, u32be } from "./bytes.js";

export const FrameKind = {
  Request: 1,
  Notify: 2,
  Ping: 3,
  Hello: 4,
  // 5 was the transfer before exchanges, which nothing sends any more; a frame
  // carrying one is refused rather than swallowed.
  Channel: 6,
  // Content of any size, resumed in both directions across sessions: a
  // request with its answer, a notification, or a transfer. See
  // `outbound-exchange.js` and `incoming-exchange.js`.
  Exchange: 7,
  // A two-way stream: see `link-stream.js`.
  Stream: 8,
};

export const FrameStatus = {
  Ok: 0,
  Failed: 1,
};

/// What a machine says it can take, in the one byte it answers a ping with.
/// A machine built before these existed answers with nothing, which is none.
/// `PeerCapabilities.cs` is the .NET half.
export const Capabilities = {
  None: 0,
  // A message frame or a channel frame of any length.
  LargeFrames: 1,
  // The exchange frame (7).
  Exchanges: 2,
};

/// What this client says it can take: the same as the .NET library, so the two
/// exchange content the same way in both directions.
export const THIS_CLIENT = Capabilities.LargeFrames | Capabilities.Exchanges;

/// The most the first frame is read at before either end knows the other —
/// the hello's answer. The one bound on anything a peer sends, and it is not on
/// data: see `LinkProtocol.HelloFrameBytes`.
export const HELLO_FRAME_BYTES = 4 * 1024;

export const encodeCapabilities = (capabilities) => Uint8Array.of(capabilities);

/// Bits this client does not know are dropped rather than refused: a newer
/// machine saying it can do more is no reason to stop talking to it.
export const decodeCapabilities = (answer) =>
  answer.length ? answer[0] & (Capabilities.LargeFrames | Capabilities.Exchanges) : Capabilities.None;
const EXCHANGE_LEN = 16;
const HEADER_LENGTH = 1 + EXCHANGE_LEN + 4;

// Not a buffer size — the payload is already in memory — but how often a
// transfer can say that it is still moving.
const PROGRESS_CHUNK_BYTES = 64 * 1024;

/// A fresh exchange id. Sixteen random bytes, written big-endian, which is
/// what a .NET Guid written big-endian is.
export const newExchange = () => randomBytes(EXCHANGE_LEN);

export function encodeHeader(tag, exchange, payload) {
  return concat(new Uint8Array([tag]), exchange, u32be(payload.length));
}

/// Writes one frame. `idle` is told about every chunk that moves, so that a
/// slow transfer is not mistaken for a dead one; it is absent where the caller
/// imposes no limit.
export async function writeFrame(stream, tag, exchange, payload, idle) {
  await stream.write(encodeHeader(tag, exchange, payload), idle?.signal);
  for (let sent = 0; sent < payload.length; sent += PROGRESS_CHUNK_BYTES) {
    await stream.write(payload.subarray(sent, sent + PROGRESS_CHUNK_BYTES), idle?.signal);
    idle?.restart();
  }
}

/// Reads one frame, of whatever length the peer sends. `idle` is as for
/// writeFrame: told about every chunk that arrives. `limit` bounds only the
/// frames read before the other machine is known, which is the hello's answer.
///
/// Memory follows the bytes that arrive rather than the length announced, so a
/// length is free to announce and costs nothing until the bytes behind it come.
export async function readFrame(stream, idle, { limit } = {}) {
  const header = await stream.readExactly(HEADER_LENGTH, idle?.signal);
  idle?.restart();

  const tag = header[0];
  const exchange = header.slice(1, 1 + EXCHANGE_LEN);
  const length = readU32be(header, 1 + EXCHANGE_LEN);
  if (limit !== undefined && length > limit) {
    throw new Error(`the peer announced a ${length}-byte message here; at most ${limit} is read`);
  }
  return { tag, exchange, payload: await readPayload(stream, length, idle) };
}

// A chunk at a time rather than in one call, for the same reason the write
// side sends one: a single read of sixteen megabytes looks exactly like a peer
// that has gone silent, and reports no progress until it is over.
async function readPayload(stream, length, idle) {
  const parts = [];
  let have = 0;
  while (have < length) {
    const chunk = await stream.read(idle?.signal, length - have);
    if (!chunk.length) throw new Error(`the peer stopped after ${have} of ${length} bytes`);
    parts.push(chunk);
    have += chunk.length;
    idle?.restart();
  }
  return concat(...parts);
}
