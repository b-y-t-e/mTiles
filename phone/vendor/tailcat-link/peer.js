// What two nodes say to each other through a relay they do not trust.
//
// Every control message is sealed in a NaCl box between the two node keys.
// The relay routes by public key but holds neither private one, so it can
// neither read a hello nor forge one — without that it could hand either end
// an attacker's key material and watch the rest.

import { concat, equal, readU32be, u32be, u64be, utf8 } from "./bytes.js";
import { nacl } from "./nacl.js";

export const PeerMessageType = {
  Hello: 0x01,
  HelloAck: 0x02,
  Ping: 0x03,
  Pong: 0x04,
  Data: 0x05,
  EndpointUpdate: 0x06,
  Relay1Record: 0x07,
};

export const PeerTransport = {
  Quic: 0,
  Relay1: 1,
};

export const MAGIC = utf8("TCN1");
export const HEADER_LEN = 5;
const NONCE_LEN = 24;
const FINGERPRINT_LEN = 32;
export const EPHEMERAL_LEN = 32;

export const isPeerMessage = (message) =>
  message.length >= HEADER_LEN && equal(message.slice(0, 4), MAGIC);

export const peerMessageType = (message) => message[4];

export function seal(type, payload, selfPrivate, peerPublic) {
  const nonce = nacl.randomBytes(NONCE_LEN);
  const box = nacl.box(payload, nonce, peerPublic, selfPrivate);
  return concat(MAGIC, new Uint8Array([type]), nonce, box);
}

export function open(message, selfPrivate, peerPublic) {
  if (!isPeerMessage(message) || message.length < HEADER_LEN + NONCE_LEN) return null;
  const nonce = message.slice(HEADER_LEN, HEADER_LEN + NONCE_LEN);
  const opened = nacl.box.open(message.slice(HEADER_LEN + NONCE_LEN), nonce, peerPublic, selfPrivate);
  return opened ? { type: message[4], payload: opened } : null;
}

// sessionId(8) | fingerprint(32) | homeRegion(4) | endpoints | transports |
// ephemeral(32, only when relay1 is on the table)
export function encodeHello({ sessionId, homeRegionId, transports, ephemeral }) {
  return concat(
    u64be(sessionId),
    new Uint8Array(FINGERPRINT_LEN), // no TLS here, so nothing to fingerprint
    u32be(homeRegionId),
    new Uint8Array([0]), // no endpoints: a browser has no address to punch to
    new Uint8Array([transports.length]),
    new Uint8Array(transports),
    ephemeral ?? new Uint8Array(0),
  );
}

export function decodeHello(payload) {
  if (payload.length < 8 + FINGERPRINT_LEN + 4 + 1) {
    throw new Error("a hello that short cannot be one");
  }
  let at = 8 + FINGERPRINT_LEN;
  const homeRegionId = readU32be(payload, at);
  at += 4;

  const endpointCount = payload[at++];
  const endpoints = [];
  for (let i = 0; i < endpointCount; i++) {
    const addrLen = payload[at++];
    if (addrLen !== 4 && addrLen !== 16) throw new Error("a hello names an address of impossible length");
    endpoints.push(payload.slice(at, at + addrLen));
    at += addrLen + 2;
  }

  // An empty list is what a node built before the negotiation sends, and QUIC
  // is the only transport such a node has.
  const transports = [];
  if (at < payload.length) {
    const count = payload[at++];
    for (let i = 0; i < count; i++) transports.push(payload[at++]);
  }
  if (!transports.length) transports.push(PeerTransport.Quic);

  const ephemeral = payload.length >= at + EPHEMERAL_LEN ? payload.slice(at, at + EPHEMERAL_LEN) : null;
  return { sessionId: null, homeRegionId, endpoints, transports, ephemeral };
}
