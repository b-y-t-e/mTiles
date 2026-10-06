// The phone page: one mTiles, seen at three distances.
//
//   workspaces  — every workspace, with the mark that says whether anything in it wants you
//   layout      — one workspace as the desktop lays it out, drawn to scale; tap a tile to zoom in
//   tile        — one tile: the conversation or the screen, what it is waiting for, and a dock to answer
//
// Going up a level is the back button, the header's back arrow, or a pinch. Everything shown is what
// mTiles pushed or answered; nothing here decides what a tile is allowed to do.

import { h, fill, activityMark, icon } from "./dom.js";
import { parse, render, renderLinked, linkSpansAcross, renderSpans } from "./markdown.js";
import { layoutRects, isLegible, readingOrder } from "./geometry.js";
import { Connection, knownMachines, lastMachineId, setLastMachine, takeInvitationFromUrl, parseInvitation, forgetMachine, PROTOCOL } from "./connection.js";
import { Dictation } from "./dictation.js";
import { ago } from "./format.js";
import { classify, trimBlank, displayable } from "./screen.js";
import { DraftMirror } from "./mirror.js";
import { isUp, attentionOf, computerStatus } from "./computers.js";
import { orderWorkspaces, matchesQuery, FILTER_THRESHOLD } from "./workspaces.js";

const KINDS = {
  terminal: "Terminal", agent: "Terminal agent", "agent-conversation": "Agent", goal: "Goal", spec: "Spec",
  note: "Note", todo: "Todo", git: "Git", database: "Database", usage: "Usage",
};

const KIND_ACCENT = {
  terminal: "terminal", agent: "agent", "agent-conversation": "agent", goal: "goal", spec: "spec", note: "note",
  todo: "todo", git: "git", database: "database", usage: "usage",
};

const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");

const state = {
  layout: null,           // RemoteLayout of state.workspaceId, on the computer on screen
  tile: null,             // RemoteTileView of state.tileId
  level: "workspaces",    // computers | workspaces | layout | tile
  workspaceId: null,
  tileId: null,
  answers: {},            // pendingId -> { questionId -> [labels] }
  answerChips: 0,         // moves when a chip is toggled — typing does not redraw, or the field loses focus
  sending: false,
};

// ── computers ─────────────────────────────────────────────────────────────────────────────────
//
// Every computer this phone is paired with is connected at once, not only the one on screen: that is
// what lets the list of computers say which one needs you, and what makes switching to one instant. Each
// holds its own link and its own picture of its workspaces; `state.conn`, `state.workspaces` and the rest
// of the fields below always mean the computer on screen.

/** id -> { id, name, conn, connState, hello, session, workspaces, clockOffset, error } */
const machines = new Map();
let currentId = null;

const MACHINE_DEFAULTS = {
  conn: null,
  connState: "unpaired",  // unpaired | connecting | connected | reconnecting | refused | failed | page-old | app-old
  hello: null,
  session: null,          // { dictation: { available, state, tileId }, activeTileId }
  workspaces: [],
  clockOffset: 0,         // mTiles' clock less this phone's, from the hello — cards' times are mTiles'
  error: null,
};

for (const key of Object.keys(MACHINE_DEFAULTS)) {
  Object.defineProperty(state, key, {
    get: () => machines.get(currentId)?.[key] ?? MACHINE_DEFAULTS[key],
    set: (value) => { const m = machines.get(currentId); if (m) m[key] = value; },
  });
}
Object.defineProperty(state, "machine", { get: () => machines.get(currentId) ?? null });

function machineFor(id) {
  let m = machines.get(id);
  if (!m) {
    const known = knownMachines().find((k) => k.id === id);
    m = { id, name: known?.name ?? "mTiles", ...MACHINE_DEFAULTS, workspaces: [] };
    machines.set(id, m);
  }
  return m;
}

const isCurrent = (m) => m.id === currentId;

/** Whether a computer other than the one on screen has something waiting for you. */
const elsewhereNeedsYou = () => [...machines.values()].some((m) => !isCurrent(m) && attentionOf(m).blocked > 0);

const root = document.getElementById("app");
const bar = h("header.bar");
const view = h("main.view");
const dock = h("footer.dock");
const toast = h("div.toast", { role: "status", "aria-live": "polite" });
root.append(bar, view, dock, toast);

const dictation = new Dictation({ getLink: () => state.conn?.link, onError: (m) => say(m, "error") });

// ── connection ────────────────────────────────────────────────────────────────────────────────

async function start() {
  const invitation = takeInvitationFromUrl();
  const known = knownMachines();
  const first = invitation?.id ?? lastMachineId() ?? known.at(-1)?.id;

  if (!first) {
    draw();
    return;
  }

  // Back from a computer's workspaces is the list of computers, so that is where the history starts.
  currentId = first;
  machineFor(first);
  history.replaceState({ level: "computers" }, "");
  go("workspaces", { machineId: first });

  connect(first, invitation?.code ?? null);
  for (const m of known) {
    if (m.id !== first) connect(m.id, null);
  }
}

async function connect(id, code) {
  const m = machineFor(id);
  // The new link is this computer's before anything is awaited, so a second call cannot leave two alive,
  // and a link that has been replaced is not listened to any more.
  const previous = m.conn;
  const conn = new Connection({
    onPush: (message) => { if (m.conn === conn) onPush(m, message); },
    onState: (next) => { if (m.conn === conn) onConnectionState(m, next); },
  });
  m.conn = conn;
  await previous?.close();
  if (m.conn !== conn) return;
  m.workspaces = [];
  m.connState = "connecting";
  m.error = null;
  if (isCurrent(m)) { state.layout = null; state.tile = null; }
  draw();

  try {
    const hello = await conn.open({ id, code });
    if (m.conn !== conn) return; // replaced meanwhile — a retry, or a log-out
    m.hello = hello;
    m.name = hello.machine ?? m.name;
    if (isCurrent(m)) setLastMachine(id);
    if (!hello.compatible) {
      m.connState = hello.protocol > PROTOCOL ? "page-old" : "app-old";
      draw();
      return;
    }
    m.session = hello.session ?? null;
    m.clockOffset = typeof hello.now === "number" ? hello.now - Date.now() : 0;
    const { workspaces } = await conn.request({ type: "workspaces" });
    m.workspaces = workspaces;
    m.connState = "connected";
    // mTiles remembers what a phone watched across a reconnect, so a computer in the background is told
    // outright to push nothing but its list.
    if (isCurrent(m)) await watch();
    else watchNothing(m);
  } catch (error) {
    if (m.conn !== conn) return;
    // Only a join that never succeeded is abandoned: once the relay has joined, mTiles holds this phone as
    // paired and the stored identity is what "Try again" reconnects with.
    if (code && !conn.link && !isPaired(id)) { abandonPairing(m, error.message); return; }
    m.connState = /no longer paired|not been paired/.test(error.message) ? "refused" : "failed";
    m.error = error.message;
  }
  draw();
}

/** Out of reach and not trying: connecting again is offered only then, so a pairing still under way is
 *  not replaced by a join without its code. */
const isDown = (m) => !isUp(m) && m.connState !== "connecting";

const isPaired = (id) => knownMachines().some((k) => k.id === id);

/** A first pairing that failed leaves nothing behind: without a pairing there is nothing to retry, and
 *  the code it came with is spent or wrong. */
function abandonPairing(m, reason) {
  const conn = m.conn;
  m.conn = null;
  conn?.close();
  machines.delete(m.id);
  say(`Pairing failed: ${reason}`, "error");
  // Somebody who has moved on to another computer meanwhile is left where they are.
  if (!isCurrent(m)) { draw(); return; }
  putNextOnScreen();
  go(currentId ? "computers" : "workspaces");
}

function onPush(m, message) {
  if (message.type === "text" && message.draft && !isShowingTile(m, message.tileId)) {
    keepDictatedFor(m, message);
    return;
  }
  // An upload's answer is awaited whichever computer is on screen now: it names the draft it goes into.
  if (message.type === "attached") { attached(m, message); return; }
  if (!isCurrent(m)) {
    // A computer in the background is listened to only for what the list of computers shows.
    const before = elsewhereNeedsYou();
    if (message.type === "session") m.session = message;
    else if (message.type === "workspaces") m.workspaces = message.workspaces ?? [];
    else { sayFrom(m, message); return; }
    if (state.level === "computers" || before !== elsewhereNeedsYou()) draw();
    return;
  }

  switch (message.type) {
    case "session": m.session = message; break;
    case "workspaces": m.workspaces = message.workspaces ?? []; break;
    case "layout":
      if (message.layout?.workspaceId === state.workspaceId) state.layout = message.layout;
      break;
    case "tile":
      if (message.tile?.tileId === state.tileId) state.tile = message.tile;
      break;
    case "tileGone":
      if (message.tileId === state.tileId) { say("That tile was closed on the computer."); go("layout"); return; }
      break;
    case "text":
      // Asked for in this box: it goes there, to be read before it is sent.
      if (message.draft) { takeDictated(message.message, message.send === true); return; }
      sayFrom(m, message);
      break;
    case "error":
      sayFrom(m, message);
      break;
    default:
      return;
  }
  draw();
}

function isShowingTile(m, tileId) {
  return isCurrent(m) && state.level === "tile" && tileId === state.tileId;
}

