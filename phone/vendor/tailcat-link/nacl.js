// The one primitive WebCrypto does not have.
//
// A sealed hello is a NaCl box — X25519, HSalsa20 and XSalsa20-Poly1305 —
// because that is what the .NET side uses and what a DERP relay demands at
// login. WebCrypto offers none of it, so tweetnacl supplies it. Everything
// else this client needs (X25519 for the ephemeral exchange, HKDF, AES-GCM)
// is WebCrypto, and boxes only ever run over handshake-sized messages.
//
// Two ways to provide it, because there are two ways this client is used:
//
//   - Bundled. `npm install tweetnacl`, and the bare import below resolves.
//   - Loaded by the page. A <script> tag ahead of the module sets the global,
//     which is what the example does — it has no build step to resolve a bare
//     specifier with.

let resolved = globalThis.nacl ?? null;

if (!resolved) {
  try {
    resolved = (await import("tweetnacl")).default;
  } catch {
    // No bundler and no script tag. Say so where it can be acted on rather
    // than failing later inside a handshake.
  }
}

if (!resolved) {
  throw new Error(
    "tailcat-link needs tweetnacl: install it (npm install tweetnacl) or load " +
      "its UMD build in a <script> tag before this module",
  );
}

/// The tweetnacl namespace, however it was provided.
export const nacl = resolved;

export default resolved;
