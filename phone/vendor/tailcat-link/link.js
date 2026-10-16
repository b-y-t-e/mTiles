// The durable link: what a page holds on to.
//
// A session dies for many reasons — the relay went away, the tab was
// backgrounded, the host rebooted, the network changed underneath — and from
// here they all look the same and all get the same answer. Nothing above this
// is told; a request simply waits for the next session and is re-sent with
// the id it already had, which is what stops the far end running it twice.
//
// Only the reconnection lives here. What one session does is `link-session.js`
// and where a session comes from is `session-source.js`, the same split
// `DurableLink.cs` keeps on the .NET side.

import { Deferred, delay, utf8, withTimeout } from "./bytes.js";
import { parseAddress, parseInvitationCode } from "./address.js";
import { LinkClosedError, LinkError, LinkTimeoutError, PairingRefusedError } from "./errors.js";
import { ExchangeFlags } from "./exchange-frame.js";
import { EXCHANGE_RETENTION_MS, ExchangeRegistry } from "./incoming-exchange.js";
import { LinkContent, asContent } from "./link-content.js";
import { ExchangeSender } from "./outbound-exchange.js";
import { DialingSessionSource, relayDialer } from "./session-source.js";
import { LinkSession } from "./link-session.js";
import { ExchangeLedger } from "./exchange-ledger.js";
import { IndexedDbStore } from "./store.js";
import { nacl } from "./nacl.js";

const DEFAULTS = {
  derpMap: "/derpmap.json",
  requestTimeout: 30_000,
  // How long a request or notification sent as a string or bytes may go with
  // nothing moving. Silence, not a deadline on the whole: see LinkOptions.RequestDeadline.
  requestDeadline: 90_000,
  // The same for content sent as LinkContent, and for transfers, which more
  // things may legitimately hold up: see LinkOptions.TransferStallTimeout.
  transferStallTimeout: 120_000,
  heartbeatInterval: 20_000,
  minReconnectDelay: 500,
  maxReconnectDelay: 30_000,
  handshakeTimeout: 20_000,
};

export class TailcatLink {
  #options;
  #source;
  #store;
  #handler = null;
  #notifyHandler = null;
  #transferHandler = null;
  #sessionEnded = new Deferred();
  #exchanges;
  #sender;
  #channels = new Map();
  #streams = new Map();
  #session = null;
  #connected = new Deferred();
  #stopped = false;
  #fatal = null;
  #closing = new AbortController();
  #ledger;