/** A sentence asked for in a box the page has since left: the computer counts it delivered, so it waits
 *  in that tile's draft for the way back rather than living only in a toast. Never sent from there. */
function keepDictatedFor(m, message) {
  const heard = message.message?.trim();
  if (!heard || !message.tileId) return;
  const key = `${m.id}/${message.tileId}`;
  const kept = drafts.get(key) ?? { text: "", adopted: null };
  kept.text = kept.text.trim() ? `${kept.text.trimEnd()} ${heard}` : heard;
  drafts.set(key, kept);
  sayFrom(m, message);
}

/** What a computer says back — a dictated sentence heard, or an error — is said whichever computer is on
 *  screen, named when it is not the one on screen, so a sentence sent before switching is not lost. */
function sayFrom(m, message) {
  if (message.type !== "text" && message.type !== "error") return;
  const from = isCurrent(m) ? "" : `${m.name}: `;
  if (message.type === "text") say(`${from}Heard: ${message.message}`);
  else say(`${from}${message.message}`, "error");
}

/** A link back after a drop meets an mTiles that may have restarted and forgotten what this page watches. */
function onConnectionState(m, next) {
  // While the hello and the first list are on their way, "connected" is `connect`'s to say.
  if (next === "connected" && m.connState === "connecting") return;
  const reconnected = m.connState === "reconnecting" && next === "connected";
  m.connState = next;
  draw();
  if (!reconnected) return;
  if (isCurrent(m)) watch();
  else watchNothing(m);
}

/** Tells a computer in the background to push no layout and no tile: only its list is listened to. */
function watchNothing(m) {
  m.conn?.request({ type: "watch", workspaceId: null, tileId: null }).catch(() => {});
}

/** Tells mTiles what this page is looking at, so that is what it pushes. */
async function watch() {
  if (!state.conn || state.connState !== "connected") return;
  const workspaceId = depth(state.level) <= depth("workspaces") ? null : state.workspaceId;
  const tileId = state.level === "tile" ? state.tileId : null;
  try { await state.conn.request({ type: "watch", workspaceId, tileId }); } catch { /* the next push corrects it */ }
}

async function ask(message) {
  try {
    return await state.conn.request(message);
  } catch (error) {
    say(error.message, "error");
    return null;
  }
}

// ── navigation ────────────────────────────────────────────────────────────────────────────────

/** Moves to a level. `from` is the element the zoom grows out of, when there is one. */
function go(level, { machineId, workspaceId, tileId, from } = {}, push = true) {
  const before = state.level;
  // The list of computers belongs to none of them, so reaching it — Back included — keeps the current one.
  // What was left is named on the computer it belonged to, before a switch moves the page to another.
  const leftTile = before === "tile" ? state.tileId : null;
  const leftMachine = currentId;
  // The last keystrokes' mirror goes out now, for the tile being left, rather than being dropped with it.
  if (leftTile && (level !== "tile" || tileId !== leftTile)) mirror.flush();
  if (level !== "computers" && machineId && machineId !== currentId && machines.has(machineId)) switchTo(machineId);
  state.level = level;
  if (workspaceId !== undefined) state.workspaceId = workspaceId;
  if (tileId !== undefined) state.tileId = tileId;
  const enteredTile = level === "tile" ? state.tileId : null;
  if (leftTile !== enteredTile) {
    // A draft belongs to the tile it was typed for, and waits there for the way back; a recording aimed
    // at a tile left behind is thrown away.
    if (dictation.active) dictation.finish(true);
    // Which of the computer's drafts this box took over travels with it, or a send on the way back would
    // not be allowed to replace it.
    if (leftTile) drafts.set(`${leftMachine}/${leftTile}`, { text: draft, adopted: adoptedDraft });
    const kept = enteredTile ? drafts.get(`${currentId}/${enteredTile}`) : undefined;
    draft = kept?.text ?? "";
    adoptedDraft = kept?.adopted ?? null;
    remoteDraft = null;
    mirror.forget();
    historyAt = -1;
  }
  if (level !== "tile") { state.tile = null; lastTileKey = lastDockKey = null; }
  if (depth(level) <= depth("workspaces")) state.layout = null;

  if (push) history.pushState({ level, machineId: currentId, workspaceId: state.workspaceId, tileId: state.tileId }, "");

  const origin = from?.getBoundingClientRect();
  draw();
  zoom(origin, depth(level) > depth(before));
  watch();
  holdScreenOn();
  moreBelow(false);

  // An answer is kept only if the page is still looking at what it asked about, on the same computer —
  // the same test a push passes.
  const askedOf = currentId;
  const stillWanted = (wantedLevel) => currentId === askedOf && state.level === wantedLevel;
  if (level === "layout" && !state.layout) ask({ type: "layout", workspaceId: state.workspaceId }).then((a) => {
    if (a?.layout && stillWanted("layout") && a.layout.workspaceId === state.workspaceId) { state.layout = a.layout; draw(); }
  });
  if (level === "tile" && !state.tile) ask({ type: "tile", tileId: state.tileId }).then((a) => {
    if (a?.tile && stillWanted("tile") && a.tile.tileId === state.tileId) { state.tile = a.tile; draw(); scrollToEnd(true); }
  });
}

const depth = (level) => ({ computers: 0, workspaces: 1, layout: 2, tile: 3 })[level] ?? 1;

/** Puts another computer on screen. The one left stops being asked for its layout and tiles — it goes on
 *  telling the list of computers what it is doing. */
function switchTo(id) {
  const leaving = machines.get(currentId);
  if (leaving?.connState === "connected") watchNothing(leaving);
  currentId = id;
  // Remembered only once paired: a pairing cut short by a reload must not be what the page opens on.
  if (id && isPaired(id)) setLastMachine(id);
  state.layout = null;
  state.tile = null;
  state.workspaceId = null;
  state.tileId = null;
  lastTileKey = lastDockKey = null;
  wsQuery = "";
  wsPage = null;
}

/** After the computer on screen is gone — logged out of, or a pairing that failed — the last paired one
 *  takes its place, with nothing of the one that left carried over. */
function putNextOnScreen() {
  switchTo(knownMachines().at(-1)?.id ?? null);
}

addEventListener("popstate", (event) => {
  const s = event.state ?? { level: "computers" };
  // An entry of a computer since logged out of names workspaces and tiles no other computer has.
  if (s.machineId && !machines.has(s.machineId)) { go("computers", {}, false); return; }
  go(s.level, { machineId: s.machineId, workspaceId: s.workspaceId ?? null, tileId: s.tileId ?? null }, false);
});

function up() {
  if (state.level === "computers") return;
  history.back();
}

/** The one motion on this page: the thing tapped grows into the screen, and shrinks back into its
 *  place going up. Answers a gesture, so it is kept even short; skipped when motion is reduced. */
function zoom(origin, inward) {
  if (reducedMotion.matches) return;
  const target = view.getBoundingClientRect();
  if (inward && origin && origin.width > 0) {
    const sx = origin.width / target.width;
    const sy = origin.height / target.height;
    view.animate(
      [
        { transformOrigin: "0 0", transform: `translate(${origin.left - target.left}px, ${origin.top - target.top}px) scale(${sx}, ${sy})`, opacity: 0.4 },
        { transformOrigin: "0 0", transform: "none", opacity: 1 },
      ],
      { duration: 260, easing: "cubic-bezier(.2,.7,.2,1)" },
    );
  } else {
    view.animate([{ transform: "scale(1.06)", opacity: 0.3 }, { transform: "none", opacity: 1 }],
      { duration: 200, easing: "ease-out" });
  }
}

// Pinching in — the gesture for seeing more — goes up a level.
let pinch = null;
view.addEventListener("touchstart", (e) => {
  if (e.touches.length === 2) pinch = spread(e.touches);
}, { passive: true });
view.addEventListener("touchmove", (e) => {
  if (pinch && e.touches.length === 2 && spread(e.touches) < pinch * 0.7) { pinch = null; up(); }
}, { passive: true });
view.addEventListener("touchend", () => { pinch = null; }, { passive: true });
const spread = (t) => Math.hypot(t[0].clientX - t[1].clientX, t[0].clientY - t[1].clientY);

// ── drawing ───────────────────────────────────────────────────────────────────────────────────

function draw() {
  document.body.dataset.level = state.level;
  document.body.dataset.conn = state.connState;

  if (machines.size === 0) {
    fill(bar, h("div.title", h("span.name", "mTiles")));
    fill(view, pairingScreen());
    fill(dock);
    lastTileKey = lastDockKey = null;
    return;
  }

  if (state.level === "computers") {
    drawComputers();
    return;
  }

  if (state.connState !== "connected" && state.connState !== "reconnecting") {
    header({ back: "All computers", title: state.machine?.name ?? "mTiles" });
    fill(view, statusScreen());
    fill(dock);
    lastTileKey = lastDockKey = null;
    wsPage = null;
    return;
  }

  switch (state.level) {
    case "layout": drawLayout(); break;
    case "tile": drawTile(); break;
    default: drawWorkspaces(); break;
  }
}

