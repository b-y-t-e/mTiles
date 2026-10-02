// Reading the one thing that travels between two machines by hand.
//
// An invitation code is a ConnBlob and a pairing token with a "." between
// them. The blob is "tc" plus base64url of a CBOR map holding the host's
// public key and the relay region it listens in — the region matters as much
// as the key, because a node is reachable in the region it chose and a bare
// key sends into the wrong one.

import { base64urlDecode, str } from "./bytes.js";

const PREFIX = "tc";
const TOKEN_SEPARATOR = ".";

/// Only what a ConnBlob contains: maps with text keys, byte strings, text,
/// unsigned and negative integers, and arrays. Anything else in there is not
/// something this client knows how to want.
function decodeCbor(bytes) {
  let at = 0;

  const byte = () => {
    if (at >= bytes.length) throw new Error("the address ends mid-value");
    return bytes[at++];
  };

  const count = (info) => {
    if (info < 24) return info;
    if (info === 24) return byte();
    if (info === 25) return (byte() << 8) | byte();
    if (info === 26) return ((byte() << 24) | (byte() << 16) | (byte() << 8) | byte()) >>> 0;
    if (info === 27) {
      let value = 0n;
      for (let i = 0; i < 8; i++) value = (value << 8n) | BigInt(byte());
      return Number(value);
    }
    throw new Error(`unsupported CBOR length ${info}`);
  };

  const value = () => {
    const initial = byte();
    const major = initial >> 5;
    const info = initial & 0x1f;
    switch (major) {
      case 0:
        return count(info);
      case 1:
        return -1 - count(info);
      case 2: {
        const n = count(info);
        return bytes.slice(at, (at += n));
      }
      case 3: {
        const n = count(info);
        return str(bytes.slice(at, (at += n)));
      }
      case 4: {
        const n = count(info);
        return Array.from({ length: n }, value);
      }
      case 5: {
        const n = count(info);
        const map = {};
        for (let i = 0; i < n; i++) map[value()] = value();
        return map;
      }
      case 7:
        if (info === 20) return false;
        if (info === 21) return true;
        if (info === 22) return null;
        throw new Error(`unsupported CBOR simple value ${info}`);
      default:
        throw new Error(`unsupported CBOR major type ${major}`);
    }
  };

  return value();
}

/// The host's key and the region it listens in.
export function parseAddress(blob) {
  const text = blob.trim();
  if (!text.startsWith(PREFIX)) {
    throw new Error(`an address starts with "${PREFIX}"; this one starts "${text.slice(0, 4)}"`);
  }

  const decoded = decodeCbor(base64urlDecode(text.slice(PREFIX.length)));
  const serverPublic = decoded.p;
  if (!(serverPublic instanceof Uint8Array) || serverPublic.length !== 32) {
    throw new Error("the address carries no usable public key");
  }

  // Either an embedded region or a region id. This client only handles the
  // id: an embedded region names its own relays, which is a thing tailcat
  // supports and nothing here has needed.
  const regionId = decoded.i ?? 0;
  if (!regionId) {
    throw new Error("the address embeds its own relay list, which this client cannot read yet");
  }

  return { value: text, serverPublic, regionId };
}

/// The address plus the secret that buys one pairing with it.
export function parseInvitationCode(text) {
  const trimmed = (text ?? "").trim();
  const dot = trimmed.indexOf(TOKEN_SEPARATOR);
  if (dot < 0) {
    throw new Error('that is not an invitation code; it should start with "tc" and carry a token after a "."');
  }
  const address = parseAddress(trimmed.slice(0, dot));
  const pairingToken = trimmed.slice(dot + 1);
  if (!pairingToken) throw new Error("the invitation code has no pairing token");
  return { address, pairingToken };
}
