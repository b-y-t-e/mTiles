// What the browser says about itself when it dials: which invitation it
// holds, and what to call it. `LinkHello.cs` is the .NET half.
//
// Version 1 of this put the pairing token on the wire on its own, with no
// envelope around it. The version byte is outside the base64url alphabet a
// token is written in, which is what lets a host tell the two shapes apart
// without a flag anywhere.
//
// The display name is a hint and nothing more: it comes from this machine,
// which may say anything, and the only thing a session authenticates is the
// public key.

import { concat, str, u16be, utf8 } from "./bytes.js";
import { LinkError } from "./errors.js";

const VERSION_2 = 0x02;

/// The most a display name may be. It is shown in a list of devices, not
/// stored as a document, and a peer must not be able to make a host write
/// megabytes to disk by naming itself.
export const MAX_DISPLAY_NAME_BYTES = 256;

/// Writes the hello for the frame that opens a session.
export function encodeLinkHello({ pairingToken, displayName = null }) {
  const token = utf8(pairingToken ?? "");
  const name = displayName ? utf8(displayName) : new Uint8Array(0);
  if (name.length > MAX_DISPLAY_NAME_BYTES) {
    throw new LinkError(`a display name may be at most ${MAX_DISPLAY_NAME_BYTES} bytes, this one is ${name.length}`);
  }
  return concat(new Uint8Array([VERSION_2]), u16be(token.length), token, u16be(name.length), name);
}

/// Reads what the other machine sent, in either shape.
export function decodeLinkHello(payload) {
  if (!payload.length || payload[0] !== VERSION_2) {
    // A bare pairing token: everything written before this envelope existed.
    return { pairingToken: str(payload), displayName: null };
  }

  let at = 1;
  const field = () => {
    if (payload.length < at + 2) throw new LinkError("the peer's hello ended mid-field");
    const length = (payload[at] << 8) | payload[at + 1];
    if (payload.length < at + 2 + length) throw new LinkError("the peer's hello ended mid-field");
    const value = payload.subarray(at + 2, at + 2 + length);
    at += 2 + length;
    return value;
  };

  const pairingToken = str(field());
  const name = field();
  if (name.length > MAX_DISPLAY_NAME_BYTES) {
    throw new LinkError(`the peer named itself in ${name.length} bytes; the limit is ${MAX_DISPLAY_NAME_BYTES}`);
  }
  return { pairingToken, displayName: name.length ? str(name) : null };
}