function statusScreen() {
  const screens = {
    connecting: ["Connecting…", "Reaching mTiles through the relay. This takes a few seconds."],
    refused: ["This phone is not paired any more",
      "It was unpaired on the computer, or the code had already been used. Scan a new code from mTiles."],
    failed: ["mTiles did not answer", state.error ?? "The computer may be off, asleep, or offline."],
    "page-old": ["This page is older than mTiles", "Reload the page to get the current version."],
    "app-old": ["mTiles on the computer needs updating", "This page speaks a newer protocol than that copy of mTiles. Update mTiles, then reload."],
  };
  const [title, text] = screens[state.connState] ?? screens.connecting;
  const m = state.machine;

  return h("section.status-screen",
    state.connState === "connecting" ? activityMark("working") : null,
    h("h1", title),
    h("p", text),
    state.connState === "failed"
      ? h("button.primary", { onclick: () => connect(m.id, null) }, "Try again") : null,
    state.connState === "failed" || state.connState === "refused"
      ? h("button", { onclick: () => logOut(m) }, "Log out of this computer") : null);
}

/** The first screen, and the one an unpaired phone stays on. */
function pairingScreen() {
  return h("section.status-screen",
    h("h1", "Pair this phone with mTiles"),
    h("p", "On the computer, open mTiles and press the QR button beside Settings. Scan the code with this phone's camera."),
    pasteLinkForm());
}

/** The route for a phone that cannot open the camera's link here — a page added to the home screen
 *  opens it in the browser instead. The link is the one under the QR code in mTiles. */
function pasteLinkForm(onDone) {
  const input = h("input.q-custom", {
    type: "url", placeholder: "Or paste the pairing link", autocomplete: "off", spellcheck: false,
    enterKeyHint: "go",
  });
  const pair = () => {
    const invitation = parseInvitation(input.value.trim());
    if (!invitation) { say("That is not a pairing link from mTiles.", "error"); return; }
    onDone?.();
    pairWith(invitation);
  };
  input.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); pair(); } });
  return h("div.paste-link", input, h("button", { onclick: pair }, "Pair"));
}

/** Pairs with a computer from a code, and puts it on screen. */
function pairWith(invitation) {
  machineFor(invitation.id);
  go("workspaces", { machineId: invitation.id });
  connect(invitation.id, invitation.code);
}

function header({ back, title, sub, marks }) {
  // Another computer waiting for you is said on every screen but the list that already says it, and
  // leads there.
  const elsewhere = state.level !== "computers" && elsewhereNeedsYou()
    ? h("button.elsewhere", {
      onclick: () => go("computers"), "aria-label": "Another computer needs you", title: "Another computer needs you",
    }, activityMark("blocked"))
    : null;
  fill(bar,
    back ? h("button.back", { onclick: up, "aria-label": back }, icon("back")) : null,
    h("div.title", h("span.name", title), sub ? h("span.sub", sub) : null),
    elsewhere,
    marks ?? null,
    state.level !== "computers" && state.connState === "reconnecting"
      ? h("span.conn", { title: "Reconnecting" }, activityMark("working")) : null);
}

// computers

function drawComputers() {
  header({ title: "Computers" });

  // In the order they were paired, never by urgency: a row that jumps while you reach for it is worse
  // than one you have to look for, and the mark on it already says which one wants you.
  // A pairing still under way — or one whose hello never came back — is not stored yet, and is listed
  // after the stored ones so it can still be retried or logged out of.
  const stored = knownMachines().map(({ id }) => machineFor(id));
  const pending = [...machines.values()].filter((m) => !stored.includes(m));
  const rows = [...stored, ...pending].map((m) => {
    const attention = attentionOf(m);
    const down = isDown(m);
    return h("div.pc-row", { class: [isCurrent(m) && "current", down && "down", attention.blocked && "blocked"].filter(Boolean).join(" ") },
      h("button.pc-open", {
        onclick: (e) => {
          if (down) connect(m.id, null);
          go("workspaces", { machineId: m.id, from: e.currentTarget });
        },
      },
        h("span.ws-text",
          h("span.ws-name", m.name),
          h("span.ws-meta", computerStatus(m))),
        m.connState === "connecting" ? activityMark("working") : activityMark(attention.activity)),
      h("button.pc-more", { onclick: () => computerSheet(m), "aria-label": `More for ${m.name}` }, icon("more")));
  });

  fill(view, h("div.pc-page",
    h("nav.ws-list", rows),
    h("button.pc-add", { onclick: addComputerSheet }, icon("plus"), "Add a computer")));
  fill(dock);
  lastDockKey = null;
  wsPage = null;
}

/** A panel from the bottom over a dimmed page; a tap on the dimmed part closes it, as Close does. What is
 *  handed back is the whole layer, so `remove()` takes both. */
function sheet(...children) {
  root.querySelector(".sheet-layer")?.remove();
  const el = h("div.sheet-layer",
    h("div.scrim", { onclick: () => el.remove() }),
    h("div.sheet", { role: "dialog" }, children, h("button.quiet", { onclick: () => el.remove() }, "Close")));
  root.append(el);
  return el;
}

function computerSheet(m) {
  const el = sheet(
    h("p.sheet-title", m.name),
    isDown(m) ? h("button", { onclick: () => { el.remove(); connect(m.id, null); } }, "Connect again") : null,
    h("button.danger", { onclick: () => { el.remove(); logOut(m); } }, "Log out of this computer"));
}

function addComputerSheet() {
  const el = sheet(
    h("p.sheet-title", "Add a computer"),
    h("p.sheet-text", "On the other computer, open mTiles and press the QR button beside Settings. Scan the code with this phone's camera — it opens here, beside the computers already paired."),
    pasteLinkForm(() => el.remove()));
}

// workspaces

let wsQuery = "";
let wsPage = null;        // the filter and the list under it, kept so typing never rebuilds the field

function drawWorkspaces() {
  header({ back: "All computers", title: state.machine?.name ?? "mTiles" });

  // Built once and refilled: a push arriving while somebody types must not take the keyboard away.
  if (!wsPage || !view.contains(wsPage.root)) {
    const input = h("input.ws-filter", {
      type: "search", placeholder: "Filter workspaces", autocomplete: "off", spellcheck: false,
      enterKeyHint: "search", value: wsQuery,
      oninput: (e) => { wsQuery = e.target.value; drawWorkspaceRows(); },
    });
    const list = h("nav.ws-list");
    wsPage = { root: h("div.ws-page", input, list), input, list };
    fill(view, wsPage.root);
  }

  const showFilter = state.workspaces.length > FILTER_THRESHOLD;
  wsPage.input.hidden = !showFilter;
  if (!showFilter && wsQuery) { wsQuery = ""; wsPage.input.value = ""; }
  drawWorkspaceRows();
  fill(dock);
  lastDockKey = null;
}

function drawWorkspaceRows() {
  const all = orderWorkspaces(state.workspaces);
  const shown = all.filter((ws) => matchesQuery(ws, wsQuery));

  const rows = shown.map((ws) =>
    h("button.ws-row", {
      class: [ws.current && "current", !ws.loaded && "unloaded"].filter(Boolean).join(" "),
      onclick: (e) => openWorkspace(ws, e.currentTarget),
    },
      h("span.ws-text",
        h("span.ws-name", ws.name),
        h("span.ws-meta", ws.branch ?? (ws.loaded ? "" : "not open"))),
      activityMark(ws.activity),
      ws.favorite ? h("span.ws-pin", { title: "Pinned" }, icon("star")) : null));

  // A filter that leaves nothing says so: a list that empties itself reads as workspaces that have gone.
  fill(wsPage.list,
    all.length === 0 ? h("p.empty", "mTiles has no workspaces yet.")
      : rows.length === 0 ? h("p.empty", `No workspace matches “${wsQuery.trim()}”.`)
        : rows);
}

/** Opening a workspace here opens it on the computer too: the phone is a remote for that screen, and
 *  the two showing different workspaces would leave dictation and the active tile aimed elsewhere. Only
 *  a workspace that is not loaded asks first, because opening it starts its shells and agents. */
async function openWorkspace(ws, from) {
  if (!ws.loaded && !confirm(`Open ${ws.name} on the computer? mTiles switches to it and starts its tiles.`)) return;
  const askedOf = currentId;
  if (!ws.current && !(await ask({ type: "open", workspaceId: ws.id }))) return;
  // Switched to another computer while it was opening: this workspace is not one of that computer's.
  if (currentId !== askedOf || state.level !== "workspaces") return;
  go("layout", { workspaceId: ws.id, from });
}

/**
 * Logs this phone out of one computer: mTiles is asked to forget it — the same as Unpair in its panel —
 * and this browser forgets the pairing. Pairing again needs a new code. With the computer out of reach
 * only the phone's half can be done; the row it leaves in mTiles' panel opens nothing without this
 * phone's key, and Unpair there removes it.
 */
async function logOut(m) {
  if (!m) return;
  if (!confirm(`Log out of ${m.name}? Pairing this phone again needs a new code from mTiles.`)) return;

  const conn = m.conn;
  m.conn = null;
  if (conn && isUp(m)) {
    try { await conn.request({ type: "unpair" }); } catch { /* forgotten here all the same */ }
  }
  await conn?.close();
  await forgetMachine(m.id);
  machines.delete(m.id);

  if (isCurrent(m)) putNextOnScreen();
  say(`Logged out of ${m.name}.`);
  go("computers");
}

// layout

