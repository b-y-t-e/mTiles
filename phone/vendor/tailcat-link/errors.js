// What went wrong, in the shapes an application can catch apart from one
// another.
//
// `LinkExceptions.cs` is the .NET half and the names line up one for one:
// PairingRefusedError is PairingRefusedException, and so on down the list.
// InvitationExpiredException is the one exception, and deliberately: only a
// host knows an invitation lapsed, it refuses in the same words either way,
// and this end — which only ever joins — could never raise it honestly.
// Two ports that disagree about which failures are worth telling apart are
// two ports an application has to learn twice.
//
// Every name is assigned from a literal rather than from the class, so that a
// bundler free to rename classes cannot rename what a page prints.

/// Anything this library raises on its own behalf.
export class LinkError extends Error {
  constructor(message, options) {
    super(message, options);
    this.name = "LinkError";
  }
}

/// The host would not have this browser. Deliberately says nothing about
/// which part was wrong — an expired invitation, a wrong token and a host
/// that is already full are one answer, so that nothing here helps anybody
/// search for the right one.
export class PairingRefusedError extends LinkError {
  constructor(message, options) {
    super(message, options);
    this.name = "PairingRefusedError";
  }
}

/// Nothing came back inside the time the caller was willing to wait. A link
/// that is merely down does not raise this: a request waits through a
/// reconnection. This is what is left when the waiting itself ran out.
export class LinkTimeoutError extends LinkError {
  constructor(message, options) {
    super(message, options);
    this.name = "LinkTimeoutError";
  }
}

/// The link or the channel has been closed for good.
export class LinkClosedError extends LinkError {
  constructor(message, options) {
    super(message, options);
    this.name = "LinkClosedError";
  }
}

/// A two-way stream stopped without a clean end. `ending` is one of
/// `StreamEnding`, and says what happened: `LinkStreamException.Ending` in .NET.
export class LinkStreamError extends LinkError {
  constructor(ending, message, options) {
    super(message, options);
    this.name = "LinkStreamError";
    this.ending = ending;
  }
}

/// The far end's handler failed. Not a broken session: retrying it would only
/// run it again.
export class RemoteHandlerError extends LinkError {
  constructor(message, options) {
    super(message, options);
    this.name = "RemoteHandlerError";
  }
}
