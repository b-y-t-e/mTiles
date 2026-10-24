// Byte handling the rest of the client is written against. Nothing here is
// tailcat-specific; it exists so that no other file has to reason about
// offsets and endianness twice.

export const utf8 = (s) => new TextEncoder().encode(s);
export const str = (b) => new TextDecoder().decode(b);

export const hex = (bytes) => [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");

export const unhex = (s) => {
  const clean = s.replace(/[^0-9a-fA-F]/g, "");
  if (clean.length % 2) throw new Error("a hex string must have an even number of digits");
  return new Uint8Array(clean.match(/../g)?.map((h) => parseInt(h, 16)) ?? []);
};

export const equal = (a, b) => a.length === b.length && a.every((v, i) => v === b[i]);

export const concat = (...parts) => {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
};

export const u16be = (n) => {
  const out = new Uint8Array(2);
  new DataView(out.buffer).setUint16(0, n);
  return out;
};

export const u32be = (n) => {
  const out = new Uint8Array(4);
  new DataView(out.buffer).setUint32(0, n >>> 0);
  return out;
};

export const readU32be = (b, at = 0) => new DataView(b.buffer, b.byteOffset + at, 4).getUint32(0);

export const u64be = (n) => {
  const out = new Uint8Array(8);
  new DataView(out.buffer).setBigUint64(0, BigInt(n));
  return out;
};

export const readU64be = (b, at = 0) => new DataView(b.buffer, b.byteOffset + at, 8).getBigUint64(0);

export const base64urlDecode = (s) => {
  const padded = s.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((s.length + 3) % 4);
  const binary = atob(padded);
  return Uint8Array.from(binary, (c) => c.charCodeAt(0));
};

export const randomBytes = (n) => crypto.getRandomValues(new Uint8Array(n));

/// A promise that rejects when the signal fires, so a wait can be abandoned.
export const withAbort = (promise, signal) => {
  if (!signal) return promise;
  return Promise.race([
    promise,
    new Promise((_, reject) => {
      if (signal.aborted) reject(signal.reason ?? new Error("aborted"));
      else signal.addEventListener("abort", () => reject(signal.reason ?? new Error("aborted")), { once: true });
    }),
  ]);
};

/// Rejects after a delay unless the promise settles first. A relayed session
/// stays writable after the peer has gone, so silence is what a broken link
/// looks like and every wait on one needs an end.
export const withTimeout = (promise, ms, what) => {
  let timer;
  return Promise.race([
    promise.finally(() => clearTimeout(timer)),
    new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error(`${what} did not answer within ${ms} ms`)), ms);
    }),
  ]);
};

export const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/// A promise whose settling is somebody else's business.
export class Deferred {
  constructor() {
    this.settled = false;
    this.promise = new Promise((resolve, reject) => {
      this.resolve = (value) => {
        this.settled = true;
        resolve(value);
      };
      this.reject = (error) => {
        this.settled = true;
        reject(error);
      };
    });
    // Nothing may await this until somebody does; without it an unhandled
    // rejection is reported for a queue that was simply abandoned.
    this.promise.catch(() => {});
  }
}

/// An unbounded queue that a reader can await.
export class Queue {
  constructor() {
    this.items = [];
    this.waiters = [];
    this.closed = null;
  }

  push(item) {
    const waiter = this.waiters.shift();
    if (waiter) waiter.resolve(item);
    else this.items.push(item);
  }

  close(error = null) {
    this.closed = error ?? new Error("closed");
    for (const w of this.waiters) w.reject(this.closed);
    this.waiters = [];
  }

  get length() {
    return this.items.length;
  }

  async next(signal) {
    if (this.items.length) return this.items.shift();
    if (this.closed) throw this.closed;
    const waiter = new Deferred();
    this.waiters.push(waiter);
    return withAbort(waiter.promise, signal);
  }
}
