# Phone terminal — the plan

**Status:** plan only, nothing built. The findings it rests on were read out of the code on 2026-09-01
(mTiles at 0.4.7, `Terminal.Avalonia` 0.3.0 sources) — file references are to those trees.

The goal: after scanning the QR code, the hosted page can **show and drive the terminal of the active
tile** — a shell tile or an agent tile — while the user is away from the machine. Watch the agent's
output, type into it, scroll it. Dictation stays what it is; this is the second thing the phone page
does.

## What is already there

The bridge was built so that a second feature is routing, not plumbing:

- **Transport** — `PhoneBridgeServer` (Kestrel, HTTPS-only, `/` serves the embedded `Assets/phone.html`,
  `/p/{token}` pairs, `/ws` WebSocket). Control messages are JSON text frames; binary frames from the
  client are audio and are only accepted from the connection that owns the stream
  (`PhoneBridgeServer.cs` → `HandleControlAsync`). Strict CSP pins `connect-src` to this very host.
- **Routing to the live tile** — `IPhoneSink` → `PhoneBridgeManager.AddressedTile()` → the active
  `LeafTileNodeViewModel`; the control itself is already reachable as `TerminalTileViewModel.CachedControl`
  (`TerminalTileViewModel.cs:337`, refused when the shell has exited). `NotifyActiveTileChanged` already
  republishes state when the user switches tiles — the stream follows the same trigger.
- **Output** — `TerminalControl.RawOutputReceived` hands over every chunk of child output as the raw VT
  bytes, *before* the emulator parses them (`TerminalControl.Session.cs:86`, raised at
  `TerminalControl.Session.cs:657`, on the UI thread, memory valid only for the call).
- **Input** — `TerminalControl.SendText(string)` writes UTF-8 verbatim to the PTY; VT sequences in the
  string pass through.
- **State, if a snapshot is ever needed** — the emulator (`Terminal.Emulation.Terminal`) exposes
  `GetRow(absolute)`, `TotalRows`, `ScrollbackCount`, `CursorRow`/`CursorColumn`, `Version` and an
  `Updated` event; `TerminalModes` carries DECCKM, mouse tracking/encoding, bracketed paste, win32-input-mode.
- **Page** — one document, strict CSP (`script-src 'unsafe-inline' blob:`), so a JS library can be
  embedded *inline*; nothing may be fetched. `PhoneKeys` today is Enter/Up/Down only.

## The rendering decision: raw stream + xterm.js, not cell snapshots

Two shapes were weighed.

**Chosen — stream `RawOutputReceived` to the phone, render with xterm.js embedded in the page.** The
phone decodes exactly the wire the desktop emulator decodes, so everything a TUI can switch — DECCKM
cursor keys, mouse reporting, bracketed paste, alternate screen — is encoded correctly *by the same
library that encodes it on a desktop client*, and mTiles never has to restate any of it. The DECCKM
problem `PhoneKeys` documents ("what Up means depends on modes the control owns and does not expose")
dissolves: xterm.js tracks the same modes from the same bytes.

**Rejected — serialize cells to the page and render them there.** No library to embed, but every key,
every mouse event and every mode change would have to be encoded by hand against `TerminalModes`, which
is exactly the table `Services/Agents/` exists to avoid duplicating. Worth revisiting only if the
xterm.js payload (~250 KB inline) proves too heavy for the page.

**Rejected — a shadow emulator in mTiles** (a second `Terminal.Emulation.Terminal` fed from
`RawOutputReceived`): no Terminal.Avalonia change needed, but double parsing, double scrollback memory,
and a second copy of the cursor and modes that can only drift.

## Stage 1 — Terminal.Avalonia: expose a snapshot

The one change the library needs. `_terminal` (the emulator) stays private; the host gets a snapshot
type instead of a live object:

- `Terminal.Emulation.TerminalDump` — a pure serializer over the emulator's public surface: walk
  `TotalRows`/`GetRow`, emit a synthesized VT byte stream (SGR runs from `TerminalColor`/`CellFlags`,
  hard line breaks at non-`Wrapped` boundaries), plus cursor position and cols/rows. Pure, so it is a
  table test with no Avalonia in sight.
