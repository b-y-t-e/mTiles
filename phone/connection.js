// The link to one mTiles, and what it says.
//
// The invitation code arrives in the URL's fragment, which a browser never sends to any server — so the
// host of this page never sees it. It is read once, used to pair, and taken out of the address bar.
// Each machine this browser has paired with gets a stored identity of its own, named after the machine's
// public key, so one phone can hold a desktop and a laptop.

import { invitationCodeFrom } from "./computers.js";
import { TailcatLink, parseInvitationCode, PairingRefusedError } from "./vendor/tailcat-link/index.js";

export const PROTOCOL = 2;
const MACHINES = "mtiles.machines";
const LAST = "mtiles.lastMachine";

/** The machines this browser is paired with, in the order they were first paired. */
export function knownMachines() {
  try { return JSON.parse(localStorage.getItem(MACHINES) ?? "[]"); } catch { return []; }
}

/** Keeps a machine's place in the list and refreshes its name: every connection says hello, and a list
 *  that reordered itself on each one would move the rows under the user's finger. */
function remember(machine) {
  const list = knownMachines();
  const at = list.findIndex((m) => m.id === machine.id);
  if (at >= 0) list[at] = machine;
  else list.push(machine);
  localStorage.setItem(MACHINES, JSON.stringify(list));
}

/** The machine the page opens on next time: the one last on screen. */
export function setLastMachine(id) {
  localStorage.setItem(LAST, id);
}

export function forgetMachine(id) {
  localStorage.setItem(MACHINES, JSON.stringify(knownMachines().filter((m) => m.id !== id)));
  if (localStorage.getItem(LAST) === id) localStorage.removeItem(LAST);
  return TailcatLink.forget(appNameFor(id)).catch(() => {});
}

export function lastMachineId() {
  return localStorage.getItem(LAST);
}

const appNameFor = (id) => `mtiles-phone-${id}`;

/** Reads a code out of the page's fragment, and takes it out of the address bar. */
export function takeInvitationFromUrl() {
  const hash = location.hash.slice(1);
  if (!hash) return null;
  history.replaceState(null, "", location.pathname + location.search);
  return parseInvitation(hash);
}

/** An invitation out of what the user pasted: the whole link, or only the code after its `#`. Null for
 *  anything else. */
export function parseInvitation(text) {
  const code = invitationCodeFrom(text);
  if (!code) return null;
  try {
    const { address } = parseInvitationCode(code);
    return { code, id: hex(address.serverPublic).slice(0, 12) };
  } catch {
    return null;
  }
}

function hex(bytes) {
  return Array.from(bytes ?? [], (b) => b.toString(16).padStart(2, "0")).join("");
}

function deviceName() {
  const ua = navigator.userAgent;
  const device = /iPhone/.test(ua) ? "iPhone" : /iPad/.test(ua) ? "iPad" : /Android/.test(ua) ? "Android phone"
    : /Mac/.test(ua) ? "Mac" : /Windows/.test(ua) ? "Windows PC" : "Browser";
  const browser = /Edg\//.test(ua) ? "Edge" : /Firefox\//.test(ua) ? "Firefox" : /CriOS|Chrome\//.test(ua) ? "Chrome"
    : /Safari\//.test(ua) ? "Safari" : "";
  return browser ? `${device} · ${browser}` : device;
}

/** What a request is told when the connection was closed under it, rather than left unanswered. */
function closedError() {
  return new Error("The connection was closed.");
}

/**
 * One mTiles, connected. `onPush(message)` is called for everything it pushes; `onState(state)` with
 * "connecting", "connected", "reconnecting" or "refused".
 */
export class Connection {
  #link = null;
  #closed = false;
  #onPush;
  #onState;

  constructor({ onPush, onState }) {
    this.#onPush = onPush;
    this.#onState = onState;
  }

  get link() { return this.#link; }

  async open({ id, code = null }) {
    this.#onState("connecting");
    const link = await TailcatLink.join({
      appName: appNameFor(id),
      invitationCode: code,
      displayName: deviceName(),
      derpMap: new URL("derpmap.json", location.href).href,
    });
    // Closed while the relay was being joined: the link would live on with nobody listening to it.
    if (this.#closed) { await link.close().catch(() => {}); throw closedError(); }
    this.#link = link;
    // Joined means mTiles now holds this phone as paired: stored before the hello, so a hello that fails
    // leaves a computer the list can still retry and log out of after a reload.
    remember({ id, name: knownMachines().find((m) => m.id === id)?.name ?? "mTiles" });

    link.onNotify((text) => {
      try { this.#onPush(JSON.parse(text)); } catch { /* not ours to understand */ }
    });
    link.onRequest(() => "");

    link.events?.addEventListener?.("failed", () => this.#onState("refused"));
    link.events?.addEventListener?.("disconnected", () => this.#onState("reconnecting"));
    link.events?.addEventListener?.("connected", () => this.#onState("connected"));

    const hello = await this.request({ type: "hello", protocol: PROTOCOL });
    // A computer logged out of meanwhile must not come back into the list of paired ones.
    if (this.#closed) throw closedError();
    remember({ id, name: hello.machine ?? "mTiles" });
    this.#onState("connected");
    return hello;
  }

  /** Asks mTiles something. Resolves with its answer, or throws an Error carrying its sentence. */
  async request(message) {
    // Closed — logged out of, or replaced by a retry — is said as what it is, not as a computer that
    // did not answer.
    if (this.#closed) throw closedError();
    // Still joining the relay: not closed, and not a computer that failed to answer either.
    if (!this.#link) throw new Error("Not connected to mTiles yet.");
    let text;
    try {
      text = await this.#link.request(JSON.stringify(message));
    } catch (error) {
      if (this.#closed) throw closedError();
      if (error instanceof PairingRefusedError) {
        this.#onState("refused");
        throw new Error("This phone is no longer paired with that computer. Scan a new code.");
      }
      throw new Error("mTiles did not answer. It may be off, asleep, or offline.");
    }
    const answer = JSON.parse(text);
    if (answer.ok === false) {
      throw Object.assign(new Error(answer.error ?? "mTiles could not do that."), { code: answer.code ?? null });
    }
    return answer;
  }

  async close() {
    this.#closed = true;
    const link = this.#link;
    this.#link = null;
    await link?.close().catch(() => {});
  }
}
