# Roadmap

Things worth doing that nothing is currently blocked on. Each entry says what is wrong now, what the
stopgap is, and what would actually settle it — so that picking one up does not start with rediscovering
why it is here.

---

## Session persistence, measured against Herdr

**Where this comes from.** A 2026-09-06 comparison against [Herdr](https://github.com/herdrdev/herdr), a
terminal-first agent runtime with a server/client split. Herdr's server keeps every pane's process alive
independently of any attached client, so detach/reattach loses nothing; mTiles is one process, so closing
the window ends every tile's shell. The comparison is not a case for adopting that architecture — a
server/client split would change what mTiles *is* — but five gaps in it are worth closing without one.

**Read the corrections before picking one up.** The first draft of this section was written from Herdr's
feature list rather than from this code, and four of the five entries were wrong about mTiles in ways
that would have cost something: one named a mechanism that executes the user's scrollback as shell
commands, one asked for a guard that already exists in a stronger form, one would have left every Claude
Code tile on a bare shell, and one called an architectural change cheap. Each is recorded below under
**What was wrong here**, because the alternative is discovering it a second time.

The order is the order worth doing them in, cheapest and safest first.

### 1. A visible sign that a resume actually happened

**Where it hurts.** `IDescribedTile.HeaderNote` shows the instance and model, never whether the session
underneath is the one from before or a fresh one started because the old id was not found. A user has no
way to tell "my agent remembers everything" from "my agent just started over" without reading the
transcript.

**What would settle it.** A short state on `HeaderNote` (or a small badge beside it) — *resumed* vs. *new
session (previous conversation not found)*.

**Where the fact actually is, and it is not free.** The resume command is chosen *unconditionally*:
`ClaudeAgent.Resume` (`ClaudeAgent.cs:486`) always emits `claude --resume <id>` with
`claude --session-id <id>` behind it, and which of the two the tile ends up running is settled by the
chain, from an exit code, seconds later. `DirectLaunchSession.RunAsync` knows it — it holds the `index`
it settled on — and tells nobody: there is no callback from the chain back to the tile. So this needs one
new signal out of `DirectLaunchSession`, not a binding onto something already published.

**And the index alone does not answer it.** For `SessionStrategy.CapturedAfterStart` (codex, agy) an
empty stored id makes command 0 a *new* conversation by design, so "the startup command stuck" means
resumed for claude, pi and opencode and means the opposite for the other two. What the badge reads is
therefore the pair: which command settled, and whether an id was passed to it at all.

> **What was wrong here.** The first draft called this "cheap: the fact already exists at the point the
> resume command is chosen, it is only not surfaced." It does not exist at that point, and nothing
> surfaces it afterwards.

### 2. A setting to skip agent resume on launch

**Where it hurts.** Resume is unconditional today. There is no way to say "start every agent fresh this
time" short of "New session" per tile. Herdr has `[session] resume_agents_on_restore = false` for exactly
this.

**What would settle it.** A Settings → AI toggle, **Resume agent sessions on startup**, default on; off
sends every terminal agent tile through a non-resuming launch for that run.

**Three of the five agents can do that for free, and two cannot.** `CapturedAfterStart` (codex, agy)
already treats an empty session id as "start a plain session", and opencode's non-resuming command is a
bare `opencode` — for those three, skipping the resume is passing no id and nothing is written down.
`SessionStrategy.Fixed` is the problem:

- **Claude Code** — the non-resuming command *is* `claude --session-id <tileId>`, and measured against
  2.1.251 that flag refuses an id already in use ("Session ID … is already in use", exit 1). Sent through
  it with the tile's own id, both commands in the chain fail fast and the tile lands on a bare
  interactive shell. Implemented literally, this toggle breaks every Claude Code tile that has ever run.
- **pi** — `--session-id` creates *and* resumes, so the same id simply resumes the conversation the
  toggle promised to skip. It fails silently rather than loudly, which is worse.

**So the honest scope is one of two, and it has to be chosen deliberately.** Either the toggle covers the
three agents that can start fresh without an identity change and says so on the row, or a fresh run on
claude/pi needs a throwaway session id — and then the conversation it starts is unreachable at the next
launch unless the id is stored, which is what "New session" already does by replacing the leaf's
`TileId`.

> **What was wrong here.** The first draft promised "off sends every terminal agent tile through its non-resuming
> launch path for that run only (not a change to any tile's stored session id)". For claude and pi there
> is no such path: not changing the stored id is exactly what makes the fresh session impossible or
> unreachable.

### 3. Two tiles restored holding the same captured session id

**Where it hurts.** A copied or hand-edited layout file can carry the same codex session id on two leaves.
`TerminalAgentTileViewModel`'s constructor claims a stored id (`TerminalAgentTileViewModel.cs:107`) with
`CapturedSessions.Claim`, which is `Held[sessionId] = holder` — last writer wins. Both tiles then resume
the same conversation, both write it back on the next save, and nothing anywhere says so.

**What would settle it.** `Claim` answering whether it displaced another live holder, and the second tile
treating a displaced claim the way `AgentSubstitution` is treated: keep running, keep the requested id
out of `Save`, and say once in `LaunchNotice` that another tile is showing this conversation. Not a
refusal — two views of one rollout is a mess, not a hazard, and a tile that silently starts a different
conversation than its layout asked for is the worse of the two.

> **What was wrong here.** The first draft asked for "one `HashSet` of claimed session refs, held for the
> duration of a workspace restore and threaded through every tile's `Create`", on the premise that
> `NewestSessionId`'s `tryTake` is "per capture, not per restore pass" and that two tiles could race for
> one rollout file. Both halves are false, and the fix would have been a regression:
> `CapturedSessions.TryClaim` is a single `GetOrAdd` (`CapturedSessions.cs:52`), so two captures on the
> thread pool cannot both come away with one id; the register is **process-wide** and outlives any one
> restore pass, so it also covers a second workspace opened later, which a per-pass `HashSet` would
> not; and a restored tile claims its stored id in its constructor, before any capture runs. What is
> left is the duplicate-*stored*-id case above, which the `HashSet` would have detected and the current
> register does not.

### 4. Opt-in scrollback capture for plain terminal tiles

**Where it hurts.** `SessionStrategy` covers agent tiles of both kinds. A plain terminal tile has no equivalent at all:
after a restart it is an empty shell in the saved cwd, with no trace of what was on screen — unlike
Herdr's opt-in `pane_history`, which replays the ANSI scrollback into the new shell (presentation only,
not a live process).

**What would settle it.** A per-install setting (default **off**, for the same reason Herdr's is off —
terminal output can hold secrets, tokens, prompts), snapshotting the last N lines of the buffer into
`TerminalTileKind.Save` and painting them back on the next launch. Presentation-only: it must not be read
as "the shell is back", only as "here is what it last showed".

**`RestartAsync`'s `startupInput` is not the route, and using it would be destructive.** That parameter
is a *keyboard*, not a screen: `ShellStarter.SplitIntoLines` (`ShellStarter.cs:62`) appends a `\r` to
every line and the control types them into a live prompt. Handed a scrollback, it would run the last N
lines of the user's own terminal output as commands, in their working directory, at every launch — with
whatever a build log, a `--help` or a pasted snippet happens to start a line with.

**The route it does need does not exist yet, and it is in the library.** `TerminalControl` keeps its
emulator private, exposes no reader for the scrollback's text, and offers nothing that writes into the
buffer without going to the PTY (`Terminal.Emulation.Terminal.Write` is not reachable from the host). So
this is two additions to Terminal.Avalonia first — read the buffer out, paint bytes in without sending
them to the child — and only then a setting here. That is the real cost of this entry.

> **What was wrong here.** The first draft named `RestartAsync(options, startupInput)` as the mechanism,
> and described the whole thing as a snapshot into `TerminalTileKind.Save`. The mechanism executes the
> snapshot, and the library API it assumes is not there.

### 5. Surviving a Velopack update — and why Job Objects are not it

**Where it hurts.** An update today means: close the process, every tile's shell and every agent CLI
inside it dies, relaunch, and whatever `SessionStrategy` can resume, resumes — as a **new** process, not
the one that was running. An agent mid-task loses whatever state lives only in its running process (a
long tool call, an open file handle, a REPL's variables). This is the gap that costs users the most, and
it is also the only one on this list that is not cheap.

**Job Objects do not answer it.** Three measured facts, in the order they defeat the idea:

- A child on Windows **already** survives its parent's exit. `KILL_ON_JOB_CLOSE` is what would kill it;
  `Terminal.Pty` creates no job object at all, so there is nothing to opt out of.
- `ConPtyConnection.Dispose` calls `TerminateProcess` on a child that has not exited, by hand. Whatever
  the OS would have allowed, this code kills them.
- Even with both of those changed, the pseudoconsole belongs to the exiting process: `Dispose` closes it
  ("signals EOF to the child so it can exit cleanly"), which takes the OpenConsole host and both pipes
  with it. A surviving child would be left with no console and no I/O — alive and useless.

**What would actually settle it** is the ConPTY endpoint outliving the GUI: the pseudoconsole handle and
the pipes held by, or handed to, a process the update does not restart, and re-attached afterwards. That
is a long-lived process independent of the GUI — the architectural line the rest of this section says
mTiles is not crossing — so this sits at the bottom of the list rather than the top, and is written down
here only so the Job Object idea is not had a second time.

> **What was wrong here.** The first draft led with this as item 1, "not urgent" but cheap, on the
> reasoning that "Job Objects support `JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK` / omitting
> `KILL_ON_JOB_CLOSE`, which lets a child survive its parent's exit". It is the most expensive item here
> and the mechanism was the wrong one.

### What not to chase

Herdr also has detach/reattach across app exit, live PTY handoff between server instances, several
clients sharing one session, and remote (SSH) access to a session already running elsewhere. All four
need a long-lived process independent of the GUI — the architectural line mTiles is not crossing. Item 5
above turned out to need the same thing, which is why it is last rather than first: update continuity is
the one of these that costs users most today, and there is no cheap substitute for it.

---

## A control that is a combo box and a search box at once

**Where it hurts.** The agent instance form's **Model** and **Fast model** fields (Settings → AI), and
the manual-connection form's database name if it ever grows a list.

**Why neither built-in control is right.** Avalonia 12.1.1 offers two, and each is wrong in the opposite
direction — both measured, not assumed:

- **`AutoCompleteBox`** — what is used today, with `FilterMode="Custom"` over `Views/ModelSearch.cs`:
  every typed word, anywhere in the id, in any order. That is the only thing that makes OpenRouter's
  catalogue usable — it answered with **396 models** when this was written. But the control has **no
  affordance at all** — no arrow, nothing to click — so a field with a perfectly good list behind it
  looks identical to an empty text box, and there is no way to ask "what are my options" without
  guessing a first letter.
- **`ComboBox IsEditable="True"`** — `IsEditable` and `Text` both exist in 12.1.1 (compile-checked), so
  switching is one attribute. It has the arrow, and it lets you type. But its typing is
  `IsTextSearchEnabled` — *jump to the first entry starting with what you typed* — and **not**
  filtering. On 396 models that is a 396-row scroll list with a keyboard shortcut to the letter B. It
  trades the thing that made the field usable for the thing that made it discoverable.

**What has changed since.** The **List** button is gone: the list is fetched when the account changes,
so there is no longer a press that has to visibly do something, and the `IsModelListOpen` stopgap that
opened the drop-down went with it. The reported symptom is therefore gone — but the gap this entry
exists for is not, and is now the whole of it: **the field still shows no sign that a list exists**
until somebody types into it.

**What would settle it.** A control that shows a drop-down affordance, opens the full list on click, and
narrows it as you type. In preference order:

1. **Find one.** Worth checking before writing anything: whether a later Avalonia gives
   `AutoCompleteBox` a toggle button in its template (this may be a theme-level fix — a
   `ControlTemplate` override on the Fluent theme's `AutoCompleteBox`, no new control at all, which
   would be by far the cheapest answer and should be tried first), and what the community control sets
   offer.
2. **Template it.** If the Fluent template can carry a chevron that sets `IsDropDownOpen`, this is a
   dozen lines in `Styles/Controls.axaml` and no new type. **Try this before option 3.**
3. **Write it.** A `TemplatedControl` wrapping `AutoCompleteBox` plus a toggle button, in
   `Views/`, or — if it earns it — in its own package alongside `Terminal.Avalonia`. The behaviour
   wanted is small and well understood: click the chevron → open with the unfiltered list; type →
   filter; Escape → close and keep the text.

**Not urgent.** Both fields are used when an instance is created or edited, which is a handful of times
per installation, and the stopgap covers the case where somebody has asked for the list. It goes on this
list rather than into the next change because a new control is a maintenance commitment, and the third
option is the one most likely to be reached for and the one least worth reaching for first.

---

## A tile header that is an index, a kind and a description

**Where it is now.** The header is a kind glyph, the tile's name, and — since the note landed — what an
agent tile is running (`IDescribedTile.HeaderNote`, e.g. `Claude Code · glm-5.3-flash`). The name is
still doing two jobs: it is both the label the user may type and the generated `Agent#1` that nobody
chose. The end state wanted is three separate things: **an index**, **what kind of tile this is**, and
**a description the user writes**.

### 1. The index, and reading order

Every tile in the open workspace gets a position label, assigned **in reading order** — left to right,
then down — so the labels match how the layout looks rather than the order the tiles were created in.
The alphabet is the keyboard's own: `1 2 3 4 5 6 7 8 9 0`, then `q w e r t y u i o p`, then
`a s d f g …`. Ten tiles is already a busy screen and thirty is the practical ceiling; past that the
label is simply absent, which is honest and costs nothing.

**Reading order is the part with a real decision in it.** The layout is a binary tree of splits, not a
grid, so "left to right, then down" is not something the tree answers directly — it is a rule about
where each leaf's rectangle *ended up*. Two ways to get it:

- **From the tree**, by walking it with the split direction in hand: a horizontal split's children are
  left then right, a vertical split's are top then bottom, and an in-order walk that respects that gives
  reading order for every layout that can actually be built. Pure, testable without a window, and it
  never has to wait for a layout pass.
- **From the arranged bounds**, sorting leaves by `(Top, Left)` with a tolerance for rows. Obviously
  correct for any layout, and obviously worse to own: it needs the visual tree, it needs a pass to have
  happened, and "which rows are the same row" is a tolerance somebody has to pick.

The first is the one to write, and the second is what a test can check it against on a handful of real
layouts.

**Renumbering is the hazard, not the numbering.** Every split, close, drag and workspace switch changes
the answer, so the label under a given tile moves. That is correct — it is a *position*, and a position
that did not follow the tile's position would be a name badly spelled — but it means the shortcut a user
learned this morning points somewhere else this afternoon. Worth knowing before building, not worth
solving with pinning: the alternative, an index that never moves, is a second identity to maintain and a
label that lies about where the tile is.

### 2. The shortcut

`Ctrl`+the label focuses that tile. **The modifier must be configurable**, and that is not a courtesy:
`Ctrl+1`…`Ctrl+9` is tab switching in most terminals and in every browser, and this application's whole
purpose is hosting programs that want their own keys. So it is a setting on the General page with a
capture control — `HotkeyCapture` and `HotkeyGesture` already exist for the dictation shortcut and are
the right pieces — plus an explicit "no shortcut" option, offered out loud the way dictation's is.

The handler belongs at window level beside `TerminalClipboardCoordinator`, which is already the place
that decides whether a keystroke belongs to a tile or to the application, and already knows not to
hijack text-editing controls.

### 3. The description

The name field becomes what it should always have been: **the user's own words, empty by default**, with
a watermark saying it can be filled in. What is lost by emptying it — *which tile is this* — is exactly
what the index and the kind glyph now carry, which is what makes the change possible at all rather than
just a blank where a label used to be.

Consequences to settle before touching it:

- **`ITileKind.NameFor` becomes a fallback, not a default.** It generates `Agent#1` and an
  adjective-and-animal for terminals today. Once the index exists, a generated name is a second answer
  to a question something else now answers better.
- **`IFileContent.RenameFile` follows the name**, so a Note's file is named from it. An empty
  description cannot be an empty filename: the note tile needs its own answer before the name can be
  allowed to be blank.
- **Layouts already on disk have names in them.** A name somebody typed is a description and must
  survive; a generated `Agent#3` is not, and carrying it forward would leave every existing tile with a
  description nobody wrote. Telling them apart means comparing against what `NameFor` would have
  produced — which is doable, and is the sort of migration that wants a golden-file test beside the one
  `TileNode` already has.

**Order to build in:** the index and its reading-order rule first (pure, testable, and useful on its own
— it is what makes the shortcut possible), then the shortcut, then the description. The third is the
only one that touches saved layouts, and it is worth having the first two in use before deciding what
the header should look like without a generated name in it.

---

## Agents that are pointed at a provider by name — done, except agy

**Fixed 2026-08-31.** Kept here because the reasoning is the kind that gets undone by somebody tidying.

An instance of opencode or pi pointed at a configured provider used to run silently on the CLI's own
default account. Both were given `OPENAI_BASE_URL` and `OPENAI_API_KEY` and a bare model id — a shape
borrowed from Claude Code, where `ANTHROPIC_BASE_URL` really does redirect. **These two CLIs are not
built that way**: each keeps a registry of providers, decides which one is in play from *which key
variable is set*, and validates `provider/model` against a catalogue before opening a socket. So
`OPENAI_API_KEY` for an OpenRouter instance authenticated against api.openai.com (`opencode auth list`
reports it as the OpenAI provider) and the model was refused outright with `ProviderModelNotFoundError`.

What the fix is made of, and where each piece lives:

- **`IAiProvider.KeyEnvironmentVariable` and `CatalogueId`** — facts about a *service*: what its key is
  called, and what catalogues call it. On the provider because they are the same everywhere the service
  is read; on the agents they would be one table written out five times.
- **`IAiAgent.QualifiedModel`** — the model spelled the way *this* CLI wants it. Claude Code takes the
  id bare and is aimed by address; opencode and pi get `provider/model`. Asked once and used by both the
  tile and the headless goal run, so one instance cannot be spelled two ways.
- **`IAiAgent.SupportsCustomEndpoint`** — whether the CLI can be aimed at a server its registry has
  never heard of. Distinct from `AiProviderCatalog.IsCompatible`, which only asks whether the wire
  formats meet: opencode and pi both speak `/v1/chat/completions` and only one has anywhere to put an
  address.
- **`OpenCodeProviderConfig`** — the generated file that is opencode's only route to a local server,
  written per instance and rewritten every launch, exactly as `OpenCodeSession`'s import document is.
- **`AgentModelResolver`** refuses pi on a local server by name, where it already refuses a deleted
  provider — because until it refuses, the failure is silent.

**What is left:** agy was not measured, and inherits the permissive default (`SupportsCustomEndpoint`
true, model unprefixed). If somebody points an agy instance at a provider, that pairing is a guess.

---

## pi on a local server: allow it when the extension is there

**Today it is refused** (`AgentModelResolver`, `PiAgent.SupportsCustomEndpoint => false`). That is
deliberate and stays until this is built — see *why refusing* below.

### The route exists

`pi-localllm-provider`, a third-party pi extension (`pi install npm:pi-localllm-provider`). Measured
2026-08-31 by installing it and reading its source:

- It calls `pi.registerProvider("localllm-<slug>", { name, baseUrl, apiKey: apiKey || "no-key",
  api: "openai-completions", models: [...] })`.
- `<slug>` is the server's display name lowercased with non-alphanumerics collapsed to `-`, so a server
  called *LM Studio* becomes `localllm-lm-studio`.
- The model is then `localllm-lm-studio/google/gemma-4-12b`.
- Its configuration is `settings.json` → `localllm.servers[]`, each entry
  `{ id, name, baseUrl (ends /v1), apiKey, apiType, models: [{ id, name, contextWindow, maxTokens,
  reasoning, input }] }`. Writable by hand; the `/localllm` TUI wizard is only one way in.

### The blocker, and it shapes the design

```ts
const SETTINGS_FILE = path.join(os.homedir(), ".pi", "agent", "settings.json");
```

The extension reads a **hardcoded path in the home directory and ignores `PI_CODING_AGENT_DIR`**. So the
`OpenCodeProviderConfig` pattern — a generated file per instance — cannot be reused: pi's local servers
are one global list for the machine. Two pi instances cannot point at two different local servers, and
mTiles must not pretend otherwise.

Confirmed the hard way: a `localllm` block written into a temporary `PI_CODING_AGENT_DIR` was never
seen, and pi answered `Model "localllm-lm-studio/…" not found`.

### What to build

1. **Detect** the extension — `pi list` names installed packages, and its own directory is under the
   agent dir. Cheap, and cacheable the way `AiAgentCatalog.Locate` caches a binary for thirty seconds.
2. **Read** `~/.pi/agent/settings.json` → `localllm.servers[]` and match one by `baseUrl` against the
   instance's provider endpoint.
3. **Allow** the pairing when both hold, and have `PiAgent.QualifiedModel` answer
   `localllm-<slug>/<model>` for that server.
4. **Refuse with the way out** when they do not: name `pi install npm:pi-localllm-provider`, and say
   that the server has to be added there — a refusal that points at the fix rather than closing a door.
5. **Do not write that file.** It is global, shared with whatever the user set up by hand, and nothing
   here owns it. Adding a server is the wizard's job.

### Why refusing, and not a warning

pi never fails for want of a provider — it has a default, and it splits `--model` on the first `/`. So
`google/gemma-4-12b`, which is what an LM Studio instance actually stores, is read as **provider
`google`**, ignoring any base URL:

```
Warning: Model "gemma-4-12b" not found for provider "google". Using custom model id.
No API key found for google.
```

That run stopped only because there was no Google key on the machine. With one present it would have
completed **remotely and billed**, while the tile, the row and the header all said LM Studio. Whether
the substitution is noticed depends on whether the user happens to lack a key, which is not a property
anything should rely on — so the tile is refused until the pairing can be checked rather than hoped for.

---

## More subscriptions through CCS

**What is already built.** [CCS](https://github.com/kaitranntt/ccs) wraps **CLIProxyAPI**, a local OAuth
proxy on `http://127.0.0.1:8317` that serves an Anthropic-flavor endpoint backed by an OAuth
subscription, and mTiles runs Claude Code through it on a **Codex** subscription with no API key.
Everything the first version of this entry asked for is in: `CcsProvider` (id `ccs`, Anthropic flavor,
paired with Claude Code only), the provider form's **Install CCS** button while `ccs` is missing and
**Auth Codex** while no `~/.ccs/cliproxy/auth/codex-*.json` exists — both run in a visible terminal
tile — the note saying what CCS is for, `IManagedAiProvider.EnsureRunningAsync` starting
`ccs cliproxy start` from `AgentModelResolver.ResolveAsync` before every launch, and the model list read
from the proxy's own `/v1/models`. See `CLAUDE.md` → `Services/Providers/`.

**One thing still unconfirmed.** The `codex-` prefix of the token file Auth Codex looks for was
inferred from the neighbours' measured naming (`gemini-…`, `kiro-…`, `xai-…`), not measured for codex
itself. If a real login writes a different name, the only symptom is the Auth Codex button staying on
screen after a successful sign-in — confirm it against the first one and pin it in `CcsProviderTests`.

**What is left — more subscriptions.** The point of CCS is that CLIProxy speaks to *many* OAuth
subscriptions. When a second one is wanted, choosing CCS on the form should grow a **subscription
choice** (Codex / Gemini / Kimi / …), each with its own auth flow (`ccs <provider> --auth`), its own
token directory under `~/.ccs/cliproxy/auth/`, its own model spellings and windows. Nothing in the
shape already built blocks it — the provider stays one, the choice is an instance field — but each
subscription is a measured integration of its own, and one that works should ship before a chooser
promises five.

---

## Agent conversation tile — what comes next

**Where this comes from.** The Agent tile (`agent-conversation`, commit `58be2f2`) shipped with the
contract, the store, checkpoints and a session for every agent; these are the gaps left after it, in the
order agreed on 2026-09-15. Background and measurements: [`AGENT-CONVERSATIONS.md`](AGENT-CONVERSATIONS.md).

### 1. Images in the composer — done

**Done 2026-09-15**: paste, drop and pick, thumbnails, sent by every session but agy (text only). See
[`AGENT-CONVERSATIONS.md`](AGENT-CONVERSATIONS.md) → *Images*. What follows is what it was.


**Now:** `SendMessage`, `AgentTurnInput` and every session already carry `ImageAttachment`s, and each
session knows its agent's shape (Claude's base64 block, codex's data URL, opencode's file part, pi's
`images`, ACP's `image` block; agy takes text only and says so). The composer has no way to add one.
**To settle it:** paste (Alt+V, the Goal tile's gesture) and drop into the composer, thumbnails with a
remove button, sent with the next message, drawn in the user's message in the timeline.

### 2. Model and permission mode inside a running conversation — done

**Done 2026-09-15**, with one limit left: opencode is offered only its TUI's modes. See
[`AGENT-CONVERSATIONS.md`](AGENT-CONVERSATIONS.md) → *Switching model, mode and effort*. What follows is what it was.


**Now:** the instance's model and mode are fixed at launch; changing them means editing the instance and
restarting the agent. **To settle it:** a model and mode chooser in the strip, applied by each agent its
own way — Claude `set_model` / `set_permission_mode` control requests, codex per `turn/start`, opencode per
prompt, pi `set_model` / `set_thinking_level`, ACP `session/set_model`; where an agent cannot switch live
(agy, Grok's permission mode) the change restarts the session on the same conversation, and the tile says
so.

### 3. Importing conversations started outside mTiles — a second source for the picker

**Now:** an Agent tile can be pointed at any conversation **it** has held in this workspace
(`IConversationStore.List`, the chooser in the strip, `conversationId` in the layout). What is missing is
everything held anywhere else: a conversation started in a Terminal agent tile, or in the CLI's own
terminal, is in *its* history and not in ours, so `/resume` in Claude Code reaches conversations this list
does not. t3code reads Claude Code's `~/.claude/projects/**/*.jsonl` and codex's `rollout-*.jsonl`, matches
the recorded cwd to the project and imports the text of the last 30 days, keeping the id so the conversation
continues.

**To settle it:** the same, per agent class — each CLI keeps its transcript differently — offered as a
**second source of the list that already exists** rather than as a screen of its own.

**The two sources overlap, and that is the part worth designing first.** A conversation held in an Agent
tile is in both stores: ours, because we recorded it, and the CLI's, because the CLI wrote its own
transcript at the same time. Listed naively it appears twice. What says they are the same is the **resume
token** — `ConversationRecord.ResumeToken` is precisely the CLI's own id for it — so the merge is a
deduplication on that, and **ours wins** wherever both exist, because ours carries the checkpoints, the diff
and the Undo that theirs cannot.

**What an imported row is honestly worth**, and the picker should say so rather than let it be found out:

| | Held here | Imported |
|---|---|---|
| Transcript | ours, drawn as it always was | mapped out of their format, one reader per agent |
| Checkpoints, **Undo changes** | from the first turn | **none** before the import — we never took them |
| Continuing it | works | works: their id *is* the resume token |

**Per agent, what is actually there** (unmeasured except where the session code already says so): Claude
Code and codex keep a file per conversation and are the two t3code reads. opencode keeps its own sessions
and is asked over its server rather than read off disk — which is already how `OpenCodeServerSession`
resumes one. pi's session id is **ours**, a GUID this application makes, so there is nothing of pi's to
import that we did not name. agy and Grok were not measured. **An agent with nothing to read should offer
nothing** rather than an empty list that reads as a fault.

**One rule this must not break.** An imported conversation is still that agent's: it is listed for the
agent that holds it, picking it moves the tile onto an instance of that agent, and a machine with no such
instance is told why — which is what the picker already does for our own rows.

### 4. Pruning

**Now:** nothing removes a closed tile's conversation from `conversations.db`, nor
`refs/mtiles/agent-sessions/*` of a forgotten tile, nor `refs/mtiles/before-restore/*` written by every
Undo. **To settle it:** a bounded sweep — conversations whose tile id no layout holds after N days, the
newest K before-restore refs per repository.

### 5. A second viewer: a browser, and another mTiles

**Now:** the desktop tile is the only viewer, and the machine it runs on is the only place the work is.
Everything a second viewer needs already exists: `AgentEvent` and `AgentCommand` as JSON with their own
discriminators, `AgentSessionJson.Options` (camelCase, written for exactly this), the pure
`ConversationReducer`, a store whose events carry **sequence numbers**, and
`AgentConversationHost.ExecuteAsync` as the one entry point with `Changed` as the one way out.

**What is wanted, and it is two things that are one thing.** A browser drawing a conversation running on
this machine; and **another mTiles** drawing a conversation running on a machine somewhere else — the
laptop at home, reached from the desk at work — so that the workspaces list holds this machine's
workspaces and the other machine's, and opening one of theirs opens their tiles. Both are the same
feature seen twice: *a client that is not the process the work is running in*. Building the browser view
against a contract and then bolting a peer link onto the side of it would produce two protocols and one
of them would rot. **One contract, two transports.**

**What it is not, and this is the line from *What not to chase* above.** The other mTiles is a **viewer**,
not a second runtime: no process moves, no PTY is handed over, nothing detaches and re-attaches, and
neither side becomes a server the other's tiles live in. The work runs where it always ran — the
repository, the agent CLI, the subscription and the checkpoints are all on the host machine — and what
crosses is what the host already recorded plus the commands the user types. That is why this is
affordable while item 5 of the Herdr section is not.

#### What can travel, and what it costs

| Kind | What a viewer needs | Cost |
|---|---|---|
| **Agent conversation** | the event stream and `ExecuteAsync` | already built; this is the whole argument for the event contract |
| **Usage** | `AiUsageReport`s, read-only, refreshed on a timer | a serialization and nothing else |
| **Note**, **Todo** | the text, and edits back | cheap, but needs a rule for two editors — last write wins is a lost paragraph |
| **Goal** | `GoalTileState` as a snapshot plus a transcript | its state is a snapshot, not an event log, so this is a read model written for the purpose |
| **Git** | request/response: status, diff of a path, and the destructive actions | plausible; a diff is text. Commit/discard **from a viewer** is a grant of its own |
| **Terminal** | a live byte stream both ways, resize, and scrollback on attach | its own decision, below |
| **Database** | nothing | **refused.** The bridge is bound to localhost by design and checks the `Host` header against it; a peer querying this machine's databases is a new grant with a new threat model, and it is not this feature's |

**The terminal is the one worth refusing first and revisiting later.** `Tailcat.Link.OpenChannelAsync`
is exactly the shape a PTY wants — a live, unresumed stream — so the transport is not the problem. What
is: the viewer's terminal is a second `TerminalControl` with its own cell grid, so resize is a
negotiation rather than a message; scrollback has to be replayed on attach or the tile opens blank on a
session that has been running for hours; and latency that is invisible in a conversation is unusable at a
shell prompt. A conversation tolerates a second of delay because it is a conversation. Ship the
conversation first and let the terminal be judged on a link that already works.

#### The shape of it

1. **`mTiles.Link` — a third project, no Avalonia, the same rule `mTiles.AgentSessions` follows.** It
   holds the wire contract (a versioned handshake, a directory of what is shared, and per-subscription
   snapshot + tail), an `ILinkTransport` seam, and the two halves: a **host** that serves what this
   machine shares, and a **client** that holds a read model of somebody else's. `Tailcat.Link` is then a
   dependency of one adapter rather than of the application, which is also what lets the WebSocket
   transport serve the browser through the same contract and `Tailcat.TestSupport`'s in-memory relay
   drive the tests without a network. **The browser half costs no new dependency and little new ground**:
   `mTiles.csproj` already carries `<FrameworkReference Include="Microsoft.AspNetCore.App" />` for the
   phone bridge, and `PhoneBridgeServer` is the worked precedent for every part of it — a Kestrel host
   with one delegate and `UseWebSockets`, a single-use token redeemed for a session, a `Host` allow-list,
   SNI certificate selection — with `PhoneBridgeManager`'s `ShouldKeepRunning`/`StartAsync`/`StopAsync`/
   `DisposeAsync` as the lifecycle shape `App.ReleaseBackgroundServices` already knows how to shut down.
2. **Subscribe by sequence number.** `IConversationStore.LastSequence` and the numbering on every event
   already make this free: a client says "I have up to N", the host answers with the tail and then the
   stream. A link drops every time a laptop lid closes, so *resume* is the normal case and not the
   failure case — the same reason `Tailcat.Link` resumes a half-sent file.
3. **What produces the state is a seam; what draws it is not.** `AgentConversationTileViewModel.Draw` is
   already a pure function of `ConversationState` — it syncs collections and scalars and knows nothing
   about where the state came from, and even its one Avalonia coupling, the `post` callback, is a
   constructor parameter. What ties it to this machine is **six call sites**: `CreateHost` (which builds
   the `AgentConversationHost` and its `GitTurnCheckpoints`), `host.Changed`, `host.ExecuteAsync`,
   `host.DiffAsync`, `host.ChildProcessId` and `_sessionStarter`. Those are the interface, with a local
   implementation over the host and a remote one over a subscription. **Not** the same tile pointed at a
   different service: `TileContext` is a `WorkingDirectory` on this disk and this machine's
   `SettingsService`, and a remote tile has neither — so the link service is handed to the kind from
   `App.BuildTileCatalog`, the way `IConversationStore` already is, rather than added to `TileContext`.
   A remote conversation's chooser lists the *remote* machine's agent instances, which is the point — the
   subscription, the accounts and the repository are all over there.
4. **Two of the six do not travel, they proxy.** `ITurnCheckpoints` is `git` against a working directory
   and two ref namespaces on the host's disk (`refs/mtiles/agent-sessions/`, `refs/mtiles/before-restore/`),
   and `ChildProcessId` is a pid in the host's process table. So a remote tile's diff and its **Undo
   changes** are requests that run `git` over there and answer with text — which is the right answer
   anyway, because the checkpoints are ours rather than the agent's and undoing a turn must happen where
   the files are. It is also the point at which *see* stops being enough and a grant is needed.
5. **The workspaces list grows a machine it belongs to.** Local workspaces first, then a section per
   connected peer, each row carrying the peer's name; the "switch everything to remote" gesture is a
   filter over that grouping rather than a mode, because a mode hides half of somebody's work and the
   panel already has pinning, sorting and a filter box to do it with. `Workspace` gains an origin; a
   remote row shows no local path, because it has none.
6. **Activity travels first, because it is nearly free and worth the most.** `WorkspaceViewModel.Activity`
   already folds every tile's `TileActivity` into one answer per workspace. Sent over the link, a remote
   row wears the same still `AlertCircleOutline` a local one does — *an agent at home has stopped to ask
   something, and it is visible from the desk at work*. That is the feature people will describe this
   whole entry as.
7. **Nothing of the peer's is written to this disk.** The client's state lives in memory for as long as
   the link does; `conversations.db` stays the host's. A conversation carries prompts, diffs and somebody's
   source, and the machine viewing it is often the one the user controls least — a work laptop accruing a
   local copy of home's repository is the failure this rule exists to prevent. An explicit per-peer
   opt-in could change it later; the default must not.
8. **Pairing is not permission.** A `Tailcat.Link` invitation code proves *which machine*, and nothing
   more. What a peer may do is a grant per peer per workspace, stored beside the sign-ins: **see** (the
   timeline, the activity), **send** (type into a conversation), **approve** (answer an agent's permission
   request). The third is the largest single grant in this application — it is a remote machine allowing
   an edit in this one's repository, which is `bypass` reached by a different road — so it asks the way
   `bypass` asks, and a workspace is shared with nobody until somebody shares it.
9. **A handshake with a version, and an unknown event is drawn rather than dropped.** Two linked mTiles
   will be different builds — Velopack updates one of them on a Tuesday — so the contract is versioned and
   an event type this build does not know is drawn as *something happened here that this version cannot
   show*, never silently skipped. That is the rule `TileNode` already follows for a tile kind it does not
   recognise, and for the same reason: the newer side must not lose what the older one cannot render.

#### What to weigh before starting

- **The link lives only while the application does.** Close mTiles at home and there is nothing at home
  to connect to — the same property the phone bridge has. Everything above is honest about that; a
  headless or tray host that keeps the link up without a window is a separate decision, and it is the one
  thing here that starts to look like the long-lived process this project has twice declined to build.
- **`Tailcat.Link` is one person's port and says so.** Its relays are Tailscale's public DERP servers,
  shared and rate-limited; the direct path needs QUIC (Windows 11+, macOS, or `libmsquic` on Linux) and
  falls back to relay when hole punching fails; and its README states plainly that the security design
  has not been externally reviewed. That is acceptable for *my two machines* and is not acceptable as the
  basis for sharing a repository with a colleague — which is why the first version is one person's
  machines, and multi-user is out of scope rather than merely unbuilt.
- **The browser client is the cheaper half and should go first**, because it exercises the contract with
  no NAT, no pairing and no second machine, and because a page that draws a conversation is also the proof
  that the state is genuinely serializable — a remote mTiles could accidentally pass by sharing types.
- **One open question worth settling before the first line: events or state on the wire.**
  `ConversationState` is itself fully serializable (`TimelineEntry` and `WorkItem` carry their own `kind`
  discriminators), so a host could send folded state and spare the client a reducer. Events are the
  cheaper stream, resume by sequence number is free with them, and the client keeps the same
  `ConversationReducer` — but that makes the *client's* build of the reducer authoritative over what the
  host recorded, which is exactly what stage 9 is guarding against. The likely answer is both: events
  while the versions agree, a folded state as the fallback and as the snapshot a fresh subscription opens
  with. It should be decided once, in `mTiles.Link`, rather than per call.
- **A message typed on the viewer is still the user's own.** `MessageEntry` records the user; nothing in
  the events says *which machine* it was typed on, and the first version does not need it to, because both
  machines are one person's. It stops being true the moment a second person is paired, which is the
  cheapest reason to keep multi-user out of scope until the grants above have been lived with.

### 6. Carrying the work across a change of agent

**Now:** the agent is picked in the conversation and settles the moment something is said in it
(*Which agent holds the conversation* in [`AGENT-CONVERSATIONS.md`](AGENT-CONVERSATIONS.md)). Moving from
Claude Code to codex halfway through a piece of work therefore means a new conversation and typing the
state of it again. Nothing carries: the resume token is the issuing CLI's, and the transcript in the store
is that agent's.

**What is wanted:** switching agent mid-task leaves the new one able to carry on — knowing what is being
built, what has been decided, which files have moved and what is left — without pretending it is the same
session. This will never be lossless, and saying where it loses is part of the design rather than an
apology: the outgoing agent's own reasoning, its cached reads of the tree and its pending approvals do not
exist outside it.

**What travels, and why.** Everything below is already in `ConversationState` or in git, so a handover is a
fold over what this application already owns rather than anything asked of a CLI:

| Carried | From | Why it survives the move |
|---|---|---|
| What was asked for | every `MessageEntry` of the user, verbatim | intent is the one thing no summary may paraphrase |
| What was decided | answered `QuestionsAsked` rounds, approved plans | the answers are the user's, not the agent's |
| The plan as it stands | `PlanUpdated` steps with their status | it is already the agent's own account of what is left |
| What has changed on disk | `CheckpointCaptured` files, `+`/`−` per path, and `git diff` of the turn range | the tree is the shared state; the new agent can read it |
| Where it stopped | the last assistant message, and any notice that ended the turn | says whether the work is mid-edit or between tasks |
| Not carried | tool call transcripts, reasoning, the other CLI's context window | verbose, model-specific, and re-derivable by reading the tree |

**The shape of it.**

1. **`ConversationHandover` — pure, in `mTiles.AgentSessions`.** `ConversationState` → a Markdown brief
   with those sections, newest first, fitted to a budget the way `AiProcessRunner.PromptBudget` fits a
   prompt: the goal and the open plan are kept whole, older turns are dropped before newer ones, and what
   was dropped is said in the brief rather than silently missing. Pure, so it is argued in a table test
   against a recorded conversation instead of against a running CLI.
2. **A summary written by the agent that is leaving, when it can be asked.** One turn on the outgoing
   session — "say what another assistant would need to carry this on" — is better than any fold we can
   write, because it knows what it was in the middle of. It is an *addition* to the brief, never a
   replacement: a refusal, a dead process or a switch made because the agent is stuck must still hand over
   something. Asked with the user's consent, since it costs a turn and money.
3. **Delivered as the new session's first message, and as a file.** The brief goes into
   `.mtiles/handovers/<conversation>-<n>.md` (ignored through `GitIgnoreFile`'s marked block, like every
   other file of ours in somebody's repository) and the first message names the file and carries the brief
   inline while it is small. A file is what lets an agent re-read the handover later in the turn, which is
   the point at which a long inline message has already scrolled out of its attention; agy is the one that
   works in its own scratch directory without `--add-dir`, so it gets the inline copy and says so.
4. **The seam is recorded, not hidden.** `HandoverRecorded(fromAgentId, toAgentId, brief)` in the event
   contract, drawn in the timeline as a row saying the work moved and what was handed over, foldable to
   read the brief. A conversation that lies about being continuous is worse than one that says where it
   was cut.
5. **One conversation, several segments.** Today `ConversationRecord.AgentId` binds the whole conversation
   to one agent. It becomes the *current* segment's agent, with each segment carrying its own agent and
   resume token, and the reducer folding every segment into one timeline: the transcript stays on screen
   across the move, only the active segment's token is ever handed back to a CLI, and the checkpoints —
   which are ours, not the agent's — carry on unbroken. This is the one change to the store's shape, and
   it is what makes "switch and keep reading what happened" true rather than a second tile beside the
   first.
6. **What the chooser then says.** Another agent stops being refused and becomes *Switch agent and hand
   over* — a confirmation naming what travels and what does not, with the summary turn offered in it. The
   refusal stays for the case it was written for: a tile whose stored conversation belongs to an agent that
   is not running here at all.

**How good it can be, measured rather than hoped.** A live test that starts a task on one agent (edit a
file, leave it half done), switches, and asks the second agent what it is working on — it passes when the
answer names the file and the remaining step without being told again. Each agent is measured separately,
because the failure is model-shaped: a brief that carries Claude Code across the seam may leave agy, whose
input is text only and whose scratch directory is its own, with nothing it can read.

**What this is not.** It is not a transfer of the other CLI's session, and no flag of theirs is guessed for
one. Nothing here reaches into `~/.claude` or a rollout file to replay somebody else's transcript into
another vendor's model: what is handed over is what this application recorded and what is on disk.