- `TerminalControl.CaptureScreen()` (or an equivalent accessor) returning the dump. Feeding it to a
  fresh xterm.js reproduces screen + scrollback + cursor; raw chunks streamed afterwards then apply as
  relative updates against a screen that matches cell for cell.

Known imperfection, accepted for v1: if the dump's wrap boundaries ever disagree with xterm's own
reflow, *history* can drift cosmetically — live output stays correct, and a **resync** (re-dump) is the
recovery. A "Resync" control goes on the page from the start.

Input gets its own door: a `SendInput(string)` next to `SendText`, documented as *the keyboard route* —
same transport, opposite contract. `SendText`'s warning ("text of unknown origin must not go through
here"; a raw `0x03` is a `CTRL_C_EVENT` for every process on the pseudoconsole) stays intact for host
scripts, and phone keystrokes — where a deliberate Ctrl+C is the *point* — go through the method whose
contract says they may.

## Stage 2 — wire protocol

Client → server (text frames, JSON, same envelope as today):

| type | meaning |
|---|---|
| `term-sub` | start streaming the addressed tile's terminal (answer: `term-meta`, then a `term-dump`, then binary) |
| `term-unsub` | stop |
| `input` | xterm `onData` — the encoded keystroke as a string |
| `input-bin` | xterm `onBinary` (mouse reports) as base64 |

Server → client: `term-meta` (`cols`, `rows`, `title`), `term-dump` (the dump, base64 or as text —
dump once, deliver whole), and **binary frames** carrying raw output chunks. `Connection.Post` gains a
binary overload; client → server binary stays audio-only, so the two directions never collide and the
audio path is untouched.

Refusals keep the existing house style: `termError` as its own type — never `error`, which the page
treats as the answer to its own dictation attempt and unwinds its microphone state on.

## Stage 3 — backpressure (the only genuinely hard part)

`Connection.Post` chains continuations without bound. A build's output is megabytes per second; a phone
on LTE is not. Unchanged, the send chain grows without limit for exactly as long as the network is slow
enough to make the feature worth having.

- The `RawOutputReceived` handler on the UI thread does one thing: **copy** the bytes (the memory is
  only valid for the call) into a per-subscription buffer. No send from the UI thread.
- A pump on a thread-pool thread sends with a **coalescing window** (~16–32 ms) and a **cap**
  (~256 KB): chunks accumulate while a send is in flight, but whole chunks are dropped from the *front*
  once the cap is passed — never truncated mid-chunk, since each chunk is an indivisible run of wire
  bytes. A terminal stream tolerates dropped history; it does not tolerate torn bytes.
- Disposal of the subscription must be idempotent and race-free against the pump (tile closed, tile
  switched, connection gone) — the same shape `OutputActivityLight` already solves for a lighter
  subscription to the same event; it is the model to copy.

## Stage 4 — mTiles: subscription plumbing

- A new tile capability in the existing idiom — announce by interface, not by type test:
  `ITerminalStreamTile` (what the phone may stream), implemented by `TerminalTileViewModel`, inherited
  by `AgentTileViewModel`. A tile that does not implement it gets a `term-meta` carrying `unavailable`
  and the reason — the Goal tile with the strip running stays on the page as a caption, exactly like
  `ITextInputTile`'s refusals name their cause.
- Subscribing resolves the **addressed tile on the UI thread** (`AddressedTile()`, the same rule the
  keys and the actions list already follow) and attaches there. `NotifyActiveTileChanged` re-resolves:
  tile switched → unsubscribe, subscribe, re-dump. A phone is always looking at what the computer is
  showing.
- Revocation (`Disconnect`) and connection teardown unsubscribe with everything else the connection
  owned.

## Stage 5 — the grant and the settings

Full keyboard input from a paired device is a far larger grant than the three keys — the phone becomes
a keyboard attached to a shell. The boundary is pairing, as it always was, but the user must be able to
decide:

- `Phone.AllowTerminalView` and `Phone.AllowTerminalInput`, **both off by default**; input refused with
  its own sentence when the view is on but the input is not. The keys today are gated on nothing —
  this feature deliberately is, because the page's copy has to change when it arrives: the panel
  promises dictation; it will also have to say what the terminal view lets a paired device do.