function drawLayout() {
  const ws = state.workspaces.find((w) => w.id === state.workspaceId);
  header({ back: "All workspaces", title: state.layout?.name ?? ws?.name ?? "Workspace", sub: ws?.branch });

  const layout = state.layout;
  if (!layout) { fill(view, h("p.empty", "Loading the layout…")); fill(dock); return; }
  if (!layout.root) { fill(view, h("p.empty", "This workspace has no tiles.")); fill(dock); return; }

  // clientWidth, not getBoundingClientRect: the zoom animates the view with a transform, and a layout
  // that arrives mid-zoom was measured at the animation's scale — a miniature drawn at half size.
  const pad = 8;
  const rects = layoutRects(layout.root, { x: 0, y: 0, w: view.clientWidth - pad * 2, h: view.clientHeight - pad * 2 }, 8);

  if (isLegible(rects)) {
    const canvas = h("div.miniature");
    for (const r of rects) {
      const card = tileCard(r.leaf);
      Object.assign(card.style, { left: `${r.x}px`, top: `${r.y}px`, width: `${r.w}px`, height: `${r.h}px` });
      canvas.append(card);
    }
    fill(view, canvas);
  } else {
    // Too many tiles for a phone to draw to scale: listed in reading order, with the same marks.
    fill(view, h("nav.tile-list", readingOrder(rects).map((r) => tileCard(r.leaf, true))));
  }
  fill(dock);
}

function tileCard(leaf, row = false) {
  const preview = leaf.preview;
  const classes = ["tile-card", row && "row", leaf.active && "active", !leaf.reachable && "unreachable",
    leaf.activity === "blocked" && "blocked", leaf.activity === "working" && "working"].filter(Boolean).join(" ");

  // How long it has been quiet: said only of a tile that is not working, since the arc says the rest.
  const since = preview?.changedAt != null && leaf.activity !== "working"
    ? ago(Date.now() + state.clockOffset - preview.changedAt) : "";
  const context = preview?.contextPercent;

  return h("button", {
    class: classes,
    style: { "--kind": `var(--k-${KIND_ACCENT[leaf.kind] ?? "terminal"})` },
    disabled: !leaf.reachable,
    title: leaf.reachable ? leaf.name : `${leaf.name} — not available from a phone`,
    onclick: (e) => go("tile", { tileId: leaf.tileId, from: e.currentTarget }),
  },
    // The name first — it is what a tile is found by — with its state beside it; the kind under it, quiet
    // and in the kind's colour; then what it is doing, taking whatever room the card has left.
    h("span.tile-head", h("span.tile-name", leaf.name), activityMark(leaf.activity)),
    h("span.tile-kind", KINDS[leaf.kind] ?? leaf.kind,
      since ? h("span.tile-age", { title: "since it last did something" }, ` · ${since}`) : null),
    preview?.text ? h("span.tile-preview", preview.text) : null,
    context != null ? h("span.tile-context", {
      class: context >= 80 ? "high" : "",
      title: `${Math.round(context)}% of the context used`,
      style: { "--used": `${Math.max(0, Math.min(100, context))}%` },
    }) : null);
}

// tile

let transcriptEl = null;
const drawnItems = new Map();

function drawTile() {
  const tile = state.tile;
  const status = tile?.status;
  header({
    back: "Back to the layout",
    title: tile?.name ?? "Tile",
    sub: [status?.detail, status?.contextPercent != null ? `${Math.round(status.contextPercent)}% context` : null]
      .filter(Boolean).join("   "),
    marks: tile ? activityMark(status.activity) : null,
  });

  if (!tile) { fill(view, h("p.empty", "Loading…")); fill(dock); lastTileKey = lastDockKey = null; return; }

  // Unchanged since it was drawn — a push about something else — so nothing under the finger moves: a
  // half-typed answer keeps its focus and its keyboard.
  const key = JSON.stringify([tile.status, tile.chat, tile.screen, tile.list, tile.view, state.answerChips, prefs.screenScroll,
    tile.view === "terminal" ? terminalView(tile) : null]);
  if (key !== lastTileKey || !view.querySelector(".tile-body")) {
    lastTileKey = key;
    drawTileBody(tile);
  }
  drawDock(tile);
}

let lastTileKey = null;

function drawTileBody(tile) {
  const status = tile.status;
  const atEnd = isAtEnd();
  // A terminal is redrawn in place while it streams: a new element each push threw away where the reader
  // had scrolled to — sideways at full size, and back to the end whenever they had scrolled up to read.
  const asChat = tile.view === "terminal" && tile.chat && terminalView(tile) === "chat";
  if (tile.view === "terminal" && !asChat && updateScreenInPlace(tile, atEnd)) return;
  const body = h("div.tile-body",
    h("p.tile-status", { class: status?.activity ?? "", hidden: !status?.text }, status?.text ?? ""),
    tile.view === "terminal" && tile.chat ? terminalTabs(tile) : null,
    tile.view === "chat" || asChat ? chatView(tile)
      : tile.view === "terminal" ? screenView(tile)
        : tile.view === "list" ? listView(tile)
          : actionsOnlyView(tile));
  if (view.firstChild?.classList?.contains("tile-body") && view.firstChild.dataset.tile === tile.tileId) {
    view.firstChild.replaceWith(body);
  } else {
    fill(view, body);
  }
  body.dataset.tile = tile.tileId;
  fitScreen(body.querySelector(".screen"));
  scrollToEnd(atEnd);
  if (!atEnd) moreBelow(true);
  revealOpenedDetail(tile);
}

/** Which of a terminal agent's two views is on screen: the conversation, or the terminal's own screen. */
function terminalView(tile) {
  return localStorage.getItem(`mtiles.termView.${tile.tileId}`) ?? "chat";
}

/** Conversation and Screen: what was said, read from the agent's own record, and what the terminal
 *  shows now — the menu or the question it is waiting on. The one waiting is marked. */
function terminalTabs(tile) {
  const current = terminalView(tile);
  const tab = (id, label, mark) => h("button.term-tab", {
    class: current === id ? "on" : "", "aria-pressed": String(current === id),
    onclick: () => {
      localStorage.setItem(`mtiles.termView.${tile.tileId}`, id);
      lastTileKey = null;
      draw();
      scrollToEnd(true);
    },
  }, label, mark ? activityMark("blocked") : null);
  return h("div.term-tabs",
    tab("chat", "Conversation"),
    tab("screen", "Screen", current === "chat" && tile.status?.activity === "blocked"));
}

/** The screen's text and status, changed where they stand. False when there is nothing to change yet. */
function updateScreenInPlace(tile, atEnd) {
  const body = view.firstChild;
  const pre = body?.dataset?.tile === tile.tileId ? body.querySelector(".screen") : null;
  if (!pre) return false;
  const status = body.querySelector(".tile-status");
  if (status) {
    status.textContent = tile.status?.text ?? "";
    status.hidden = !tile.status?.text;
    status.className = `tile-status ${tile.status?.activity ?? ""}`;
  }
  const left = pre.scrollLeft;
  pre.dataset.lines = JSON.stringify(tile.screen?.lines ?? []);
  fitScreen(pre);
  pre.scrollLeft = left;
  if (atEnd) scrollToEnd(true);
  else moreBelow(true);
  return true;
}

// Scrolled up to read while the tile goes on writing: a way back to the end, rather than being pulled
// there — the rule a chat or a terminal keeps on the computer too.
const toEnd = h("button.to-end", {
  "aria-label": "Jump to the latest", hidden: true,
  onclick: () => { scrollToEnd(true); moreBelow(false); },
}, icon("down"));
root.append(toEnd);
function moreBelow(show) { toEnd.hidden = !show || state.level !== "tile"; }
view.addEventListener("scroll", () => { if (isAtEnd()) moreBelow(false); }, { passive: true });

// ── list: a git tile's changed files, a database tile's databases ─────────────────────────────

let openedDetail = null;    // the row whose detail the reader asked for, to bring into view when it comes

function listView(tile) {
  const list = tile.list ?? { sections: [] };
  return [
    list.sections.map((section) => h("section.list-section",
      h("div.list-head",
        section.checkAll != null ? h("button.tick", {
          class: section.checkAll ? "on" : "", "aria-label": section.checkAll ? "Untick all" : "Tick all",
          onclick: () => itemAct(tile, "*", "check"),
        }, icon("check")) : null,
        h("span.list-title", section.title)),
      section.items.length
        ? h("ul.list", section.items.map((item) => listRow(tile, item)))
        : h("p.list-empty", section.empty ?? ""))),
    list.detail ? detailView(list.detail) : null,
  ];
}

function listRow(tile, item) {
  const tick = item.checked != null ? h("button.tick", {
    class: item.checked ? "on" : "", "aria-label": `${item.checked ? "Untick" : "Tick"} ${item.text}`,
    onclick: () => itemAct(tile, item.id, "check"),
  }, icon("check")) : null;
  const main = h("button.row-main", {
    onclick: () => {
      // Only opening a row happens on a tap of the row: a tick can be a grant, and taking one away is left to
      // the tick itself, never to a stray tap while scrolling.
      if (item.selectable) { openedDetail = item.id; itemAct(tile, item.id, "select"); }
    },
  },
    item.badge ? h("span.badge", { dataset: { status: item.badge } }, item.badge) : null,
    h("span.row-text", h("span.row-name", item.text), item.note ? h("span.row-note", item.note) : null));
  const toggle = item.switch ? h("button.switch", {
    role: "switch", "aria-checked": String(item.switch.on), class: item.switch.on ? "on" : "",
    "aria-label": `${item.switch.label}: ${item.switch.on ? "on" : "off"}`,
    onclick: () => {
      if (!item.switch.on && item.switch.warning && !confirm(item.switch.warning)) return;
      itemAct(tile, item.id, "switch");
    },
  }, h("span.switch-label", item.switch.label), h("span.switch-track", h("span.switch-knob"))) : null;
  return h("li.list-row", { class: item.selected ? "selected" : "" }, tick, main, toggle);
}

