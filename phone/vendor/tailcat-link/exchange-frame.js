// The two headers of an exchange, the offset an end says it has, and the blocks
// content travels in. `ExchangeFrame.cs` and `TransferFrame.cs` are the .NET
// half, and `docs/exchanges.md` is the specification both follow.
//
// Every field is length-prefixed with 32 bits. A narrower prefix would be a
// limit on what an application can say about its content, which is not the
// protocol's business.

import { concat, readU32be, str, u32be, utf8 } from "./bytes.js";
import { LinkError } from "./errors.js";

/// What an exchange asks of the machine that receives it.
export const ExchangeFlags = {
  None: 0,
  // A request: the handler's answer comes back as content of its own.
  Answer: 1,
  // For the transfer handler rather than the request handler.
  Transfer: 2,
  // The content follows the header at once instead of waiting to be told
  // where to start: a first attempt with content that fits one block, where
  // the answer could only have been zero, spared the round trip it costs.
  Pipelined: 4,
  // Acknowledged once the content has arrived rather than once the handler has
  // dealt with it — a notification, whose sender waits for nothing it does.
  AckOnDelivery: 8,
};

const KNOWN_FLAGS = ExchangeFlags.Answer | ExchangeFlags.Transfer | ExchangeFlags.Pipelined | ExchangeFlags.AckOnDelivery;
const VERSION = 1;

/// How much content one block carries: a whole relay1 stream window, so a
/// block never waits for a window update in the middle of itself.
export const BLOCK_BYTES = 256 * 1024;

const BLOCK_HEADER_LENGTH = 4;

export function encodeExchangeHeader({
  flags,
  length = null,
  answerOffset = 0,
  name = "",
  contentType = "",
  metadata = new Uint8Array(0),
}) {
  if (length !== null && length < 0) throw new LinkError(`content cannot be ${length} bytes long`);
  // -1 rather than a flag: a length nobody could send is the clearest way to
  // say "unknown", and it keeps the header one shape.
  return concat(
    Uint8Array.of(VERSION, flags),
    i64(length ?? -1),
    i64(answerOffset),
    field(utf8(name)),
    field(utf8(contentType)),
    field(metadata),
  );
}

/// Whether blocks follow a header on the stream, which is still worth knowing
/// of a header refused for the rest of it: they must be read before the refusal
/// is heard. Only a header in this version says so; another's second byte could
/// mean anything.
export function announcesPipelinedContent(payload) {
  return payload.length >= 2 && payload[0] === VERSION && (payload[1] & ExchangeFlags.Pipelined) !== 0;
}

/// @throws {LinkError} if the peer sent something this cannot be.
export function decodeExchangeHeader(payload) {
  expectVersion(payload, "an exchange", 18);
  const flags = payload[1];
  if (flags & ~KNOWN_FLAGS) {
    // Refused rather than ignored: a flag is an instruction, and carrying on
    // without following it would do something other than what was asked.
    throw new LinkError(`the peer asked for exchange flags 0x${flags.toString(16)}, which this does not know`);
  }
  const length = readI64(payload, 2);
  const answerOffset = readI64(payload, 10);
  if (answerOffset < 0) throw new LinkError(`the peer says it has ${answerOffset} bytes of the answer`);

  const [name, afterName] = readField(payload, 18);
  const [contentType, afterType] = readField(payload, afterName);
  const [metadata] = readField(payload, afterType);
  return {
    flags,
    length: length < 0 ? null : length,
    answerOffset,
    name: str(name),
    contentType: str(contentType),
    metadata: metadata.slice(),
  };
}

export function encodeAnswerHeader({ length = null, contentType = "", metadata = new Uint8Array(0) }) {
  if (length !== null && length < 0) throw new LinkError(`an answer cannot be ${length} bytes long`);
  return concat(Uint8Array.of(VERSION), i64(length ?? -1), field(utf8(contentType)), field(metadata));
}

/// @throws {LinkError} if the peer sent something this cannot be.
export function decodeAnswerHeader(payload) {
  expectVersion(payload, "an answer", 9);
  const length = readI64(payload, 1);
  const [contentType, afterType] = readField(payload, 9);
  const [metadata] = readField(payload, afterType);
  return { length: length < 0 ? null : length, contentType: str(contentType), metadata: metadata.slice() };
}

/// The offset an end says it wants content, or an answer, to start at.
export const encodeOffset = (offset) => i64(offset);

/// @throws {LinkError} if the peer asked to start somewhere impossible.
export function decodeOffset(payload, length) {
  if (payload.length < 8) throw new LinkError("the peer took the exchange without saying where to start");
  const offset = readI64(payload, 0);
  if (offset < 0 || (length !== null && offset > length)) {
    throw new LinkError(`the peer asked the content to start at byte ${offset}`);
  }
  return offset;
}

/// The length in front of a block, or the zero that ends the content.
export const encodeBlockHeader = (length) => u32be(length);

/// The length of the next block, or zero at the end of the content.
///
/// @throws {LinkError} if the peer announced a block larger than one can be.
export async function readBlockLength(stream, signal) {
  const length = readU32be(await stream.readExactly(BLOCK_HEADER_LENGTH, signal));
  if (length > BLOCK_BYTES) {
    throw new LinkError(`the peer announced a ${length}-byte block; the limit is ${BLOCK_BYTES}`);
  }
  return length;
}

/// Writes content from `from`, a block at a time, and the block that ends it.
/// `idle` is told after every block, and `advanced` with each block's size.
export async function writeBlocks(stream, content, from, idle, advanced = () => {}) {
  for (let at = from; at < content.length; ) {
    const block = content.subarray(at, Math.min(content.length, at + BLOCK_BYTES));
    // Header and block as one write: on the relayed transport a separate
    // four-byte write would be a whole record of its own.
    await stream.write(concat(encodeBlockHeader(block.length), block), idle?.signal);
    idle?.restart();
    at += block.length;
    advanced(block.length);
  }
  await stream.write(encodeBlockHeader(0), idle?.signal);
}

/// Reads blocks and throws them away, up to and including the end. For content
/// that arrived ahead of a refusal: left unread, the refusal behind it would
/// reach nobody.
export async function drainBlocks(stream, signal) {
  let length;
  while ((length = await readBlockLength(stream, signal)) !== 0) {
    await stream.readExactly(length, signal);
  }
}

function i64(value) {
  const out = new Uint8Array(8);
  new DataView(out.buffer).setBigInt64(0, BigInt(value));
  return out;
}

function readI64(payload, at) {
  if (payload.length < at + 8) throw new LinkError("the peer's header stopped in the middle");
  const value = new DataView(payload.buffer, payload.byteOffset + at, 8).getBigInt64(0);
  if (value > BigInt(Number.MAX_SAFE_INTEGER)) {
    throw new LinkError(`the peer announced ${value} bytes, more than this client can count`);
  }
  return Number(value);
}

const field = (bytes) => concat(u32be(bytes.length), bytes);

function readField(payload, at) {
  if (payload.length < at + 4) throw new LinkError("the peer's header stopped in the middle");
  const length = readU32be(payload, at);
  if (length > payload.length - at - 4) {
    throw new LinkError(`the peer announced a ${length}-byte field with ${payload.length - at - 4} left`);
  }
  return [payload.subarray(at + 4, at + 4 + length), at + 4 + length];
}

function expectVersion(payload, what, fixed) {
  if (payload.length < fixed) throw new LinkError(`the peer's header for ${what} stopped in the middle`);
  if (payload[0] !== VERSION) {
    throw new LinkError(`the peer sent ${what} in version ${payload[0]}, which this does not speak`);
  }
}
