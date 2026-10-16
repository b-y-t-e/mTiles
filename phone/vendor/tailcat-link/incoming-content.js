// Content arriving from the other machine — a request, a notification, a
// transfer, or the answer to one — with what it says about itself.
// `IncomingTransfer.cs` is the .NET half.
//
// What differs is only how a handler is given it. .NET hands a handler a
// stream that fills as blocks arrive; a page is handed the whole of it once it
// is all here. Everything about how it gets here is the same: each block is
// taken by its position in the content, so a session dying mid-content leaves
// an offset rather than a ruin, and neither a resumed attempt nor a repeated
// one can put the same byte in twice.

import { Deferred, concat, hex, str } from "./bytes.js";
import { LinkError } from "./errors.js";
import { readBlockLength } from "./exchange-frame.js";

// What a file system anywhere refuses in a name, which is the strictest set:
// the name was written by the other machine, and the page may save it anywhere.
const REFUSED_IN_A_NAME = new Set(['"', "<", ">", "|", ":", "*", "?", "\\", "/"]);

export class IncomingContent {
  #parts = [];
  #received = 0;
  #ended = false;
  #whole = null;
  #isReceipt = false;
  #complete = new Deferred();
  #lastArrived = Date.now();

  constructor({ id, name = "", contentType = "", length = null, metadata = new Uint8Array(0) }) {
    /// What identifies it, on both machines and across every session it takes.
    this.id = hex(id);
    /// What the sender called it. Not to be trusted as a path: see `suggestedFileName`.
    this.name = name;
    this.contentType = contentType;
    /// How many bytes are coming, when the sender knew.
    this.length = length;
    this.metadata = metadata;
  }

  /// Content that has already arrived whole, as a single frame brings it.
  static whole(id, bytes, { name = "", contentType = "", metadata = new Uint8Array(0) } = {}) {
    const content = new IncomingContent({ id, name, contentType, length: bytes.length, metadata });
    content.accept(bytes, 0);
    content.end();
    return content;
  }

  /// The same content as described and counted, holding none of its bytes: what
  /// an exchange keeps once its handler is done, when a resumed attempt only
  /// needs to know how much arrived. The original is left whole for whoever
  /// still holds it.
  receipt() {
    const receipt = new IncomingContent({
      id: new Uint8Array(0),
      name: this.name,
      contentType: this.contentType,
      length: this.length,
      metadata: this.metadata,
    });
    receipt.id = this.id;
    receipt.#isReceipt = true;
    receipt.#received = this.#received;
    receipt.#ended = this.#ended;
    receipt.#lastArrived = this.#lastArrived;
    if (this.failure) receipt.failure = this.failure;
    receipt.#complete = this.#complete;
    return receipt;
  }

  get bytesReceived() {
    return this.#received;
  }

  get lastArrived() {
    return this.#lastArrived;
  }

  get bodyEnded() {
    return this.#ended;
  }

  /// Settles once the content has all arrived, or failed to.
  get complete() {
    return this.#complete.promise;
  }

  /// The whole content. Only there once it has all arrived.
  get bytes() {
    if (this.failure) throw this.failure;
    if (!this.#ended) throw new LinkError("the content has not all arrived yet");
    if (this.#isReceipt) throw new LinkError("this is a receipt for the content, which holds none of its bytes");
    this.#whole ??= concat(...this.#parts);
    this.#parts = [];
    return this.#whole;
  }

  get text() {
    return str(this.bytes);
  }

  /// `name` reduced to something safe to append to a directory.
  ///
  /// The name comes from the other machine, which makes it exactly as
  /// trustworthy as anything else off a network: `../../etc/passwd` is a name
  /// a peer may send. This keeps the last path segment, drops what a file
  /// system would refuse, and falls back to `transfer` when nothing is left.
  get suggestedFileName() {
    const bare = this.name.replaceAll("\\", "/");
    const last = bare.slice(bare.lastIndexOf("/") + 1);
    const cleaned = [...last].filter((c) => c.charCodeAt(0) >= 32 && c.charCodeAt(0) !== 127 && !REFUSED_IN_A_NAME.has(c));
    const name = cleaned.join("").replace(/^[ .]+|[ .]+$/g, "");
    return name.length ? name : "transfer";
  }

  /// Takes one block, which begins at `position` in the content. Whatever
  /// overlaps what is already here is dropped.
  ///
  /// @throws {LinkError} if the block begins past what is here, or arrives after the end.
  accept(block, position) {
    if (position > this.#received) {
      throw new LinkError(`the peer sent content from byte ${position} with only ${this.#received} here`);
    }
    const fresh = block.subarray(Math.min(block.length, this.#received - position));
    if (!fresh.length) return;
    if (this.#ended) throw new LinkError("the peer sent more of content it had already ended");
    this.#parts.push(fresh);
    this.#received += fresh.length;
    this.#lastArrived = Date.now();
  }

  /// The block that ends the content arrived. Content that ends short of the
  /// length it announced is broken, and fails rather than being taken as whole:
  /// the content on the other machine is what ran out, and asking again would
  /// produce the same.
  end() {
    if (this.#ended) return;
    if (this.length !== null && this.#received !== this.length) {
      this.fail(new LinkError(`the content ended after ${this.#received} of the ${this.length} bytes it announced`));
      return;
    }
    this.#ended = true;
    this.#complete.resolve(this);
  }

  /// Ends the content with an error, if it had not already ended.
  fail(error) {
    if (this.#ended) return;
    this.#ended = true;
    this.#parts = [];
    this.failure = error;
    this.#complete.reject(error);
  }

  /// Takes content off one stream, the first block beginning at `startsAt`,
  /// up to and including the block that ends it.
  ///
  /// @param silenceMs how long one read from the network may wait, or nothing
  ///   for no bound.
  async readBody(stream, startsAt, { silenceMs, signal } = {}) {
    let position = startsAt;
    for (;;) {
      const length = await fromNetwork((s) => readBlockLength(stream, s), silenceMs, signal);
      if (length === 0) break;
      const block = await fromNetwork((s) => stream.readExactly(length, s), silenceMs, signal);
      this.accept(block, position);
      position += length;
    }
    this.end();
  }
}

// One read, bounded by its own silence when there is one to bound it by.
async function fromNetwork(read, silenceMs, signal) {
  if (silenceMs === undefined) return read(signal);
  const quiet = AbortSignal.timeout(silenceMs);
  return read(signal ? AbortSignal.any([signal, quiet]) : quiet);
}