function detailView(detail) {
  // Drawn as a code block through markdown.js, so a diff here is coloured by the one rule a chat's is.
  const code = render([{ type: "code", lang: detail.kind === "diff" ? "diff" : "", text: detail.text }]);
  code.firstChild.classList.add("detail-text");
  return h("section.detail", h("p.detail-title", detail.title), code);
}

function revealOpenedDetail(tile) {
  const selected = tile.list?.sections?.flatMap((s) => s.items).find((i) => i.selected);
  if (!openedDetail || selected?.id !== openedDetail || !tile.list?.detail) return;
  openedDetail = null;
  requestAnimationFrame(() => view.querySelector(".detail")?.scrollIntoView({ block: "start", behavior: "smooth" }));
}

async function itemAct(tile, itemId, act) {
  navigator.vibrate?.(8);
  await ask({ type: "item", tileId: tile.tileId, itemId, act });
}

function isAtEnd() {
  return view.scrollHeight - view.scrollTop - view.clientHeight < 80;
}

function scrollToEnd(force) {
  if (force) requestAnimationFrame(() => { view.scrollTop = view.scrollHeight; });
}

function chatView(tile) {
  const chat = tile.chat;
  if (!transcriptEl || transcriptEl.dataset.tile !== tile.tileId) {
    transcriptEl = h("ol.transcript");
    transcriptEl.dataset.tile = tile.tileId;
    drawnItems.clear();
  }

  // Kept by id and rebuilt only where an item changed, so a streaming answer does not re-render the
  // whole conversation four times a second.
  const seen = new Set();
  let previous = null;
  for (const item of chat.items) {
    seen.add(item.id);
    const key = JSON.stringify(item);
    let entry = drawnItems.get(item.id);
    if (!entry || entry.key !== key) {
      const el = chatItem(item);
      if (entry) entry.el.replaceWith(el);
      entry = { key, el };
      drawnItems.set(item.id, entry);
    }
    const expected = previous ? previous.nextSibling : transcriptEl.firstChild;
    if (expected !== entry.el) transcriptEl.insertBefore(entry.el, expected);
    previous = entry.el;
  }
  for (const [id, entry] of drawnItems) {
    if (!seen.has(id)) { entry.el.remove(); drawnItems.delete(id); }
  }

  return [
    chat.omitted ? h("p.omitted", `${chat.omitted} earlier ${chat.omitted === 1 ? "entry" : "entries"} are on the computer.`) : null,
    transcriptEl,
    chat.plan ? planSteps(chat.plan) : null,
    chat.pending ? pendingBlock(tile, chat.pending) : null,
  ];
}

function chatItem(item) {
  const li = h("li.msg", { class: `${item.role} ${item.tone ?? ""}` });
  if (item.role === "work") {
    const details = h("details.work",
      h("summary", item.text),
      h("ul", (item.work ?? []).map((w) => h("li", { class: w.state }, h("span.state"), w.title))));
    if ((item.work ?? []).some((w) => w.state === "running")) details.classList.add("running");
    li.append(details);
  } else if (item.markdown) {
    const body = h("div.md");
    body.append(render(parse(item.text)));
    li.append(body);
  } else {
    li.append(h("pre.plain", renderLinked(item.text)));
  }
  // A review's findings, one row each, the severity in its own colour so a blocker is seen before it is read.
  if (item.findings?.length) {
    li.append(h("ul.findings", item.findings.map((f) => h("li", { class: `sev-${f.severity}` },
      h("span.sev", f.severity),
      f.place ? h("span.place", f.place) : null,
      h("span.title", renderLinked(f.title))))));
  }
  // What was said can be said again, changed: the desktop's Up arrow in the composer, as a tap.
  if (item.role === "user" && item.text?.trim()) {
    li.append(h("button.msg-edit", { "aria-label": "Edit and send again", onclick: () => setDraft(item.text) }, icon("edit")));
  }
  if (item.streaming) li.classList.add("streaming");
  return li;
}

function planSteps(plan) {
  return h("ol.plan", plan.map((s) => h("li", { class: `step-${s.status}` }, s.text)));
}

function pendingBlock(tile, pending) {
  const block = h("section.pending", { class: pending.kind },
    h("p.pending-title", pending.title,
      pending.secondsLeft != null ? h("span.countdown", ` ${pending.secondsLeft}s`) : null));

  if (pending.detail) {
    if (pending.kind === "plan") {
      const md = h("div.md.plan-detail");
      md.append(render(parse(pending.detail)));
      block.append(md);
    } else {
      block.append(h("pre.plain.detail", pending.detail));
    }
  }

  if (pending.kind === "questions") {
    const answers = (state.answers[pending.id] ??= {});
    for (const q of pending.questions ?? []) {
      const chosen = answers[q.id] ?? [];
      block.append(h("div.question",
        q.header ? h("p.q-header", q.header) : null,
        h("p.q-text", q.text),
        h("div.chips", q.options.map((label) => h("button.chip", {
          class: chosen.includes(label) ? "on" : "",
          onclick: () => {
            answers[q.id] = q.multi
              ? (chosen.includes(label) ? chosen.filter((c) => c !== label) : [...chosen, label])
              : [label];
            state.answerChips++;
            draw();
          },
        }, label))),
        q.custom ? h("input.q-custom", {
          type: "text", placeholder: "Or type an answer",
          value: chosen.length === 1 && !q.options.includes(chosen[0]) ? chosen[0] : "",
          oninput: (e) => { answers[q.id] = e.target.value.trim() ? [e.target.value.trim()] : []; },
        }) : null));
    }
    block.append(h("div.options", h("button.primary", {
      onclick: async () => {
        if (await ask({ type: "answer", tileId: tile.tileId, pendingId: pending.id, answers })) delete state.answers[pending.id];
      },
    }, "Send answers")));
  } else if (pending.options.length) {
    block.append(h("div.options", pending.options.map((o) => h("button", {
      class: o.tone,
      onclick: () => ask({ type: "choose", tileId: tile.tileId, pendingId: pending.id, optionId: o.id }),
    }, o.label))));
  }
  return block;
}

function screenView(tile) {
  const pre = h("pre.screen");
  pre.dataset.lines = JSON.stringify(tile.screen?.lines ?? []);
  return pre;
}

/**
 * Draws a terminal's screen to fit the phone's width, with no sideways scrolling: the type shrinks to the
 * widest line down to a size still readable, and past that the lines wrap. A rule drawn across the whole
 * terminal — a line of nothing but ─ — is cut to the width instead, or it would wrap into a stack of
 * rules. The screen is the desktop's, many columns wider than a phone, and a sideways scroll there means
 * reading every line in two halves.
 */
function fitScreen(pre) {
  if (!pre?.isConnected) return;
  const lines = trimBlank(JSON.parse(pre.dataset.lines ?? "[]").map((l) => l.replace(/\s+$/, "")));
  const rows = lines.map(classify);
  pre.classList.toggle("full-size", prefs.screenScroll);
  if (prefs.screenScroll) {
    pre.style.fontSize = "";
  } else {
    const style = getComputedStyle(pre);
    const room = pre.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
    const widest = Math.max(1, ...lines.filter((_, i) => rows[i].kind !== "rule").map((l) => [...l].length));
    const size = Math.max(SCREEN_MIN_PX, Math.min(SCREEN_MAX_PX, room / (widest * charWidth(style.fontFamily))));
    pre.style.fontSize = `${size.toFixed(2)}px`;
  }
  // An address the terminal broke at its width links to the whole address from every line it is on.
  const width = Math.max(0, ...lines.map((l) => [...l].length));
  const spans = linkSpansAcross(rows.map((row) => displayable(row.text)), lines.map((l) => [...l].length >= width));
  // One block per line: a wrapped line goes on under its own text, and a rule is a rule.
  pre.replaceChildren(...rows.map((row, i) => row.kind === "rule"
    ? h("span.ln.rule")
    : h("span.ln", {
      class: row.kind,
      style: { "--indent": `${row.indent + row.hang}ch`, "--hang": `${row.hang}ch` },
    }, row.text ? renderSpans(spans[i]) : "\u00a0")));
}

const SCREEN_MIN_PX = 8.5;
const SCREEN_MAX_PX = 12;
const widths = new Map();

/** How wide one character of this monospace face is, per pixel of type size. */
function charWidth(family) {
  if (!widths.has(family)) {
    const ctx = document.createElement("canvas").getContext("2d");
    ctx.font = `100px ${family}`;
    widths.set(family, ctx.measureText("M".repeat(20)).width / 2000 || 0.6);
  }
  return widths.get(family);
}

function actionsOnlyView(tile) {
  return tile.actions.length
    ? h("div.actions-only", tile.actions.map((a) => actionButton(tile, a)))
    : h("p.empty", "There is nothing to do in this tile from a phone.");
}

function actionButton(tile, a) {
  return h("button", {
    disabled: !a.enabled,
    onclick: async () => {
      if (await ask({ type: "action", tileId: tile.tileId, id: a.id })) say(`${a.label}: started.`);
      draw();
    },
  }, a.label);
}

