// The hello, offered on every session before anything else is asked.
//
// It is presented again on each reconnection because this end cannot tell
// whether the machine it reached still remembers it: a host that was reset is
// indistinguishable from one that never paired. `PairingHandshake.cs` is the
// .NET half, and answers the same way.

import { str } from "./bytes.js";
import { PairingRefusedError } from "./errors.js";
import { encodeLinkHello } from "./link-hello.js";
import { FrameKind, FrameStatus, HELLO_FRAME_BYTES, newExchange, readFrame, writeFrame } from "./link-frame.js";

/// Presents this browser's `hello` on `connection` and waits to be let in.
///
/// @param hello `{ pairingToken, displayName }`; the name is what the host
///   lists this browser as, and is authenticated by nothing.
/// @throws {PairingRefusedError} if the host will not have this browser.
export async function offerPairing(connection, hello) {
  const stream = connection.openStream();
  try {
    await writeFrame(stream, FrameKind.Hello, newExchange(), encodeLinkHello(hello));
    // Bounded: read before either end knows the other, and the machine that
    // answers may not be the one the invitation meant.
    const answer = await readFrame(stream, undefined, { limit: HELLO_FRAME_BYTES });
    if (answer.tag !== FrameStatus.Ok) {
      throw new PairingRefusedError(str(answer.payload));
    }
  } finally {
    await stream.close().catch(() => {});
  }
}

// Re-exported because it was raised from here before it had a module of its
// own, and `import { PairingRefusedError } from "./pairing-handshake.js"` is
// in applications already written.
export { PairingRefusedError };