- `docs/DICTATION.md` → *Dictating from a phone* gains the section, per the convention that every
  widening of what pairing means is written down where the reader of `Services/Phone/` will trip over it.

## Stage 6 — the page

- xterm.js vendored into `Assets/` and inlined into the served document (CSP allows `script-src
  'unsafe-inline'`; nothing external is fetched). Pin the version; verify synchronized output (?2026)
  support in the pinned release — the emulator tracks `Modes.SynchronizedOutput` and xterm.js that
  ignores it will flicker on agent output that batches.
- Terminal view as a **second tab on the same page** ("Dictate / Terminal") — the sofa use is one breath:
  dictate a line, then watch what the agent did with it. The dictation UI does not move.
- Keyboard: xterm's own hidden textarea, `term.focus()` on tap (autocapitalize/autocomplete off). The
  phone's native keyboard is the keyboard; no on-screen keys beyond what dictation already has.
- Scrolling: client-side in the normal buffer, zero server work — this is the whole answer to
  "scrollować". In the alternate buffer, xterm turns the wheel into mouse reports per the app's own
  mode; they travel as `input-bin`.
- Zoom: pinch is disabled page-wide (`user-scalable=no`) for the dictation gestures. Zoom **buttons**
  (+ / − / fit) over CSS transform, because pinch handling on iOS inside a fixed viewport is unreliable
  and the terminal must not scroll the page.
- `user-scalable=no` stays; the buttons are the deliberate affordance.

The layout, states and component treatment of this view are specified in detail in the appendix at the
end of this document.

## Stage 7 — input