// dock
//
// Three rows, each shown only when it has something in it: the keys a terminal is driven by (always
// there — hunting for them behind a button is what a phone held at arm's length on a treadmill cannot
// do), the ways to send besides the plain one together with Stop and the history of what was said, and
// the text box itself. Dictation lands in that box rather than straight in the tile, so what was heard is
// read and corrected before it goes — unless the phone's auto-Enter is on in Settings → Speech.

let draft = "";
const drafts = new Map();   // "computer/tile" -> { text typed there and not sent, the computer draft it took over }
let lastDockKey = null;
/** The computer's own unsent draft in this tile, as last pushed. A send replaces only the one taken
 *  into this box (adoptedDraft) — the one the user has actually read in full. */
let remoteDraft = null;
/** The computer's draft the phone's box last took over, so the same one is not taken again after it was
 *  cleared here on purpose. */
let adoptedDraft = null;
let micHeldSince = 0;
/** Where ↑/↓ are in the list of previous messages: -1 is the box as it was before the first ↑. */
let historyAt = -1;
let historyStash = "";

/** The open tile's box, mirrored into the computer's as it is typed (see mirror.js). */
const mirror = new DraftMirror({
  capture: currentMirror,
  isOpen: (target) => state.tileId === target.tileId && currentId === target.machine,
  onLanded: (target) => { adoptedDraft = target.text || null; },
  // Left meanwhile: what the left tile's box holds goes into its kept draft, so the next mirror there
  // names the right text as seen.
  onLeft: (target) => {
    const key = `${target.machine}/${target.tileId}`;
    drafts.set(key, { text: drafts.get(key)?.text ?? "", adopted: target.text || null });
  },
  onError: (message) => say(message, "error"),
});

/** A refused mirror resumes once the computer's box is empty again or holds what this box last saw:
 *  nobody there is writing over it any more, so what is typed here goes on appearing there. */
function resumeMirrorOnceClear() {
  if (!mirror.refused) return;
  if (remoteDraft && remoteDraft !== (mirror.mirrored ?? adoptedDraft)) return;
  mirror.forget();
  adoptedDraft = remoteDraft;
  if (draft !== (remoteDraft ?? "")) mirror.soon();
}

/** Takes the computer's draft into this box: the text there is now what was seen, not what was last mirrored. */
function adopt(text) {
  adoptedDraft = text;
  mirror.forget();
}

/** The mirror the open tile's box asks for now, captured whole so it survives the phone moving on. */
function currentMirror() {
  const tile = state.tile;
  if (!tile?.composer?.syncsDraft || state.level !== "tile" || mirror.refused) return null;
  return { tileId: tile.tileId, machine: currentId, conn: state.conn, text: draft,
    seen: mirror.mirrored ?? adoptedDraft, unchanged: mirror.mirrored ?? remoteDraft ?? "" };
}


const prefs = {
  get keysHidden() { return localStorage.getItem("mtiles.keysHidden") === "1"; },
  get screenScroll() { return localStorage.getItem("mtiles.screenScroll") === "1"; },
  set screenScroll(v) { localStorage.setItem("mtiles.screenScroll", v ? "1" : "0"); },
  set keysHidden(v) { localStorage.setItem("mtiles.keysHidden", v ? "1" : "0"); },
};

const KEYS = [
  ["escape", "Esc"], ["tab", "Tab"], ["shifttab", "⇧Tab"], ["up", null], ["down", null], ["left", null],
  ["right", null], ["ctrlc", "^C"], ["backspace", "⌫"], ["enter", null],
];

/** Puts text in the box to be edited — a previous prompt, the computer's draft — and the keyboard on it. */
function setDraft(text, { focus = true } = {}) {
  draft = text;
  lastDockKey = null;
  draw();
  mirror.soon();
  const input = dock.querySelector(".draft");
  if (input && focus) { input.focus(); input.setSelectionRange(input.value.length, input.value.length); }
}

/** A sentence heard by the computer, for this box: added after what is there, and sent at once when the
 *  phone's auto-Enter is on in the computer's Settings. */
function takeDictated(text, sendNow) {
  const heard = text.trim();
  if (!heard) return;
  draft = draft.trim() ? `${draft.trimEnd()} ${heard}` : heard;
  lastDockKey = null;
  draw();
  if (sendNow && state.tile) send(state.tile, true);
  else mirror.soon();
}

/** How full the context is: a hairline along the dock's top edge, and the settings button turning to a
 *  warning past 80%. Painted on every push, since the dock itself is rebuilt only when what it offers changes. */
function paintContext(context) {
  const tight = context >= 80;
  dock.style.setProperty("--ctx", context != null ? `${Math.max(0, Math.min(100, context))}%` : "0%");
  dock.classList.toggle("tight", tight);
  dock.querySelector(".setting")?.classList.toggle("tight", tight);
}

function drawDock(tile) {
  const composer = tile.composer;
  const dictating = state.session?.dictation?.available;
  const hasActions = tile.actions.length > 0 && tile.view !== "none";
  const hasHistory = historyFor(tile).length > 0;
  const modes = composer.modes ?? [];

  // The computer's draft is taken into an empty box the first time it is seen; into a box holding
  // something it is offered, never pushed over what the thumb is writing.
  remoteDraft = composer.draft ?? null;
  resumeMirrorOnceClear();
  const focused = document.activeElement?.classList?.contains("draft");
  if (remoteDraft && remoteDraft !== adoptedDraft && !draft.trim() && !focused) {
    draft = remoteDraft;
    adopt(remoteDraft);
  }
  const empty = draft.trim().length === 0;
  // What this box mirrored there comes back in every push while the thumb is ahead of it: that is our own
  // text, not somebody else's draft to offer.
  const offerRemote = remoteDraft && remoteDraft !== draft && remoteDraft !== adoptedDraft
    && remoteDraft !== mirror.mirrored && remoteDraft !== mirror.inFlight;

  // Rebuilt only when what it offers changed: rebuilding the text box under somebody's thumb ends their
  // keyboard's composition and throws the caret to the end.
  // The computer's draft is left out: every mirror comes back in it. Only a draft being offered is in the key,
  // so the offer shows what is there now, the text "Edit here" takes.
  const { draft: _computerDraft, ...offered } = composer;
  const sending = [...uploads.values()].filter((u) => u.tileId === tile.tileId);
  const dockKey = JSON.stringify([tile.tileId, offered, tile.actions, tile.newLabel, dictating, prefs.keysHidden,
    empty, hasHistory, offerRemote && remoteDraft, sending.map((u) => u.name)]);
  paintContext(tile.status?.contextPercent);
  if (dockKey === lastDockKey && dock.firstChild) return;
  lastDockKey = dockKey;

  const input = h("textarea.draft", {
    rows: 1,
    placeholder: composer.placeholder || "Type a message",
    disabled: !composer.enabled,
    value: draft,
    enterKeyHint: composer.keys ? "enter" : "send",
    oninput: (e) => {
      const wasEmpty = draft.trim().length === 0;
      draft = e.target.value;
      historyAt = -1;
      grow(e.target);
      mirror.soon();
      // What the chips offer depends on whether anything is typed.
      if (wasEmpty !== (draft.trim().length === 0)) drawDock(tile);
    },
    onkeydown: (e) => { if (e.key === "Enter" && !e.shiftKey && !composer.keys) { e.preventDefault(); send(tile, true); } },
  });

  const mic = dictating ? h("button.mic", {
    class: dictation.recording ? "recording" : "",
    "aria-label": dictation.recording ? "Stop recording" : "Talk — hold, or tap to start and tap to stop",
    onpointerdown: (e) => {
      e.preventDefault();
      if (dictation.active) { stopRecording(false); return; }
      micHeldSince = Date.now();
      e.currentTarget.setPointerCapture(e.pointerId);
      // Marked at once: the recording itself only begins once the microphone is open.
      e.currentTarget.classList.add("recording");
      e.currentTarget.setAttribute("aria-label", "Stop recording");
      startRecording(tile);
    },
    // Held: push to talk, and letting go ends it. Tapped: it keeps listening until the next tap — the
    // hand that is holding a rail on a treadmill cannot also hold a button.
    onpointerup: () => { if (micHeldSince && Date.now() - micHeldSince > 400) stopRecording(false); micHeldSince = 0; },
    onpointercancel: () => { if (micHeldSince) stopRecording(true); micHeldSince = 0; },
    oncontextmenu: (e) => e.preventDefault(),
  }, icon("mic")) : null;

  // A terminal with nothing typed is sent an Enter: the prompt it is sitting on wants answering more
  // often than it wants text.
  const primary = h("button.send", {
    "aria-label": composer.keys && empty ? "Enter" : "Send",
    disabled: !composer.enabled || (empty && !composer.keys),
    onclick: () => (composer.keys && empty ? pressKey(tile, "enter") : send(tile, true)),
  }, icon(composer.keys && empty ? "enter" : "send"));

  const more = hasActions || tile.newLabel || composer.keys || tile.listsConversations
    ? h("button.tool", { "aria-label": "More", onclick: () => moreSheet(tile) }, icon("more"))
    : null;

  // Stop is not in Send's place any more, so it needs no second tap: the desktop's Escape asks nothing.
  const stop = composer.canInterrupt
    ? h("button.tool-btn.stop-btn", { onclick: () => ask({ type: "interrupt", tileId: tile.tileId }), "aria-label": "Stop (Esc)" },
      icon("stop"), "Stop")
    : null;
  // The composer's Up and Down: through what was said before, into the box to be changed and sent again.
  const arrows = !composer.keys && hasHistory ? [
    h("button.tool-btn", { "aria-label": "Previous message", title: "Previous message", onclick: () => stepHistory(tile, 1) }, icon("up")),
    h("button.tool-btn", { "aria-label": "Next message", title: "Next message", onclick: () => stepHistory(tile, -1) }, icon("down")),
  ] : [];
  const history = hasHistory
    ? h("button.tool-btn", { onclick: () => historySheet(tile), "aria-label": "Previous messages", title: "Previous messages" }, icon("history"))
    : null;
  const context = tile.status?.contextPercent;
  // What the next message runs as, in one quiet button: the values joined, and the sheet behind it names
  // them, lets each be changed and holds Compact. Three pickers side by side did not fit a phone's width.
  const pickers = composer.pickers ?? [];
  const settings = pickers.length || composer.canCompact
    ? h("button.setting", {
      class: context >= 80 ? "tight" : "",
      onclick: () => settingsSheet(tile),
      "aria-label": `Settings: ${pickers.map((p) => `${p.label} ${p.valueLabel ?? "not set"}`).join(", ")}`,
    }, h("span.setting-value", pickers.map((p) => shortValue(p)).join(" · ") || "Settings"), icon("chevron"))
    : null;
  // An empty box can only read a goal out of the changes; a typed one can set it, or narrow what is read.
  const modeChips = modes.filter((m) => !empty || m.needsText !== true)
    .map((m) => h("button.mode", { disabled: !m.enabled, onclick: () => sendMode(tile, m) }, m.label));

  const tools = [stop, ...arrows, history].filter(Boolean);

  fill(dock,
    offerRemote ? h("div.remote-draft",
      h("span.remote-text", h("span.muted", "On the computer: "), remoteDraft),
      h("button.mode", { onclick: () => { adopt(remoteDraft); setDraft(remoteDraft); } }, "Edit here")) : null,
    composer.keys && !prefs.keysHidden ? h("div.keys", KEYS.map(([key, label]) => h("button.key", {
      "aria-label": key, class: key === "ctrlc" || key === "escape" ? "warn" : "",
      onclick: () => pressKey(tile, key),
    }, label ?? icon(key)))) : null,
    modeChips.length ? h("div.dock-modes", modeChips) : null,
    tools.length || settings
      ? h("div.dock-bar", h("div.dock-tools", tools), h("div.dock-settings", settings)) : null,
    sending.length ? h("div.uploads", sending.map((u) => h("span.upload", activityMark("working"), u.name))) : null,
    composer.enabled || composer.placeholder
      ? h("div.dock-row", more,
        composer.takesAttachments ? h("button.tool.attach", {
          "aria-label": "Attach a photo or a file", title: "Attach a photo or a file",
          onclick: () => { attachFor = tile.tileId; picker.click(); },
        }, icon("clip")) : null,
        input, mic, primary)
      : more ? h("div.dock-row.only-more", more) : null);
  grow(input);
  if (focused) { input.focus(); input.setSelectionRange(input.value.length, input.value.length); }
}

