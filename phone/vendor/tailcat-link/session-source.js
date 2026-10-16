// Where the next session comes from.
//
// The browser only ever dials, so there is one source here where the .NET
// side has two (`SessionSources.cs`); what the split buys is the same thing
// it buys there. Everything about reaching a relay, agreeing a transport and
// deriving the traffic keys lives here, and the reconnect loop above knows
// none of it — which is also what lets a test stand the whole link up against
// a session that never touched a socket, the way
// `TailcatNodeOptions.ConnectRelay` does on the .NET side.

import { equal, withTimeout } from "./bytes.js";
import { DerpConnection, relayHostFor } from "./derp.js";
import {
  PeerMessageType,
  PeerTransport,
  decodeHello,
  encodeHello,
  isPeerMessage,
  open as openPeerMessage,
  peerMessageType,
  seal,
} from "./peer.js";
import { Relay1Session, deriveKeys } from "./relay1.js";
import { offerPairing } from "./pairing-handshake.js";
import { nacl } from "./nacl.js";

/// One session and the relay connection carrying it, as one thing to own.
///
/// The layer above opens streams, accepts them and lets go; that it is a
/// relay underneath rather than QUIC is this class's business alone.
export class RelayedConnection {
  #derp;
  #session;
  #peerPublic;

  constructor({ derp, session, peerPublic }) {
    this.#derp = derp;
    this.#session = session;
    this.#peerPublic = peerPublic;
    this.relayInfo = derp.serverInfo;
    derp.onclose = (error) => session.close(error);
    this.#pumpRecords();
  }

  get accepted() {
    return this.#session.accepted;
  }

  get closed() {
    return this.#session.closed;
  }

  openStream() {
    return this.#session.openStream();
  }

  /// Resolves once everything written has left the socket. Written is not
  /// sent: closing on top of a full send buffer throws the answers away.
  flush(timeoutMs) {
    return this.#derp.flush(timeoutMs);
  }

  close(error = new Error("the session was closed")) {
    this.#session.close(error);
    this.#derp.close();
  }

  // Records arrive on the relay's queue and belong to the session; everything
  // else is somebody else talking to this key and is dropped.
  #pumpRecords() {
    (async () => {
      try {
        for (;;) {
          const packet = await this.#derp.packets.next();
          // The sender first, as TailcatNode does: a record from anyone else
          // cannot open, and handleRecord answers a record that will not open
          // by closing the session. Without this, one packet from anybody who
          // knows this browser's public key ends the session, over and over.
          if (!equal(packet.source, this.#peerPublic)) continue;
          if (!isPeerMessage(packet.payload)) continue;
          if (peerMessageType(packet.payload) !== PeerMessageType.Relay1Record) continue;
          await this.#session.handleRecord(packet.payload);
        }
      } catch (error) {
        this.#session.close(error);
      }
    })();
  }
}

/// The joining end: it knows where the host is, dials it, and does not hand
/// the session on until the pairing has been accepted.
export class DialingSessionSource {
  #dial;
  #hello;
  #handshakeTimeout;

  /// @param dial how a connection is reached; the seam a test replaces.
  /// @param displayName what the host is to call this browser. A hint the
  ///   host authenticates in no way, and it may be left out.
  constructor({ dial, pairingToken, displayName = null, handshakeTimeout }) {
    this.#dial = dial;
    this.#hello = { pairingToken, displayName };
    this.#handshakeTimeout = handshakeTimeout;
  }

  /// Produces the next session, already paired.
  async nextSession(signal) {
    const connection = await this.#dial(signal);
    try {
      // Before anything else is asked, because until it has run the host will
      // refuse everything anyway.
      await withTimeout(offerPairing(connection, this.#hello), this.#handshakeTimeout, "the host");
      return connection;
    } catch (error) {
      connection.close(error);
      throw error;
    }
  }
}

/// Reaches the host over a relay: the DERP map, the WebSocket, the sealed
/// hello, the ephemeral exchange, and a relay1 session on top of the lot.
export function relayDialer({ options, identity, peer }) {
  return async (signal) => {
    const host = options.relayHost ?? (await relayHostFor(options.derpMap, peer.regionId));
    const derp = await DerpConnection.connect({
      url: `wss://${host}/derp`,
      privateKey: identity.secretKey,
      publicKey: identity.publicKey,
      signal,
    });

    try {
      const keys = await negotiate({ derp, options, identity, peer });
      const session = new Relay1Session({ derp, peerPublic: peer.serverPublic, keys, isDialer: true });
      return new RelayedConnection({ derp, session, peerPublic: peer.serverPublic });
    } catch (error) {
      derp.close();
      throw error;
    }
  };
}

// The hello and its answer: which transport the two have in common, and the
// two ephemeral halves the traffic keys come out of.
async function negotiate({ derp, options, identity, peer }) {
  const sessionId = Number(BigInt.asUintN(48, BigInt(Date.now())) * 1000n + BigInt(Math.floor(Math.random() * 1000)));
  const ephemeral = nacl.box.keyPair();

  derp.sendPacket(
    peer.serverPublic,
    seal(
      PeerMessageType.Hello,
      encodeHello({
        sessionId,
        homeRegionId: peer.regionId,
        transports: [PeerTransport.Relay1],
        ephemeral: ephemeral.publicKey,
      }),
      identity.secretKey,
      peer.serverPublic,
    ),
  );

  const ack = await withTimeout(awaitHelloAck(derp, identity, peer), options.handshakeTimeout, "the host");
  if (ack.transports.length !== 1 || ack.transports[0] !== PeerTransport.Relay1) {
    throw new Error(
      `no transport in common: this client speaks [relay1], the host answered [${ack.transports.join(", ")}]`,
    );
  }
  if (!ack.ephemeral) throw new Error("the host agreed to relay1 without sending an ephemeral key");

  return deriveKeys({
    ephemeralPrivate: ephemeral.secretKey,
    peerEphemeral: ack.ephemeral,
    sessionId,
    dialerPublic: identity.publicKey, // this end dialled
    hostPublic: peer.serverPublic,
  });
}

async function awaitHelloAck(derp, identity, peer) {
  for (;;) {
    const packet = await derp.packets.next();
    if (!isPeerMessage(packet.payload)) continue;
    if (peerMessageType(packet.payload) !== PeerMessageType.HelloAck) continue;
    const opened = openPeerMessage(packet.payload, identity.secretKey, peer.serverPublic);
    if (!opened) continue;
    return decodeHello(opened.payload);
  }
}