- `input`/`input-bin` → dispatcher → addressed tile's `SendInput`. Refused with a sentence when the
  shell is not running (the same voice as `PressKeyAsync`'s refusals).
- Rate-limited per connection (generous — tens per second — enough that holding an arrow key works,
  tight enough that a flood is a bug in somebody's client). Input is the one client message that can
  arrive at wire rate with no audio-sized framing to justify it.

## Stage 8 — tests

- `TerminalDump` — table test in Terminal.Avalonia's own suite: colors, wide/combining cells
  (`Cell.Extended`), wrap boundaries, hyperlink cells (dump drops the link id — out of scope, recorded),
  cursor, alt screen (dump the visible screen only; alt buffer has no scrollback by definition).
- `PhoneBridgeServerTests` (loopback, fake sockets — the existing pattern): `term-sub` → dump then
  binary; audio still refused from a non-owner; input refused when the grant is off; revoked connection
  receives nothing further; binary client→server from a non-stream-owner is ignored as today.
- Coalescing: flood N chunks faster than the pump, assert bounded memory, monotonic whole chunks, and
  that the *last* chunk survives.
- `PhoneBridgeManagerTests`: subscribe targets the addressed tile; switching the active tile resyncs;
  a non-terminal active tile answers `unavailable` and dictation keeps working.

## Known gaps, recorded rather than hidden

- **Geometry.** The PTY's cols/rows follow the desktop tile's pixel size and stay its business. The
  phone renders the desktop's geometry (possibly 180 columns on a 400 px screen — hence the zoom
  buttons) and never asks for a resize. A phone-driven resize protocol would fight the desktop control
  over `RedrawShellOnResize` and is deliberately out of scope.
- **win32-input-mode.** PSReadLine enables `?9001`; xterm.js does not speak it and answers in classic
  encoding, which PSReadLine also accepts. Recorded so the day line editing looks different on the
  phone has a name to check first.
- **History before connect.** The dump is the only pre-connect history and it is color-accurate but
  reflow-fragile (see Stage 1). Live output is byte-exact from the first streamed chunk.
- **Two phones.** Raw chunks go to every subscribed connection (the `Post` chain already serializes
  per connection). No coordination, no exclusivity — the screen is not a resource anyone can take away
  from anyone.

## Appendix — the page's design (Stage 6 in detail)

Designed against the existing `Assets/phone.html` (its palette, its `#keys` recipe, its 10px radius and
hairline borders) so the terminal view reads as the same page, not a bolted-on dashboard. Design intent
in one line: **the phone is a small window laid on the desktop machine's monitor** — the one aesthetic
risk taken is presenting the terminal as *a screen in the dark*, a ground deeper than the page itself
(`--term-bg: #10101d`), which dims when the connection drops. Every other choice stays inside the
existing vocabulary.

### Navigation

**Top segmented tabs, directly under the shared header** — two equal-width buttons, `Dictate |
Terminal`, in a 1px-bordered 10px-radius container. A bottom switcher was rejected: the bottom is
contended three ways (dictation's footer, the terminal control bar, the native keyboard when open),
while the top is free — and the sofa gesture, dictate a line then switch and watch, is a small vertical
thumb move, not a reach.

- The **header stays shared and unchanged**: dot (`#dot`), status text, tile name (`#target`). One
  socket, one dot; two status rows would be two places to disagree. The tile name answers "which tile
  is shown" for both tabs and is not repeated inside the terminal view.
- The **footer (latch checkbox) belongs to dictation** and hides while the Terminal tab is active
  (`body.term footer { display: none }`). Nothing in the dictation DOM moves.
- **Keyboard open** toggles `body.kb` (focus/blur on xterm's hidden textarea; on iOS also size the
  terminal section from `visualViewport.height`, because the layout viewport does not shrink there):
  control bar and status line stand down, header and tabs stay — the tabs are the way out. The
  terminal card takes everything else. Landscape-with-keyboard leaves a short strip of terminal; that
  is the accepted shape, not a problem to solve with a second layout.

### Layout

Portrait at rest:

```
┌───────────────────────────────────┐
│ ● Connected           agent-tile  │  header — unchanged, shared
│ ┌───────────────┬───────────────┐ │
│ │    Dictate    │   Terminal    │ │  tabs — segmented, 44px
│ └───────────────┴───────────────┘ │
│ Read-only                         │  status line — 13px muted; danger text when offline
│ ┌───────────────────────────────┐ │
│ │                               │ │
│ │  $ npm run build              │ │  terminal card — flex:1
│ │  vite v5.1.4  building…       │ │  (xterm; vertical scroll its own,
│ │  …                            │ │   horizontal pan when the grid is
│ │                               │ │   wider than the card)
│ └───────────────────────────────┘ │
│ ┌────┬──────┬────┬────────┬─────┐ │
│ │ −  │ fit  │ +  │ Resync │ ⌨   │ │  control bar — 44px, five equal cells
│ └────┴──────┬────┴────────┴─────┘ │
└───────────────────────────────────┘
```

Docked: header, tabs, status line, control bar. Scrollable: **nothing outside the card** — xterm owns
vertical scroll of the buffer, the card owns horizontal pan (`overflow-x: auto`) for a 180-column grid
at legible zoom. The two axes never fight: horizontal drag pans the card, vertical drag reaches xterm's
viewport. Safe area is already covered — every docked element is a body child of the page's existing
`env(safe-area-inset-*)` padding. When the stream is unavailable, the card's interior shows the message
and the control bar renders **disabled, not hidden** — visible but inert, so the page does not reflow
when a Goal tile hands back to a terminal tile.

### Typography and sizing

- xterm font stack (no fetched fonts): `ui-monospace, "SF Mono", Menlo, Consolas, "Cascadia Mono", monospace`.
- **Default 12px; zoom is discrete font-size steps, not a transform** — `[6, 8, 10, 12, 14, 16, 18, 22]`,
  one step per tap, persisted in `localStorage` (wrapped in try/catch — the convenience must not break
  the page where storage is refused). Changing `fontSize` re-renders crisply at every DPR and, because
  cols are the desktop's and never change, zooming out reveals *more columns* — exactly what 180
  columns on a 400px screen needs. Transforms are never used: scaled text blurs.
- **Minimum legible 6px** (≈111 columns on 400px at DPR 3 — an overview, not a reading size). **Fit**
  means fit-width clamped to `[6, 12]`: `fit = clamp(cardWidth / cols / 0.6, 6, 12)` — the one-tap
  recovery from "I zoomed in to read and got lost", not a promise that everything fits.
- **Line-height 1.2** (xterm option) — airier than xterm's 1.0 for finger-sized rows, tighter than the
  page's 1.45 body so the terminal still reads as a terminal. Chrome sizes stay in the page's existing
  scale: status line 13px muted, tabs 15px/500, fallback 15px, glyphs 20px (the existing `.glyph`).

### States

| State | What the page shows |
|---|---|
| **Connected, streaming** | Header dot green (existing). Status line: mode word. Terminal live. |
| **Offline / reconnecting** | Header dot red (existing, shared). Status line: `Offline — showing the last screen.` in `--danger` — danger red is for offline states, and this is its one use in the view. The card dims to `opacity: .6`. The last screen is never blanked; it is marked stale. Auto-reconnect exists; no button. |
| **Read-only** | Status line: `Read-only`. No keyboard button rendered. Tapping the terminal does not focus it, so no keyboard is ever summoned — read-only is enforced by not opening the door, not by ignoring keystrokes. |
| **Input allowed** | Status line: `Input on`; `⌨` button present. Tapping the terminal focuses it (native keyboard opens); `⌨` toggles it off and wears the latched look (`#1d2a3a` ground, accent border — the same vocabulary as `#talk.busy`). |
| **No terminal / grant off** | Card interior shows the server's own reason from `term-meta`/`termError`, centered, in the `#transcript` register (15px, muted, on the dark card). Defaults: `No terminal on the active tile.` and `Terminal view is switched off in mTiles Settings.` — the second names the fix, per *say what it is, or offer to fix it*. The tab is never hidden: a tab the user cannot find is worse than a notice. Control bar disabled. |
| **Keyboard open** | `body.kb` hides control bar and status line; terminal takes the remaining height. The header dot still reports offline while typing. |
| **Heavy output** | No cue. The stream updating *is* the cue; a spinner over a spinner-shaped activity is noise. The one moment needing anything is the first dump: `Loading screen…` in the card interior until `term-dump` paints. Resync confirms itself the way every control here does — the 120ms `.pressed` flash — and the repaint is the rest of the feedback. |

Subscribing follows the tab, not the page: entering Terminal sends `term-sub`; leaving the tab — or the
socket reconnecting after `document.hidden`, which closes the socket today — re-subscribes so the fresh
dump is fetched. The pump must not feed a screen nobody is watching.

### Colour and components

- One new token, everything else reused: `--term-bg: #10101d` (a deepening of the page's own ground,
  not a new hue). xterm theme: `background: var(--term-bg)`, `foreground: var(--text)`,
  `cursor: var(--text)`, `selectionBackground: var(--accent)`. ANSI 0–15 stay xterm's defaults —
  recorded, not tuned: hand-tuning sixteen colours against a dump nobody has seen yet is a guess
  wearing a spec.
- **The terminal is full-bleed inside its card, not inset.** One `#termcard`: `--term-bg`, 1px
  `--border`, 10px radius, `overflow: hidden`; xterm fills it with 6px/8px padding so no glyph touches
  the hairline or the corner radius. Insetting a screen inside a card would spend scarce width on a
  margin and put a square rectangle inside a rounded one — the same mistake the desktop app's tile
  rules document.
- **The control bar reads as the key row's sibling**: identical recipe to `#keys button` (1px
  `--border`, `--surface`, 10px radius, `.pressed` flash) at **44px min-height instead of 58** — five
  controls share the row, and Resync is a once-in-a-while recovery rather than a rhythm key. Zoom −/+
  are glyphs (pressed a hundred times, recognized not read); `fit`, `Resync`, `⌨` are words (read
  before pressed, like the actions row). Disabled is the existing `opacity: .45`.
- **The one accent is spent once** — the latched keyboard button. Nothing else in this view
  out-shouts the talk button going red.

### Structure

Appended to the existing document; existing ids untouched.

```html
<nav id="tabs" role="tablist">
  <button id="tab-dictate" class="on" aria-selected="true">Dictate</button>
  <button id="tab-terminal" aria-selected="false">Terminal</button>
</nav>

<section id="view-terminal" hidden>
  <div id="termstatus"></div>
  <div id="termcard">
    <div id="term"></div>              <!-- xterm mounts here -->
    <div id="termnote" hidden></div>   <!-- "Loading screen…" / fallback reason -->
  </div>
  <div id="termbar">
    <button id="zoomout" aria-label="Zoom out"><span class="glyph">−</span></button>
    <button id="zoomfit" aria-label="Fit width">fit</button>
    <button id="zoomin" aria-label="Zoom in"><span class="glyph">+</span></button>
    <button id="resync">Resync</button>
    <button id="kbd" hidden aria-label="Keyboard"><span class="glyph">⌨</span></button>
  </div>
</section>
```

```css
:root { --term-bg: #10101d; }

#tabs { display: flex; border: 1px solid var(--border); border-radius: 10px; overflow: hidden; }
#tabs button { flex: 1; min-height: 44px; border: 0; background: transparent; color: var(--muted);
  font: inherit; font-size: 15px; font-weight: 500; touch-action: manipulation; }
#tabs button + button { border-left: 1px solid var(--border); }
#tabs button.on { background: var(--surface); color: var(--text); }

#view-terminal { flex: 1; display: flex; flex-direction: column; gap: 10px; min-height: 0; }
#termstatus { font-size: 13px; color: var(--muted); min-height: 1.4em; }
#termstatus.bad { color: var(--danger); }
#termcard { flex: 1; min-height: 0; position: relative; overflow-x: auto;
  background: var(--term-bg); border: 1px solid var(--border); border-radius: 10px; }
#termcard.stale { opacity: .6; }
#term { height: 100%; padding: 6px 8px; }
#termnote { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center;
  padding: 24px; text-align: center; color: var(--muted); font-size: 15px; line-height: 1.5; }
#termbar { display: flex; gap: 8px; }
#termbar button { touch-action: manipulation; user-select: none; -webkit-user-select: none;
  appearance: none; border: 1px solid var(--border); background: var(--surface); color: var(--text);
  border-radius: 10px; min-height: 44px; font-size: 14px; font-weight: 500; flex: 1;
  display: flex; align-items: center; justify-content: center; gap: 6px;
  transition: background .1s, border-color .1s, transform .1s; }
#termbar button:disabled { opacity: .45; }
#termbar button.pressed { background: #1d2a3a; border-color: var(--accent); transform: scale(.96); }
#termbar button.latched { background: #1d2a3a; border-color: var(--accent); }
#termbar .glyph { font-size: 20px; line-height: 1; }

body.term footer { display: none; }          /* the latch belongs to dictation */
body.kb #termbar, body.kb #termstatus { display: none; }
```

Tab switching toggles `hidden` on the dictate `main` vs `#view-terminal`, the `term` class on body,
`aria-selected`, and the subscription. The dictate view's markup, handlers and layout are otherwise
untouched.

### What NOT to build

- **No mouse-mode indicator** — the user cannot act on DECCKM/mouse bits; xterm already encodes from
  the same bytes.
- **No cols×rows or terminal-title readout** — the geometry is the desktop's business and the screen
  already shows the prompt; a second chrome line stating what the screen states is spent height.
- **No on-screen key rows beyond the existing six** — the native keyboard is the keyboard (Stage 6);
  duplicating it in HTML is a worse keyboard.
- **No pinch zoom** — `user-scalable=no` stays; dictation gestures own it. The buttons are the
  deliberate affordance.
- **No settings, theme toggle or grant controls on the page** — grants live in mTiles Settings where
  the confirmation dialog is; the page names where to fix a refusal, it does not offer to.
- **No second connection dot or per-tab status** — one socket, one truth in the header.
- **No scroll-to-bottom floating button** — Resync returns to the live edge; a fourth scrolling
  affordance is one more thing to miscast.
- **No resize requests to the PTY** — out of scope by Stage 1 (it fights `RedrawShellOnResize`); the
  phone is a window, not a second geometry.
- **No copy/share buttons in v1** — xterm's native selection exists; a share sheet is a second feature
  wearing this one's tab.