// ── attachments ──
//
// A photo or a file for the next message, sent on a channel of its own the way a recording is — a request
// is capped at 64 KB and a photo is megabytes. The computer keeps it and answers with the marker that names
// it ([Image #1], @.mtiles/attachments/…), which goes into this box; the box is mirrored into the tile's.
// A photo is scaled to the size the desktop gives a pasted image before it goes, because a phone's camera
// takes twelve megapixels and an agent is sent at most 1568 on the long edge.

const UPLOAD_CHUNK = 48 * 1024;
const UPLOAD_LIMIT = 20 * 1024 * 1024;
const IMAGE_EDGE = 1568;
// How long the computer has to answer a file already sent before the phone says it did not arrive.
const UPLOAD_ANSWER_MS = 60_000;
const uploads = new Map();       // id -> { name, tileId, timer }
let attachFor = null;
const picker = h("input", {
  type: "file", multiple: true, hidden: true,
  onchange: (e) => {
    const tileId = attachFor;
    for (const file of e.target.files) if (tileId) upload(tileId, file);
    e.target.value = "";
  },
});
root.append(picker);

async function upload(tileId, file) {
  const link = state.conn?.link;
  if (!link) { say("Not connected to mTiles.", "error"); return; }
  let name = file.name || "photo.jpg";
  let mime = file.type || "application/octet-stream";
  let bytes = null;
  if (mime.startsWith("image/") && !(mime === "image/png" && file.size < 2_000_000)) {
    const shrunk = await shrinkPhoto(file).catch(() => null);
    if (shrunk) { bytes = shrunk; mime = "image/jpeg"; name = `${name.replace(/\.[^.]+$/, "")}.jpg`; }
  }
  bytes ??= new Uint8Array(await file.arrayBuffer());
  if (bytes.length > UPLOAD_LIMIT) { say(`${name} is larger than 20 MB.`, "error"); return; }

  const id = crypto.randomUUID();
  uploads.set(id, { name, tileId });
  lastDockKey = null;
  draw();
  try {
    const channel = await link.openChannel("attach");
    await channel.send(new TextEncoder().encode(JSON.stringify({ id, tileId, name, mime })));
    for (let at = 0; at < bytes.length; at += UPLOAD_CHUNK) await channel.send(bytes.subarray(at, at + UPLOAD_CHUNK));
    await channel.close();
    const sent = uploads.get(id);
    if (sent) sent.timer = setTimeout(() => uploadFailed(id), UPLOAD_ANSWER_MS);
  } catch {
    uploadFailed(id);
  }
}

/** An upload that went wrong, or that the computer never answered. */
function uploadFailed(id) {
  const upload = uploads.get(id);
  if (!upload) return;
  uploads.delete(id);
  say(`${upload.name} could not be sent.`, "error");
  lastDockKey = null;
  draw();
}

/** A photo at the size an agent is sent, as JPEG — or null where this browser cannot decode it. */
async function shrinkPhoto(file) {
  const bitmap = await createImageBitmap(file);
  const scale = Math.min(1, IMAGE_EDGE / Math.max(bitmap.width, bitmap.height));
  const canvas = document.createElement("canvas");
  canvas.width = Math.round(bitmap.width * scale);
  canvas.height = Math.round(bitmap.height * scale);
  canvas.getContext("2d").drawImage(bitmap, 0, 0, canvas.width, canvas.height);
  bitmap.close?.();
  const blob = await new Promise((resolve) => canvas.toBlob(resolve, "image/jpeg", 0.88));
  return blob ? new Uint8Array(await blob.arrayBuffer()) : null;
}

/** The computer's answer to an upload: its marker into the box it was sent from, or why not. */
function attached(m, message) {
  const upload = uploads.get(message.id);
  clearTimeout(upload?.timer);
  uploads.delete(message.id);
  lastDockKey = null;
  if (message.error) { say(message.error, "error"); draw(); return; }
  if (message.notice) say(message.notice);
  const marker = message.marker ?? "";
  const into = (text) => (text.trim() ? `${text.trimEnd()} ${marker} ` : `${marker} `);
  if (isShowingTile(m, message.tileId)) {
    draft = into(draft);
    draw();
    mirror.soon();
  } else {
    // Left the tile while it went: the marker waits in that tile's own draft on this phone.
    const key = `${m.id}/${message.tileId}`;
    const kept = drafts.get(key) ?? { text: "", adopted: null };
    drafts.set(key, { ...kept, text: into(kept.text) });
    say(`${upload?.name ?? "The file"} is attached to the draft in that tile.`);
  }
}

function pressKey(tile, key) {
  navigator.vibrate?.(8);
  return ask({ type: "key", tileId: tile.tileId, key });
}

function startRecording(tile) {
  navigator.vibrate?.(20);
  // The button is updated in place, never rebuilt: a finger still holds it, and a new element would lose
  // the pointer capture, so letting go elsewhere would leave the microphone open.
  dictation.begin(tile.tileId, { toDraft: true }).finally(() => markMic(dictation.recording));
}

function markMic(recording) {
  const mic = dock.querySelector(".mic");
  if (!mic) return;
  mic.classList.toggle("recording", recording);
  mic.setAttribute("aria-label", recording ? "Stop recording" : "Talk — hold, or tap to start and tap to stop");
}

function stopRecording(cancel) {
  navigator.vibrate?.(cancel ? [10, 40, 10] : 12);
  dictation.finish(cancel);
  markMic(false);
}

/** What the History sheet lists: the user's own messages in this conversation, newest first.
 *  Nothing typed on the phone is kept by the phone: a terminal's box is where a password answers sudo. */
function historyFor(tile) {
  const own = (tile.chat?.items ?? []).filter((i) => i.role === "user" && i.text?.trim()).map((i) => i.text).reverse();
  return [...new Set(own)].slice(0, 40);
}

function historySheet(tile) {
  const el = sheet(
    h("p.sheet-title", "Previous messages"),
    h("div.history", historyFor(tile).map((text) => h("button.history-item", {
      onclick: () => { el.remove(); setDraft(text); },
    }, text))));
}

