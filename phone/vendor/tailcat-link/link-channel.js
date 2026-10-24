// The third shape a link carries, between a message and a file.
//
// A request is one frame with an answer and a ledger entry; a transfer is
// content resumed across as many sessions as it takes. A channel is neither:
// ordered within itself, and not durable — it ends with the session carrying
// it rather than being resumed, which is the whole of what it promises.
// `docs/channels.md` is the specification and `LinkChannel.cs` the .NET half.

import { concat, readU32be, str, u32be, utf8 } from "./bytes.js";
import { LinkClosedError, LinkError } from "./errors.js";

/// The most a channel name may be, in UTF-8 bytes.
export const MAX_CHANNEL_NAME_BYTES = 256;

/// Why a channel stopped carrying frames.
export const ChannelCloseReason = {
  LocalClosed: "local-closed",
  PeerClosed: "peer-closed",
  SessionEnded: "session-ended",
};

/// Writes the name for the frame that opens a channel, or a stream.
export function encodeChannelName(name, what = "channel") {
  const encoded = utf8(name ?? "");
  if (!encoded.length || encoded.length > MAX_CHANNEL_NAME_BYTES) {
    throw new LinkError(`a ${what} name must be 1 to ${MAX_CHANNEL_NAME_BYTES} bytes, this one is ${encoded.length}`);
  }
  return encoded;
}

/// Reads the name out of the frame that opens a channel, or a stream.
export function decodeChannelName(payload, what = "channel") {
  if (!payload.length || payload.length > MAX_CHANNEL_NAME_BYTES) {
    throw new LinkError(`a ${what} name must be 1 to ${MAX_CHANNEL_NAME_BYTES} bytes, this one is ${payload.length}`);
  }
  return str(payload);
}

/// What both ends have in common: a name, the stream the frames go on, and
/// one ending whichever way it arrives.
class Channel {
  #name;
  #closed = null;

  /// @param stream the channel's own stream. Named without a `#` because both
  ///   ends read it; nothing outside this file does.
  constructor(name, stream) {
    this.#name = name;
    this._stream = stream;
    /// Called once with `{ reason, detail }`. The reason is what lets a page
    /// tell "the peer hung up" from "the session died and will be back".
    this.onclose = null;
  }

  get name() {
    return this.#name;
  }

  get open() {
    return this.#closed === null;
  }

  /// `{ reason, detail }` once it has ended, and null while it has not.
  get closed() {
    return this.#closed;
  }

  /// Ends the channel once, for whichever reason arrived first.
  async close(reason = ChannelCloseReason.LocalClosed, detail = "this end closed the channel") {
    if (this.#closed) return;
    this.#closed = { reason, detail };
    await this._release(reason);

    // The page's own listener is isolated, the way the .NET half isolates
    // `Closed`: closing happens on the failure path of `send()` and on
    // `close()`, and a listener that throws there would replace the reason
    // the caller is being told about with one of its own.
    try {
      this.onclose?.(this.#closed);
    } catch (error) {
      console.warn(`the onclose handler of the "${this.#name}" channel threw`, error);
    }
  }

  /// Lets go of the stream, once, however the channel ended.
  async _release() {}
}

/// The end that opened the channel and sends on it.
export class ChannelWriter extends Channel {
  // Frames queue behind one another rather than racing: the order they arrive
  // in is the only thing a channel promises, and two senders awaiting the same
  // stream would interleave their bytes.
  #sending = Promise.resolve();

  async send(frame) {
    if (!frame.length) {
      // Not a limit: a frame of no bytes is what ends a channel on the wire.
      throw new LinkError("a channel frame must carry at least one byte");
    }
    if (!this.open) {
      throw new LinkClosedError(`the "${this.name}" channel has ended`);
    }

    const sent = this.#sending.then(() => writeChannelFrame(this._stream, frame));
    // Kept whatever happens, so one failed frame does not leave every later
    // one waiting on a rejection nobody handles.
    this.#sending = sent.catch(() => {});
    try {
      await sent;
    } catch (error) {
      await this.close(ChannelCloseReason.SessionEnded, error.message);
      throw new LinkClosedError(`the "${this.name}" channel ended with its session`, { cause: error });
    }
  }

  async _release(reason) {
    if (reason === ChannelCloseReason.LocalClosed) {
      // The zero-length marker is what tells the other end that the frames
      // stopped on purpose rather than with the session, and it queues behind
      // them for the same reason they queue behind one another: written
      // beside a frame still going out, it would arrive inside that frame's
      // bytes. Best-effort: a session that has already died cannot carry it,
      // and the peer learns the same thing from the stream ending.
      this.#sending = this.#sending.then(() => writeChannelFrame(this._stream, new Uint8Array(0))).catch(() => {});
    }
    // After whatever is still queued, so closing cannot cut a frame in half.
    await this.#sending;
    await this._stream.close().catch(() => {});
  }
}

/// The end the peer opened a channel into.
export class ChannelReader extends Channel {
  /// Yields frames in the order they were sent, until the channel ends.
  /// Reading is what paces the sender, so a handler that falls behind slows
  /// the other machine down rather than losing frames.
  async *read() {
    for (;;) {
      let frame;
      try {
        frame = await readChannelFrame(this._stream);
      } catch (error) {
        await this.close(ChannelCloseReason.SessionEnded, error.message);
        return;
      }

      if (frame === null) {
        await this.close(ChannelCloseReason.PeerClosed, "the peer closed the channel");
        return;
      }
      yield frame;
    }
  }

  // The stream belongs to the loop serving this channel, which closes it once
  // the handler has returned; closing it here would cut the handler off from
  // frames it is still reading. A session that ended is the exception: closing
  // it is what unparks a handler waiting for frames that can no longer arrive.
  async _release(reason) {
    if (reason === ChannelCloseReason.SessionEnded) {
      await this._stream.close().catch(() => {});
    }
  }
}

async function writeChannelFrame(stream, frame) {
  await stream.write(concat(u32be(frame.length), frame));
}

/// One frame, or null once the other end has closed the channel on purpose.
async function readChannelFrame(stream) {
  const length = readU32be(await stream.readExactly(4));
  return length ? await stream.readExactly(length) : null;
}