  constructor({ options, source, store }) {
    this.#options = options;
    // Longer than the far end may keep retrying one request, so that its last
    // retry still meets the answer its first attempt produced. `LinkProtocol`
    // ties the two together the same way, from the other side.
    this.#ledger = new ExchangeLedger({ retention: options.requestDeadline + 30_000 });
    this.#source = source;
    this.#store = store;
    this.events = new EventTarget();

    // On the link, not the session: what arrived before a session died is
    // exactly what the next session carries on from.
    this.#exchanges = new ExchangeRegistry({
      requests: () => asHandler(this.#handler),
      notifications: () => asHandler(this.#notifyHandler) ?? asHandler(this.#handler),
      transfers: () =>
        this.#transferHandler &&
        (async (content) => {
          await this.#transferHandler(content);
          return null;
        }),
      retentionMs: EXCHANGE_RETENTION_MS,
      ackPatienceMs: options.requestTimeout,
      // Named and measured rather than decoded: a request of any size would
      // otherwise become a string of the same size only to sit in an event.
      answered: (request, answer) =>
        this.#emit("answered", { name: request.name, requestLength: request.length, length: answer.length }),
      unheardFailure: (error) => this.#emit("answer-failed", error),
    });
    this.#sender = new ExchangeSender({
      sessions: {
        current: () => this.#sessionAsync(),
        ended: () => this.#sessionEnded.promise,
        stopping: this.#closing.signal,
        fatal: () => this.#fatal,
      },
      minReconnectDelay: options.minReconnectDelay,
    });
  }

  /// Brings up the end that connects to a host. The invitation code is needed
  /// the first time only; afterwards it is stored, and joining again needs
  /// nothing from anybody.
  ///
  /// @param displayName what the host is to list this browser as, matching
  ///   `JoinRequest.DisplayName` on the .NET side. It is a hint the host
  ///   authenticates in no way, so nothing may be keyed off it.
  static async join({ appName, invitationCode = null, displayName = null, ...rest }) {
    if (!appName) throw new Error("a link needs an appName to store its identity under");
    const options = { ...DEFAULTS, ...rest };
    const store = options.store ?? IndexedDbStore;

    const saved = (await store.load(appName)) ?? {};
    const identity = saved.privateKey ? nacl.box.keyPair.fromSecretKey(saved.privateKey) : nacl.box.keyPair();

    let peer;
    let pairingToken;
    if (invitationCode) {
      const code = parseInvitationCode(invitationCode);
      peer = code.address;
      pairingToken = code.pairingToken;
    } else if (saved.peerAddress) {
      peer = parseAddress(saved.peerAddress);
      pairingToken = saved.pairingToken;
    } else {
      throw new Error(`this browser has not been paired for "${appName}" yet; pass the host's invitation code once`);
    }

    await store.save(appName, {
      privateKey: identity.secretKey,
      peerAddress: peer.value,
      pairingToken,
    });

    const source = new DialingSessionSource({
      // `dial` is the seam a test replaces to stand the whole link up without
      // a socket, as TailcatNodeOptions.ConnectRelay does on the .NET side.
      dial: options.dial ?? relayDialer({ options, identity, peer }),
      pairingToken,
      displayName,
      handshakeTimeout: options.handshakeTimeout,
    });

    const link = new TailcatLink({ options: { ...options, appName }, source, store });
    link.#run();
    return link;
  }

  /// Forgets the identity and the pairing, so the next join demands a code.
  static async forget(appName, store = IndexedDbStore) {
    await store.remove(appName);
  }

  /// Answers whatever the host asks, of whatever size. A notification arrives
  /// here too, as a request whose answer is thrown away.
  ///
  /// The handler is given `(text, bytes, content)`: the request as text, as
  /// bytes, and as the content it arrived as, with the name, content type and
  /// metadata the host sent beside it. It may answer with text, bytes, or a
  /// `LinkContent` carrying its own. It runs once per request, however many
  /// sessions the request takes to arrive.
  onRequest(handler) {
    this.#handler = handler;
  }

  /// Called for messages the host sent without expecting an answer, with the
  /// same `(text, bytes, content)` a request handler is given.
  onNotify(handler) {
    this.#notifyHandler = handler;
  }

  /// Takes what the host sends as transfers — content kept apart from
  /// requests, finished for the host once this handler has returned. The
  /// handler is given the content, whole. A link without one refuses them.
  onTransfer(handler) {
    this.#transferHandler = handler;
  }

  /// Takes the channels the host opens under `name`. The handler is given the
  /// channel and keeps receiving for as long as it stays inside `read()`;
  /// when it returns, the channel is over. A name nothing is listening for is
  /// refused rather than swallowed.
  onChannel(name, handler) {
    this.#channels.set(name, handler);
  }

  /// Opens a channel to the host: ordered within itself, and not durable.
  /// Unlike a request it is not carried across a reconnection — it ends with
  /// the session under it, and `channel.closed` says which way it ended.
  ///
  /// @throws {RemoteHandlerError} if the host is not listening for that name.
  async openChannel(name, { timeout } = {}) {
    let session;
    try {
      session = await withTimeout(this.#sessionAsync(), timeout ?? this.#options.requestTimeout, "the host");
    } catch (error) {
      // Waiting is all a channel can do about a link that is down: it is not
      // carried across a reconnection, so there is nothing to retry into.
      throw asLinkError(error);
    }
    return session.openChannel(name);
  }

  /// Takes the two-way streams the host opens under `name`. The handler is
  /// given the stream; when it returns the stream is closed, and if it throws
  /// the stream is aborted, so the host is told it did not finish.
  onStream(name, handler) {
    this.#streams.set(name, handler);
  }

  /// Opens a two-way stream to the host: bytes in order both ways, `finish()`
  /// to end this end's writes while still reading, and `read()` resolving
  /// with an empty array only at a clean end. Like a channel it is not carried
  /// across a reconnection.
  ///
  /// @throws {RemoteHandlerError} if the host is not listening for that name.
  async openStream(name, { timeout } = {}) {
    let session;
    try {
      session = await withTimeout(this.#sessionAsync(), timeout ?? this.#options.requestTimeout, "the host");
    } catch (error) {
      throw asLinkError(error);
    }
    return session.openStream(name);
  }

  get connected() {
    return this.#session !== null && !this.#session.closed;
  }

  /// Resolves once a session is up. Sending does not need it — a request
  /// waits by itself — but a page that wants to show a state does.
  async waitUntilConnected(timeout = this.#options.requestDeadline) {
    if (this.connected) return;
    try {
      await withTimeout(this.#connected.promise, timeout, "the host");
    } catch (error) {
      throw asLinkError(error);
    }
  }

  /// Sends a request of any size and resolves with the answer, waiting through
  /// as many reconnections as it takes and carrying on from where each end got
  /// to. The host's handler runs once.
  ///
  /// Text is answered with text and bytes with bytes. A `LinkContent` is
  /// answered with the content the answer arrived as — its `text`, `bytes`,
  /// `contentType` and `metadata` — which is how a request says more about
  /// itself than its bytes.
  ///
  /// @param timeout how long nothing may move before the request is given up
  ///   on. Silence, not a deadline on the whole, so a request that keeps
  ///   moving is never given up on however large it is.
  async request(message, { timeout, signal } = {}) {
    if (message instanceof LinkContent) {
      return this.#sender.request(message, timeout ?? this.#options.transferStallTimeout, signal);
    }
    const answer = await this.#sender.request(bytesOf(message), timeout ?? this.#options.requestDeadline, signal);
    return typeof message === "string" ? answer.text : answer.bytes;
  }

  /// Sends a message the host is not expected to answer. It is delivered once,
  /// through reconnections, and resolves when the host has all of it.
  async notify(message, { timeout, signal } = {}) {
    const patience = message instanceof LinkContent ? this.#options.transferStallTimeout : this.#options.requestDeadline;
    await this.#sender.deliver(bytesOf(message), ExchangeFlags.AckOnDelivery, timeout ?? patience, signal);
  }

  /// Sends content to the host's transfer handler, and resolves once that
  /// handler has finished with it.
  async send(message, { timeout, signal } = {}) {
    await this.#sender.deliver(
      bytesOf(message),
      ExchangeFlags.Transfer,
      timeout ?? this.#options.transferStallTimeout,
      signal,
    );
  }

  /// Stops the link, but not before the answers already produced have left.
  async close({ drainTimeout = this.#options.requestTimeout } = {}) {
    this.#stopped = true;
    // A connect in flight owns a connection this method cannot see yet — the
    // session is assigned only once the source has produced one — so the
    // attempt itself is abandoned rather than left to finish into a link
    // nobody holds.
    this.#closing.abort(new LinkClosedError("the link was closed"));

    const session = this.#session;
    await session?.drain(drainTimeout);
    session?.close(new LinkClosedError("the link was closed"));
    // Nobody will come back for them, and a timer each would otherwise hold
    // them for the whole retention window.
    this.#exchanges.expireAll();
  }

  async #sessionAsync() {
    for (;;) {
      if (this.#session && !this.#session.closed) return this.#session;
      if (this.#stopped) throw this.#fatal ?? new LinkClosedError("the link is closed");
      await this.#connected.promise;
    }
  }

  // ---- the loop that keeps a session up -------------------------------

  async #run() {
    let attempt = 0;
    while (!this.#stopped) {
      try {
        const connection = await this.#source.nextSession(this.#closing.signal);
        // close() while a connect was running found no session and so closed
        // nothing; without this check the connection just made would be
        // served forever, holding the relay open for a link nobody holds.
        if (this.#stopped) {
          connection.close(new LinkClosedError("the link was closed"));
          return;
        }

        this.#session = this.#openSession(connection);
        attempt = 0;
        if (!this.#connected.settled) this.#connected.resolve();
        this.#emit("connected");
        await this.#session.serve();
      } catch (error) {
        if (error instanceof PairingRefusedError) await this.#giveUp(error);
        else if (!this.#stopped) this.#emit("disconnected", error);
      }

      this.#session?.close();
      this.#session = null;
      // Wakes every exchange that was waiting for this session to end before
      // trying again, so the next attempt meets the next session.
      this.#sessionEnded.resolve();
      this.#sessionEnded = new Deferred();
      // Not after a refusal: that rejection is the answer every waiter is
      // owed, and replacing it with a fresh Deferred would park them instead.
      if (this.#connected.settled && !this.#fatal) this.#connected = new Deferred();

      if (this.#stopped) return;
      const wait = Math.min(
        this.#options.maxReconnectDelay,
        this.#options.minReconnectDelay * 2 ** Math.min(attempt++, 8),
      );
      await delay(wait);
    }
  }

  // The handlers are passed as functions rather than values so that a page
  // that calls onRequest after connecting still answers.
  #openSession(connection) {
    return new LinkSession({
      connection,
      handler: () => this.#handler,
      notifyHandler: () => this.#notifyHandler,
      channels: (name) => this.#channels.get(name) ?? null,
      streams: (name) => this.#streams.get(name) ?? null,
      ledger: this.#ledger,
      exchanges: this.#exchanges,
      options: this.#options,
      emit: (name, detail) => this.#emit(name, detail),
    });
  }

  // A refused pairing is not a session that broke: the code is wrong or spent,
  // and only a new one can help. Reconnecting on the usual schedule would ask
  // the host the same rejected question every half minute while every caller
  // here saw nothing but a deadline, so the link stops and says why.
  async #giveUp(error) {
    this.#stopped = true;
    this.#fatal = error;
    // join() stores the code before anything has tried it, so a refused one is
    // already on disk and would be offered again on the next load of the page.
    try {
      await this.#store.remove(this.#options.appName);
    } catch {
      // The pairing outliving its refusal is worth less than the failure that
      // is about to be reported.
    }
    if (!this.#connected.settled) this.#connected.reject(error);
    this.#emit("failed", error);
  }

  #emit(name, detail) {
    this.events.dispatchEvent(new CustomEvent(name, { detail }));
  }
}

// A page's handler, as the registry runs it: given the content, returning content.
function asHandler(handler) {
  return handler ? async (content) => asContent(await handler(content.text, content.bytes, content)) : null;
}

// Text, bytes or content, as content.
function bytesOf(message) {
  if (message instanceof LinkContent) return message;
  return LinkContent.fromBytes(typeof message === "string" ? utf8(message) : message);
}

// `withTimeout` speaks in plain Errors, because bytes.js knows nothing about
// links; what an application catches has to be the taxonomy's.
function asLinkError(error) {
  return error instanceof LinkError ? error : new LinkTimeoutError(error.message, { cause: error });
}