function moreSheet(tile) {
  const composer = tile.composer;
  const el = sheet(
    h("p.sheet-title", tile.name),
    tile.actions.map((a) => h("button", {
      disabled: !a.enabled,
      onclick: async () => { el.remove(); if (await ask({ type: "action", tileId: tile.tileId, id: a.id })) say(`${a.label}: started.`); },
    }, a.label)),
    tile.newLabel ? h("button", { onclick: () => { el.remove(); startOver(tile); } }, tile.newLabel) : null,
    tile.listsConversations
      ? h("button", { onclick: () => { el.remove(); conversationsSheet(tile); } }, "Other conversations…") : null,
    tile.view === "terminal" ? h("button.quiet", {
      onclick: () => { el.remove(); prefs.screenScroll = !prefs.screenScroll; lastTileKey = null; draw(); },
    }, prefs.screenScroll ? "Fit the screen to the width" : "Show the screen at full size") : null,
    composer.keys ? h("button.quiet", {
      onclick: () => { el.remove(); prefs.keysHidden = !prefs.keysHidden; lastDockKey = null; draw(); },
    }, prefs.keysHidden ? "Show the keys" : "Hide the keys") : null);
}

function stepHistory(tile, by) {
  const list = historyFor(tile);
  if (historyAt === -1) historyStash = draft;
  historyAt = Math.max(-1, Math.min(list.length - 1, historyAt + by));
  const at = historyAt;
  setDraft(at === -1 ? historyStash : list[at], { focus: false });
  historyAt = at;
}

/** A model id is the part a person reads by: its provider and its family prefix go. */
function shortValue(picker) {
  const value = picker.valueLabel ?? "—";
  return picker.id === "model" ? value.replace(/^.*\//, "").replace(/^claude-/, "") : value;
}

/** Model, mode and effort, one section each, and Compact under them: what the next message runs as. */
function settingsSheet(tile) {
  const composer = tile.composer;
  const context = tile.status?.contextPercent;
  const el = sheet(
    (composer.pickers ?? []).map((p) => h("button.setting-row", { onclick: () => { el.remove(); pickerSheet(tile, p); } },
      h("span.setting-label", p.label), h("span.setting-now", p.valueLabel ?? "not set"), icon("chevron"))),
    composer.canCompact ? h("button.setting-row", { onclick: () => { el.remove(); compactNow(tile); } },
      h("span.setting-label", "Context"),
      h("span.setting-now", { class: context >= 80 ? "tight" : "" }, context != null ? `${Math.round(context)}% used` : "not known"),
      h("span.setting-act", "Compact")) : null);
}

/** The composer's model, mode or effort: one list to choose from, and a name typed by hand for a model. */
function pickerSheet(tile, picker) {
  const choose = async (choice) => {
    if (choice.warning && !confirm(choice.warning)) return;
    el.remove();
    if (await ask({ type: "pick", tileId: tile.tileId, picker: picker.id, value: choice.id })) say(`${picker.label}: ${choice.label}`);
  };
  const typed = picker.custom ? h("input.q-custom", {
    type: "text", placeholder: `Or type a ${picker.label.toLowerCase()} name`, autocomplete: "off", spellcheck: false,
    enterKeyHint: "done",
    onkeydown: (e) => { if (e.key === "Enter" && e.target.value.trim()) choose({ id: e.target.value.trim(), label: e.target.value.trim() }); },
  }) : null;
  const el = sheet(
    h("p.sheet-title", picker.label),
    typed,
    h("div.history", picker.choices.map((c) => h("button.history-item", {
      class: c.id === picker.value ? "current" : "", onclick: () => choose(c),
    }, c.label))));
}

async function compactNow(tile) {
  if (!confirm("Compact the context? The agent summarises what has been said so far and carries on from the summary. The transcript is not touched.")) return;
  if (await ask({ type: "compact", tileId: tile.tileId })) say("Compacting the context.");
}

async function conversationsSheet(tile) {
  const answer = await ask({ type: "conversations", tileId: tile.tileId });
  if (!answer || state.tileId !== tile.tileId) return;
  const el = sheet(
    h("p.sheet-title", "Conversations in this workspace"),
    h("div.history", answer.conversations.map((c) => h("button.history-item.conversation", {
      class: c.current ? "current" : "", disabled: !!c.reason, title: c.reason ?? "",
      onclick: async () => {
        if (c.current) { el.remove(); return; }
        el.remove();
        if (await ask({ type: "openConversation", tileId: tile.tileId, conversationId: c.id })) say("Opening the conversation.");
      },
    }, h("span.conv-title", c.title), h("span.conv-note", c.reason ?? c.note)))));
}

async function startOver(tile) {
  const what = tile.newLabel;
  const question = tile.kind === "goal" || tile.kind === "spec"
    ? `${what}? The current goal and its transcript are discarded.`
      + (tile.kind === "spec" ? " Every version of the specification and the plan, and the conversation about them, will be deleted." : "")
      + (tile.composer.canInterrupt ? " The run is going, and this stops it." : "")
    : `${what}? This one stays in the list of conversations on the computer.`
      + (tile.composer.canInterrupt ? " The agent is working, and this stops what it is doing." : "");
  if (!confirm(question)) return;
  if (await ask({ type: "new", tileId: tile.tileId })) { draft = ""; adoptedDraft = remoteDraft; say(`${what}: started.`); draw(); }
}

/** Whether sending now may replace what the tile holds, having asked. */
function consentToStartOver(tile) {
  if (!tile.composer.startsOver) return { ok: true, discard: false };
  return { ok: confirm("This starts a new goal and discards the current one. Go on?"), discard: true };
}

async function sendMode(tile, mode) {
  const text = draft.trim();
  if (mode.needsText && !text) { say("Type the goal first.", "error"); return; }
  const { ok, discard } = consentToStartOver(tile);
  if (!ok || state.sending) return;
  mirror.cancel();
  state.sending = true;
  const answer = await ask({ type: "send", tileId: tile.tileId, text, submit: true, mode: mode.id, replaces: mirror.holding(adoptedDraft), discard });
  state.sending = false;
  // A detect keeps the words on the computer until a goal is read out of them, so the next push carries
  // them back: they were sent, and are not a draft to take into the box again.
  if (answer) { draft = ""; adoptedDraft = text || remoteDraft; mirror.forget(); historyAt = -1; scrollToEnd(true); }
  lastDockKey = null;
  draw();
}

function grow(el) {
  el.style.height = "auto";
  el.style.height = `${Math.min(el.scrollHeight, 140)}px`;
}

async function send(tile, submit) {
  const text = draft.trim();
  if (!text || state.sending) return;
  const { ok, discard } = submit ? consentToStartOver(tile) : { ok: true, discard: false };
  if (!ok) return;
  // A mirror still waiting would land after the send and put the sent words back into the computer's box.
  mirror.cancel();
  state.sending = true;
  const answer = await ask({ type: "send", tileId: tile.tileId, text, submit, replaces: mirror.holding(adoptedDraft), discard });
  state.sending = false;
  if (answer) { draft = ""; adoptedDraft = remoteDraft; mirror.forget(); historyAt = -1; scrollToEnd(true); }
  lastDockKey = null;
  draw();
}

// The screen stays on while a tile is open: a phone strapped to a treadmill that locks itself between two
// sentences is a phone that has to be unlocked with a sweaty thumb.
let wakeLock = null;
async function holdScreenOn() {
  const want = state.level === "tile" && !document.hidden;
  if (want && !wakeLock && navigator.wakeLock) {
    try {
      wakeLock = await navigator.wakeLock.request("screen");
      wakeLock.addEventListener("release", () => { wakeLock = null; });
    } catch { /* refused — battery saver, or an older browser */ }
  } else if (!want && wakeLock) {
    wakeLock.release().catch(() => {});
    wakeLock = null;
  }
}
document.addEventListener("visibilitychange", holdScreenOn);

// ── toast ─────────────────────────────────────────────────────────────────────────────────────

let toastTimer = null;
function say(message, tone = "info") {
  toast.textContent = message;
  toast.dataset.tone = tone;
  toast.classList.add("shown");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toast.classList.remove("shown"), tone === "error" ? 6000 : 3500);
}

addEventListener("resize", () => {
  if (state.level === "layout") draw();
  if (state.level === "tile") fitScreen(view.querySelector(".screen"));
});

// The cards' "3m" moves with the clock, not only with pushes.
setInterval(() => { if (state.level === "layout" && !document.hidden) draw(); }, 30_000);

// The keyboard. Not every browser honours `interactive-widget=resizes-content` — some lay the keyboard
// over the page and shrink only the visual viewport, which left the dock under it. So the page is held
// to the visual viewport itself: as tall as what is visible, and moved down by however far the browser
// scrolled the layout to keep the caret in view.
const vv = window.visualViewport;
if (vv) {
  const fit = () => {
    const atEnd = state.level === "tile" && isAtEnd();
    root.style.height = `${vv.height}px`;
    root.style.transform = vv.offsetTop > 0 ? `translateY(${vv.offsetTop}px)` : "";
    if (atEnd) scrollToEnd(true);
  };
  vv.addEventListener("resize", fit);
  vv.addEventListener("scroll", fit);
  fit();
}

// A pairing link opened in the page already running — pasted into its address bar — changes only the
// fragment, which reloads nothing.
addEventListener("hashchange", () => {
  const invitation = takeInvitationFromUrl();
  if (invitation) pairWith(invitation);
});

history.replaceState({ level: "computers" }, "");
start();
