// What goes to the other machine — a request, a notification, a transfer, or
// the answer a handler gives — with what it says about itself. `LinkContent.cs`
// is the .NET half.
//
// A kilobyte of text and a large recording are the same thing here: neither
// has a size limit, the content travels in blocks, and it carries on from
// where the other machine got to when a session dies. In a page the content is
// held in memory, which is what a page has.

import { utf8 } from "./bytes.js";

export class LinkContent {
  #bytes;

  /// @param bytes the content. Not copied.
  /// @param name what the content is called. Shown to the other machine, never
  ///   used by it as a path.
  /// @param contentType the media type, when there is one worth saying.
  /// @param metadata whatever the application wants to travel in front of the
  ///   content; it arrives before the content does.
  /// @param progress told `(sent, total)` after each block that leaves.
  constructor({ bytes = new Uint8Array(0), name = "", contentType = "", metadata = new Uint8Array(0), progress = null } = {}) {
    this.#bytes = bytes;
    this.name = name;
    this.contentType = contentType;
    this.metadata = metadata;
    this.progress = progress;
  }

  static fromBytes(bytes, options = {}) {
    return new LinkContent({ ...options, bytes });
  }

  static fromString(text, options = {}) {
    return new LinkContent({ contentType: "text/plain; charset=utf-8", ...options, bytes: utf8(text) });
  }

  /// What a handler with nothing to say returns.
  static get empty() {
    return new LinkContent();
  }

  get bytes() {
    return this.#bytes;
  }

  get length() {
    return this.#bytes.length;
  }

  /// The same content with some of what it says about itself changed.
  with(changes) {
    return new LinkContent({
      bytes: this.#bytes,
      name: this.name,
      contentType: this.contentType,
      metadata: this.metadata,
      progress: this.progress,
      ...changes,
    });
  }
}

/// Whatever a handler returned, as content: a LinkContent as it is, bytes and
/// text as the bytes they are, and nothing as nothing.
export function asContent(value) {
  if (value instanceof LinkContent) return value;
  if (value instanceof Uint8Array) return LinkContent.fromBytes(value);
  if (value === undefined || value === null) return LinkContent.empty;
  return LinkContent.fromBytes(utf8(String(value)));
}
