# Agent conversations

An **Agent** tile holds an AI coding agent as a conversation: messages, every tool call as a row,
approvals and questions as blocks, the agent's plan, the context it has used, and — for every turn that
changed files — a diff and a way back. The **Terminal agent** tile beside it is unchanged: the same agent's
own TUI in a terminal.

The idea and most of the protocol knowledge are ported from t3code (`D:\work\sources\t3code`), which
drives every agent it supports through a structured protocol and never reads a terminal.

## The shape of it

```
 agent class (Services/Agents)         mTiles.AgentSessions (no Avalonia)            view
 ─────────────────────────────         ─────────────────────────────────────         ─────────────────────
 ClaudeAgent : IConversationalAgent ──► IAgentSession ──emits──► AgentEvent ──┐
 CodexAgent, OpenCodeAgent, PiAgent,                                           ▼
 AntigravityAgent, GrokAgent           AgentConversationHost  ── numbers, stores (SQLite), folds
                                          │    ▲                 ConversationReducer ──► ConversationState ──► AgentConversationTileViewModel
                                          │    └── AgentCommand (send, interrupt, approve, answer, restore)
                                          └── ITurnCheckpoints (git trees under refs/mtiles/agent-sessions/)
```

**Three rules hold it together.**

1. **The events are the contract.** `mTiles.AgentSessions.Events` is the only vocabulary a session may
   speak and the only thing a view reads. Nothing about any CLI is in it. It is serialized with a `type`
   discriminator and camelCase (`AgentSessionJson`), so a browser receives the same records the desktop
   draws.
2. **Differences live in the agent's class.** Each agent implements `IConversationalAgent.CreateSession`
   and owns every word of its protocol: `Sessions/Claude`, `Sessions/Codex`, `Sessions/OpenCode`,
   `Sessions/Pi`, `Sessions/Antigravity`, `Sessions/Grok`. A separate interface rather than more members on
   `IAiAgent`, which is already the whole of the terminal launch, the Goal run, sign-ins and usage.
3. **What is drawn is always recomputed.** The store keeps events, never a picture; `ConversationReducer`
   is pure and replays them into `ConversationState`. A change to how the timeline groups work changes
   every stored conversation at once — the construction t3code's event store has.

## Why a separate project

`src/mTiles.AgentSessions` has no reference to Avalonia or to the application. The compiler keeps the
boundary: the contract, the reducer, the store, the checkpoints and the protocol plumbing (a process over
stdio, JSON-RPC, ACP) cannot pick up a view by accident, and a web server can reference the library as it
is. What stays in the application is what knows about agents and instances: the sessions, the launcher,
the tile.

## The host

`AgentConversationHost` is what every viewer talks to — the tile now, a browser later.

- **One lock, three steps**: an event is numbered, folded into the state and queued for the store, in that
  order. The store is written off the caller's thread; consecutive deltas of one message are merged into
  one row (`EventBatch`), which replays to the same text.
- **A user message is recorded by the host**, never echoed by the agent, so it is drawn the same way for
  every agent. Sessions drop their CLI's echo (opencode sends one).
- **A turn is bracketed by two checkpoints**: one before the message is handed to the agent, so edits made
  between turns are not blamed on it, and one when the turn ends. The second carries the files changed
  between the two.
- **The resume token is stored beside the conversation** and handed back to the same agent only. A tile
  moved to another agent drops the old conversation rather than giving its token to a CLI that never saw it.

## Checkpoints

`GitTurnCheckpoints`, the construction `GoalBaseline` already uses: a private `GIT_INDEX_FILE` copied from
the real one, `add -A`, `write-tree`, `commit-tree` with no parent, and a ref under
`refs/mtiles/agent-sessions/<conversation>/<n>`. Untracked files are included; `.gitignore` is honoured;
nothing the user can see moves. Diffs are tree against tree — never a tree against the working copy, which
reports a once-untracked file as deleted.

**Undo changes** restores the checkpoint taken as the turn began. Not `git restore` plus `git clean` (what
t3code runs): `restore` goes by the index, so a file that was untracked at the checkpoint is not put back,
and it rewrites the user's staging. Instead the tree is checked out through a private index and whatever is
on disk now that the checkpoint does not hold is deleted, both lists scoped to the workspace so a workspace
inside a larger repository never reaches past itself. Ignored files are never touched. The agent's own
conversation is not rolled back — it still remembers the turn.

## Per agent, and what was measured

Measured 2026-09-15 on this machine unless it says otherwise. Each is somebody else's contract and is
pinned by `AgentProtocolMapperTests`; `LiveAgentConversationTests` (opt-in, `MTILES_LIVE_AGENTS=claude,…`)
runs one real turn per agent through launcher, session, host and checkpoint.

| Agent | Transport | Live turn | Approvals |
|---|---|---|---|
| Claude Code 2.1.272 | `claude -p --input-format stream-json --output-format stream-json --verbose --include-partial-messages --permission-prompt-tool stdio`; control requests on the same pipe (checked against the Agent SDK's `sdk.mjs` 0.3.272) | ✔ | ✔ `can_use_tool`, live |
| codex 0.153.2 | `codex app-server`, JSON-RPC with no `jsonrpc` field (schema from `generate-json-schema`) | ✔ | wired (`requestApproval`), not triggered live — ask mode wrote inside the workspace without asking |
| opencode 1.18.18 | `opencode serve` on loopback with its own password; HTTP + `/event` SSE | ✔ (with a model the account supports) | wired (`permission.asked` → `/permission/{id}/reply`), not triggered live |
| pi 0.84.4 | `pi --mode rpc` (from its `docs/rpc.md`) | ✔ | pi has none; extension dialogs become questions |
| agy 1.1.26 | print mode kept open: `--input-format stream-json --output-format stream-json --print=` | ✔ | none — headless agy auto-denies and says so; a denial becomes a notice |
| Grok | ACP: `grok [--permission-mode X] agent stdio` plus `x.ai/*` extensions | **not run — not installed here** | ACP `session/request_permission` |

### Things worth knowing before touching a session

- **Claude Code**: the CLI writes one whole `assistant` line *per content block*, so the whole text and its
  streamed deltas are matched by block kind (`ClaudeStreamMapper.FindIndex`). A new conversation gets a
  fresh `--session-id`, never the tile's id — "New conversation" in the same tile must not reopen the old
  one. `AskUserQuestion` and `ExitPlanMode` arrive as permission requests and are answered as questions and
  as a plan to approve.
- **codex**: the thread id is the resume token and comes back at once. Behaviour maps to approval policy
  and sandbox by the same table the TUI's flags use (`CodexAgent.AppServerPermissions`).
- **opencode**: the user's own message comes back as a part of a `user` message and is skipped; an idle
  status only ends a turn once that turn has been busy. A message's error and `session.error` carry the
  same sentence, so only the second is used.
- **agy**: t3code speaks ACP to a separate `agy_acp_server` the CLI install does not carry, so this uses
  the print mode. Its reply is not streamed — it arrives whole in `result.response`. No BOM on stdin
  (refused), every message needs `event`, text only. Without `--add-dir` it works in its own scratch
  directory. There is no interrupt message: stopping ends the process and the next message resumes with
  `--conversation`.
- **Grok**: everything is t3code's (`GrokAcpSupport.ts`, `XAiAcpExtension.ts`). The first run against a
  real `grok` should be read against `GrokAcpSession`'s remarks and corrected where they are wrong. The
  terminal Grok tile claims no resume and no print mode — both would be guessed flags.
- **Every session registers its wait for an answer before announcing the request.** A viewer can answer
  from inside the announcement (the live test does); an answer that finds nothing waiting is lost and the
  turn hangs. Found by the live Claude run.
- **A process the session stopped is `Stopped`, not `Failed`** (`AgentProcess.StoppedByUs`): a process
  killed on the way out exits -1, which is not the agent failing.

## Storage

`%APPDATA%/mTiles/agent-conversations/conversations.db` (Linux: `~/.config/mTiles/…`), in a directory
created owner-only because SQLite writes `-wal` and `-shm` beside the file. Two tables: `conversations`
(id = tile id, agent, directory, resume token) and `events` (conversation, sequence, type, JSON payload).
An event a build cannot read — written by a newer one — is skipped with a log line. Nothing is pruned yet:
a closed tile's conversation stays — and is now reachable rather than orphaned, because a tile can be
pointed at it (below).

## Which conversation a tile is showing

**Every conversation ever held in this workspace is in the list** (`IConversationStore.List(directory)`,
the chooser in the strip's right-hand corner, which holds conversations and nothing else). Until it existed there was no way back to any of them: the
conversation *was* the tile's id, so the only way to start a new one in a tile was to write over the old.

- **The tile keeps a `conversationId` in its layout, and only once one has been chosen.** Absent means the
  tile's own id, so a layout written before this existed opens exactly the conversation it always did and a
  tile nobody has pointed elsewhere saves the same bytes as before. A field rather than a change of `TileId`
  because the id is the tile's identity to the layout, and two tiles showing one conversation would
  otherwise be two leaves saved under one id.
- **"New conversation" no longer forgets, and always asks.** It is the strip's own button beside the list
  (`MessagePlusOutline` in the accent — the one "start another" glyph the Goal and terminal agent tiles
  share), not a row in it, and it asks first every time, since to the eye it clears the screen; an
  unwired dialog is a no. It opens a new one beside the old, which stays in the list. Forgetting is **Delete this conversation**, which says what it takes and asks first.
- **A conversation is one tile's at a time** (`OpenConversations`). Two hosts of one conversation each
  number their events from what the store held when they were built, so both write the same sequence
  numbers and the store keeps whichever landed last — one of the two is lost, silently. The second tile is
  refused with the reason.
- **The agent comes with the conversation.** Picking one held by another agent moves the tile onto an
  instance of that agent; a machine with no such instance is told so rather than shown the transcript on a
  CLI that has never seen it. The same rule as *Which agent holds the conversation* below, from the other
  side.
- **The title is the user's own first words**, cut to a line (`ConversationTitle`, pure, so a browser
  listing the same conversations calls them the same things). Nothing derived is stored for it: the opening
  is read back out of the first `UserMessageAdded`, which is cheap because `events` is keyed by
  `(conversation_id, sequence)`. The list is read when it is opened, never on a timer — the answer moves
  only when something is said, and the tile redraws every frame while an agent replies.

**What the picker cannot promise, per agent.** The transcript is always drawn in full, because it is *our*
record — but whether the **agent** remembers it is the CLI's answer, and three of the six are quiet about
failing:

| | How it resumes cold | If it refuses |
|---|---|---|
| Claude Code | `--resume <token>` | kills the process, starts fresh, **says so** in a notice |
| codex | `thread/resume` | falls back to `thread/start`, **says so** |
| opencode | its own server's stored session | **says so** ("could not be found in opencode") |
| pi | `--session-id <token>` — and the token is **ours**, a GUID this application made | creates an empty session under that id; `get_state` answers `messageCount: 0` and `ResumeCheck.PiLost` **says so** |
| agy | `--conversation <id>` | starts a new conversation and exits 0; its `init` names a different id and `ResumeCheck.AgyLost` **says so** |
| Grok | ACP `session/load` (1.0.34 advertises `loadSession`) | a JSON-RPC error, `-32603 Path not found`; the ACP session starts a new one and **says so** |

**None of the six fails in silence any more** — measured live 2026-09-17 against pi 0.84.4, agy 1.2.3 and
Grok 1.0.34, which is what this table used to be wrong about. pi and agy did fail quietly, and both turned
out to say it in a structured field before the first message: pi's `get_state` (which `PiRpcSession`
already asks for) and agy's `init` line (written at start-up, with nothing on stdin). Grok never was
silent on 1.0.34 — the "discards the token" row described a CLI without `loadSession`, which this one is
not, and `AcpAgentSession` now says so in that case as well: a resume token it has nowhere to hand back is
a notice before the first message, exactly as a `session/load` that fails is. That closes *A visible sign that a resume actually happened* ([`ROADMAP.md`](ROADMAP.md), the Herdr
section, item 1) for the Agent tile, and it is what lets `SkillChangePolicy` restart an idle agent whatever
its conversation holds: the worst a restart can now do is lose the agent's memory **and say so**.

When each one fails to find a conversation, which is worth knowing because two of the three are about
*where* rather than *whether*:

- **pi** keys a session by the **exact working directory** — a subdirectory of the same repository is a
  different key — and by its agent directory, so another sign-in is another store. The session file is
  written only at the first message, so a conversation nobody spoke in always comes back empty; that is
  why the check asks whether our own store holds a user message first (`AgentSessionLaunch.HasHistory`).
  **A trap after the fact**: once something has been said into the empty session pi created, the next
  start in that directory resumes *it*, without a warning, with the short history. The notice on the start
  that lost it is therefore the one that matters.
- **agy** keeps a conversation as `~/.gemini/antigravity-cli/conversations/<id>.db`; without that file it
  is gone, and a failed resume still leaves an empty new conversation behind on disk. The new id is adopted
  — the old one does not exist — but no longer silently: it used to be written into the store as though it
  were the same conversation. Unmemoried, agy also goes looking for what it was asked about: the measured
  failed resume spent 98 s and 282k tokens to answer that it did not know.
- **Grok** keys a session by the **cwd string as it was spelled** at `session/new`: forward slashes or a
  trailing separator make an existing session "Path not found" (case does not matter on NTFS). mTiles
  passes the workspace directory verbatim both times, which is why it holds — a normalisation added to that
  path later would quietly start losing Grok conversations.

Conversations held **outside** an Agent tile — in a Terminal agent tile, or in the CLI's own window — are
not in this list, because they are in the CLI's history and not in ours. Adding them as a second source of
the same list, deduplicated by resume token, is [`ROADMAP.md`](ROADMAP.md) §3.

## Which agent holds the conversation

**The tile asks nothing before it opens.** A terminal tile has to know its shell before anything can run; a
conversation with nothing in it is bound to nobody, so the agent is picked in the strip beside the model.
A new tile opens on the instance the last one was pointed at (`AppSettings.LastAgentInstanceId`), and on the
first available one before there is such a thing.

**Once the conversation has something in it, the agent is settled** (`IsBoundToItsAgent`) — t3code's rule
(`ProviderCommandReactor` refuses `thread.turn.start` across drivers) and ours for the same reason: the
resume token belongs to the CLI that issued it and the stored events are that agent's. Another agent is
offered, dimmed and carrying the sentence saying to start a new conversation (dimmed rather than disabled —
a disabled item is out of Avalonia's hit test, so the reason would never be read); another instance of the *same*
agent — another account, another model — is taken, and restarts the session on the same conversation.
"Something in it" is read from the store as well as from the screen: a restored tile is empty until its start
has read the store, and another agent's stored conversation keeps that agent pickable and the picked one refused.
A stored record alone binds nobody — a host writes one as its session starts — so it is a stored user message
that counts, and a switch is refused before it is remembered as last used or saved into the layout.
"Last used" is written whenever a message is sent, not only when the chooser moves.

Carrying the work across that seam — a brief built from what this application recorded, handed to the new
agent as its first message — is [`ROADMAP.md`](ROADMAP.md) §6, and is what turns the refusal into a choice.

**Nothing is thrown away by a chooser.** Switching onto a tile that still holds another agent's stored
conversation starts nothing and says so; the way past it is to pick or start another conversation, and
**Delete this conversation** is the one gesture that forgets.

## Switching model, mode and effort inside a conversation

The strip above the conversation holds a model field (pick from what the session lists, or type a name and
press Enter) and choosers for the permission mode and the effort. What they offer is the session's answer
(`SessionOptionsReported`); what they send is `ChangeSessionSettings`, handled by the host.

- **Modes and efforts are the application's words**, never an agent's: an `AiBehaviour` or `AiEffort` name,
  narrowed to what the agent supports (`SessionSettingOptions`). Each session translates it into its own
  CLI's spelling. `SessionConfigured.Mode` and `.Effort` carry the same ids back.
- **Each agent switches its own way** (`IAgentSession.ChangeSettingsAsync`), measured 2026-09-15 by
  `LiveAgentConversationTests.Switching_settings_and_sending_an_image`:

  | Agent | Models listed from | Model | Mode | Effort |
  |---|---|---|---|---|
  | Claude Code | `initialize` answer, `models[].value` | `set_model` control request | `set_permission_mode` | restart (`--effort` is a launch flag) |
  | codex | `model/list` | next `turn/start` | next `turn/start` (approval policy + sandbox) | next `turn/start` |
  | opencode | `GET /config/providers`, `provider/model` | next prompt | `PATCH /session` rules + plan agent | — |
  | pi | `get_available_models`, `provider/id` | `set_model` | — (pi has none) | `set_thinking_level` |
  | agy | none (field takes a typed name) | next process | next process | next process |
  | Grok | ACP `session.models.availableModels` | `session/set_model` | restart | `set_model` with `_meta.reasoningEffort` |

- **A change the agent cannot take while it runs asks for a restart** (`SettingsChangeOutcome.NeedsRestart`
  → `AgentConversationHost.RestartRequested`); the tile starts the session again on the same conversation.
  Never under a working agent: the host refuses out loud instead, because a restart would end the turn.
- **"Tool default" always restarts**, whatever the table above says for that agent. It is the absence of a
  flag of ours, and no live switch can say that: Claude Code's `set_permission_mode default` is a mode like
  any other and would override the user's own `~/.claude/settings.json`, while opencode's rules, pi's
  `set_thinking_level` and Grok's `_meta.reasoningEffort` left out simply leave the session as it was — the
  chooser saying it had changed while nothing had. Started again without the flag, the CLI's own
  configuration is back in charge, which is what the choice means.
- **The choice is the tile's, not the instance's**: kept as `SessionOverrides` in the layout (`model`,
  `mode`, `effort` keys) and laid over a copy of the instance at every launch, so switching one
  conversation leaves every other tile on that instance alone.
- **The catalogue is never stored** (`AgentEvent.IsTransient`): `SessionOptionsReported` carries every model
  a session lists — hundreds of them on an opencode installation — and the reducer keeps only the last
  report, so it is folded in and handed to the viewer but not appended. Kept, a tile started thirty times
  would write megabytes of catalogue into one conversation and parse them back on every open; dropped, it
  costs nothing, because the next session reports it again at start.
- **Known limit**: the modes offered are the agent's *interactive* list, which for opencode is the TUI's
  (bypass or its own default) although its server could also ask or plan per session.

## Images and files

Pasted (Ctrl+V when the clipboard holds no text, Alt+V always), dropped on the composer, or picked with the
paperclip. Every image is re-encoded as PNG and scaled to at most 1568 px on its long edge
(`ComposerImages`), refused over 5 MB and beyond ten per message, and once sent shown in the user's message.
Every session already knew its agent's shape — verified live: Claude Code, codex, opencode and pi each
answered "Red" for a red square. agy's stream input takes text only, and the message goes without the image
and says so.

**An image stands where the user put it.** Attaching one inserts `[Image #n]` at the caret (`ComposerEdit`,
the one insertion rule both composers use — padded so a marker or a mention is never welded onto the word
beside it) and the chip above the box is **read off the text** (`ComposerChips`, `ComposerImageChips.NamedIn`):
deleting the marker by hand takes the chip, an undo brings it back, and the chip's `×` takes the marker out.
The image itself waits beside the draft until the message goes, so that undo still has a picture to name.
Numbers are never reused into a gap while the draft is written — removing `#2` must not rename `#3` under the
caret — and **renumbered from one when it is sent** (`OutgoingMessage`), with a marker that names no image
dropped, since it would reach the agent as a picture that is not there.

**The order is the text's, and the protocols carry it** (`ImageMarkers.Interleave`, reached through
`AgentTurnInput.Blocks`): Claude Code, ACP, codex and opencode all take a list of blocks, so the text is cut
at each marker and the image goes between the two halves, the marker staying at the end of the text before it
so the sentence can still name it. A blank piece between two markers is dropped (Anthropic refuses an empty
text block) unless nothing else would be sent. An image no marker names goes after the text — where every
image went before markers existed.

**Any other file becomes an `@` mention** at the caret (`ComposerFileReference`), spelled exactly as the `@`
list spells one — a path, never the contents, because every agent here opens files for itself and pi, agy and
a Goal prompt carry no file at all. A file inside the workspace is named where it is; one from outside is first
**copied into `.mtiles/attachments/`** (`AttachmentStore`, ignored by the Git tile's `.mtiles/` rule, never
pruned) — Claude Code asks before reading outside its directory, codex's sandbox may refuse, and Downloads
empties itself. Over 20 MB, or when the copy fails, the original's absolute path is used and the composer says
so. Every mention that names something on disk gets a chip of its own (`ComposerFileScanner`, one disk lookup
per path rather than per keystroke), removed with its mention by the same `ComposerEdit`.

## What is the Goal tile's

Both tiles are an agent talking in a column, so the parts they share are one definition, not two copies:

- **`Styles/Conversation.axaml`**: message rows and gutters, the ask block (`ask`, `ask-rail`, `ask-marker`,
  `ask-question`, `ask-why`, `ask-option`, `ask-field`), copy buttons, the composer, `chat-action` buttons,
  the thinking dots and `strip-choice`. Each tile keeps only what is its own: the Goal tile's criteria and
  findings, the Agent tile's tool rows and diffs.
- **`@` file mentions**: the same `FileMentionsViewModel` + `FileMentionBehavior` on the composer and on
  every answer box, with Enter left to the suggestions while they are open.
- **Questions** are drawn as the Goal tile's round: a number column, full-width answer rows, a copy button
  per question, "Send answers". The one difference is on purpose: an agent takes its choices as choices (a
  label, several where allowed), so a row is toggled rather than copied into the answer box.
- **Pasting an image** is `ClipboardImage` for both: Alt+V always, Ctrl+V only when there is no text.
- **The composer's keys and gestures** are `ComposerInput`: Enter sends, Shift+Enter breaks the line, the
  frame shows the box's focus and a click on it puts the caret in the box. **Enter has to be caught in the
  tunnel**: a multi-line `TextBox` handles Enter itself before a `KeyDown` wired in markup sees it, so both
  keys used to break the line (`ComposerEnterTests` presses real keys through a window to pin it).
- **Waiting**: `WaitingRow` (the thinking dots, a stage and a clock) fed by `ElapsedClock` — the Goal
  tile's run, the Agent tile's turn.
- **A copy button per message** (`msg-copy` + `CopyButton`), and following the end of the transcript by
  `TranscriptFollow`'s rule.

New UX for either tile goes into these shared pieces, not into one view.

## Not built yet

- Importing conversations started outside mTiles, as a second source of the tile's own conversation list
  (t3code reads Claude's and codex's transcripts) — [`ROADMAP.md`](ROADMAP.md) §3.
- Pruning old conversations and checkpoint refs. It matters more now that a closed tile's conversation is
  reachable rather than orphaned: what the list holds is what the sweep would take.
- A second viewer — a browser, and another mTiles reached over a link, so that a conversation running on
  the machine at home can be read and answered from the one at work. What either needs is already here:
  `AgentEvent`/`AgentCommand` as JSON, the reducer, the store's sequence numbers, and
  `AgentConversationHost.ExecuteAsync` as the one entry point for commands. Both are one contract with two
  transports, and what travels, what proxies back to the host and what a paired peer is allowed to do are
  [`ROADMAP.md`](ROADMAP.md) §5.

## Reading an agent's own session store

Measured 2026-09-18 on Windows against the installed binaries. **Five of the six CLIs keep a record on
disk of the conversations they hold, filed by working directory**, and two things this application could
not otherwise know are read out of it: **which conversation the tile is really in** (the user types
`/clear` or `/resume` inside the TUI and the id in the layout stops being the one on screen) and **how
full the model's context is** (a TUI paints that into its own footer, where no host can read it off a
pseudo-terminal).

The port is `Services/Agents/SessionLogs/IAgentSessionLog`, reached through `IAiAgent.SessionLog`, which
defaults to `null` — an agent whose author has measured nothing keeps the session id it was launched with
and draws no bar, which is exactly what it does today. **Grok answers `null` too, although its store is
the easiest of the five to read** (`GrokSessionLog` is measured and tested): a store is read by the id of
the conversation a tile is in, and a terminal Grok tile never has one — it resumes nothing, and it
captures nothing because its store cannot tell its TUI from an Agent tile's ACP session or a Goal run.
Wired in, the reader would only keep a watcher running for a reading that can never arrive.

| agent | store | session id | working directory | tokens | context window | cost |
|---|---|---|---|---|---|---|
| **claude** | `<config>/projects/<slug>/<id>.jsonl` | the file's name | on the message lines | `message.usage` — input + cache read + cache write + output | — | — |
| **codex** | `<home>/sessions/YYYY/MM/DD/rollout-<stamp>-<id>.jsonl` | in the file name | first line, `payload.cwd` | `last_token_usage.total_tokens` | **`model_context_window`** | — |
| **opencode** | `<data>/opencode/storage/{project,session,message}/` | `ses_…`, the session file's name | `project/<id>.json` → `worktree` | `tokens` on an assistant message | — | `cost`, per turn |
| **pi** | `<dir>/sessions/--<slug>--/<stamp>_<id>.jsonl` | after the first `_` | first line, `cwd` | `message.usage` — input + output + cacheRead + cacheWrite | — | `usage.cost.total`, per turn |
| **grok** | `~/.grok/sessions/<url-encoded cwd>/<id>/` | the directory's name | the directory's name, and `summary.json` | `usage.json` → the last of `turns`, `totalTokens` (never `session.totalTokens`, a running sum) | — | `costUsdTicks`, 1e-10 USD each |
| **agy** | `~/.gemini/antigravity-cli/conversations/<id>.db` | the file's name | **only inside a protobuf blob** | **none anywhere** | — | — |

Six things in there are load-bearing and each was a wrong first guess:

- **The slug is not "replace the separators".** Claude Code and pi turn *every* character that is not a
  letter or a digit into a dash, so `D:\work\sources\kursalpha.eu` is `D--work-sources-kursalpha-eu` —
  the dot goes the same way as the colon. A slug wrong by one character finds no directory, which reads
  exactly like an agent that has never been run in this workspace.
- **Grok url-encodes instead** (`C%3A%5CUsers%5Candrz`), so the colon and the separators survive as
  themselves. A slug would find nothing there.
- **opencode's project id is not a hash of the path.** It looks like one; SHA-1 of the worktree in every
  plausible spelling misses it. The index is read instead, which is right today and free the next time
  opencode changes how it derives the id.
- **The cache counts towards the context and the reasoning does not.** What occupies the window on the
  next turn is what will be sent again. A Claude turn of 6 669 input against 116 608 cache-read is a
  conversation of some 124 000 tokens, and counting the input alone drew it as almost empty.
- **codex's `last_token_usage`, never `total_token_usage`.** The second is every token the conversation
  has ever spent, which runs past the window after a few turns and draws the gauge as permanently full.
- **A usage object of zeroes is a turn that never reached the model** — an auth failure, a refused model.
  Read as the answer it empties the gauge behind a conversation that is still there.

**Only codex names its own window** (`modelContextWindow`), and over the Agent tile's own protocols only
codex and ACP do (`size`). Claude Code's stream reports the tokens and nothing else, so the bar the Agent
tile was built around was drawn for two agents out of six — and never on a subscription. The denominator
is therefore filled in, in this order and in both tiles:

1. **what the agent said**, where it said anything;
2. **`AiAgentInstance.MaxContextTokens`** — the field in Settings → AI. A decision, handed over unchanged,
   the same precedence `ModelContextWindow.Answer` already gives it;
3. **the provider** (`ModelContextWindow.ContextOfAsync`, the same half-hour cache the launch's own
   windows spend). A fact about what is being served, and also what this application hands the CLI, so
   the gauge and the run agree;
4. **the agent's own account** (`IAiAgent.AccountContextWindowAsync`). The only route left for a
   subscription, which has no provider instance at all and is the commonest configuration there is.

Measured 2026-09-18: **`GET api.anthropic.com/v1/models` with Claude Code's own OAuth token answers 200**
and carries `max_input_tokens` per model — 1 000 000 for `claude-opus-5`, `claude-sonnet-5`,
`claude-fable-5-1` and the 4.6–4.8 families, 200 000 for `claude-opus-4-5-20251101` and
`claude-haiku-4-5-20251001`. `ClaudeModelCatalog` asks it with the request shape `ClaudeUsageReader`
already uses — the token read through `ClaudeCredentialStore` but **never renewed from here**
(`LiveAccessToken`): the tile asking runs Claude Code on that same login, which renews it itself, and a
rotating refresh token spent by both at once leaves one exchange refused — possibly the CLI's. An expired
token is no answer until the CLI's renewal lands, and the gauge asks again at its next reading; the status and never the body in a log — plus **two headers, not one**: `anthropic-beta:
oauth-2025-04-20` *and* `anthropic-version: 2023-06-01`. The second is what this endpoint wants and the
usage endpoint does not; left out, the answer is `400 anthropic-version: header is required`, and since
every failure here becomes "no window", the symptom is a bar that silently never appears. Both are pinned
by a test against a capturing handler. **A long-context variant names its own window and outranks the
list** (`ClaudeModelCatalog.VariantWindow`): Claude Code spells those by putting the size in brackets
after the model — `claude-sonnet-4-5[1m]` — and the suffix travels to the API as part of the model
string, so the transcript carries it too. Matched against the catalogue's ids it finds nothing, and the
plain entry beside it is the *short* window, so a session really running on a million tokens was drawn
against 200 000: a full bar from a fifth of the way in, which is the wrong full bar this whole section
exists to avoid. The suffix is read rather than tabulated — a count and a scale — and anything else
falls through to the plain id. The answer is cached for half an hour per credentials file, and a *failed* read is not cached, so a network that came back does not leave
the tile barless for the rest of it. **`max_input_tokens`, never `max_tokens`**: the second sits right
beside it and is how much the model may *write* in one reply (128 000 on the current families), which as
a window would draw every conversation as long past full. The other five agents have no measured route to
their own service, answer null, and show a count with no bar.

**There is deliberately no fifth step, and the families are why.** An earlier version fell back to what
Claude Code documents itself as assuming for a model it cannot verify — 200 000 — and drew a *full* bar
over a conversation of 234k. One account, asked at one moment, serves opus-5 a million tokens and
opus-4.5 two hundred thousand, so no single figure is right for both; and across this machine's own
transcripts the largest context actually seen was 1 000 782 on `claude-opus-5` and 534 018 on
`z-ai/glm-5.3-flash`. A bar pinned at 100% reads as *about to run out*, which is the one thing it must not
say wrongly — so a tile with no source shows the count and no bar, and the Settings field is the way to
get one. Past a window somebody really did name, the bar clamps: an agent that compacts reports the
tokens it had before the compaction landed, and there a full bar is the truth.

`Services/Agents/AgentSessionWatcher` is what follows it: one per tile, debounced, never reading twice at
once, and knowing nothing about any CLI. It carries the two filters a capture already carries — a
conversation older than this tile's current identity is not this tile's to take, and one another tile
holds (`CapturedSessions`) is passed over, claimed rather than merely tested so that two tiles restored
from one layout cannot both take it. A tile claims the conversation it is in the moment it has one — the derived id of a claude or pi
tile included, so a neighbour of the same agent reading first cannot take it — and a *headless* run filed
in the same directory is never a candidate at all: Claude Code marks every message line with its
`entrypoint`, `cli` in the TUI and `sdk-cli` under `-p`, which is what a Goal tile's run and an Agent
tile's session both are, and codex says the same on its first line (`payload.source`: `cli` in the TUI,
`exec` for `codex exec`, `vscode` for an app-server client). pi, opencode and grok mark nothing, so the
first two are read **by id alone** (`IAgentSessionLog.TellsHeadlessRunsApart`): the gauge follows, and a
`/clear` inside their TUI is not adopted, because the newest conversation there may be a Goal tile's run.
The time filter is the **last write** against the tile's own start, not the conversation's start:
`/resume` goes on appending to a conversation begun before the tile, and filtered by its start that move
would never be seen. A followed id is written into the layout (`StoredSessionId`) for claude as well as
for the captured agents, or the next start would resume the conversation derived from the tile — and not
for pi, whose store is read by id alone and so never follows anything to write down
(`TerminalAgentTileViewModel.KeepsSessionId` asks the same question).
**Only the active tile may take a conversation it did not start with** (`IActiveStateTile`): nothing in
the store says which process wrote a new file, so a `/clear` in one tile — or a `claude` started by hand
in a plain terminal next door — is seen by every tile of that agent in the workspace, and the one being
typed into is the active one. The others go on reading the conversation they know. **And only on the heels of an Enter pressed in it** (`IInputSubmissionTile`,
`ConversationFollower.MoveWindow`, 30 s): a `claude` outside this application — a
terminal next door, VS Code — can go on writing a long task into the same directory for minutes after the
user has come back to the tile, so being active is not enough. A new conversation is taken only when it
was written after that Enter *and* the conversation the tile knows was not: a process still writing where
it was has not moved. **And never one seen written while no window was open** (`AgentSessionWatcher`
`NoteStrangersAsync`, `IAgentSessionLog.ListInteractiveAsync`): an Enter that makes the tile's own
transcript write nothing straight away — a menu choice, a permission accepted before a long tool run —
leaves the window open while that outside `claude` goes on writing, and nothing typed here moves the tile
into a conversation that was already alive elsewhere. One the tile has held itself is exempt, so
`/resume` back to what a `/clear` left still follows. Switching the tile to an instance on another account restarts the watcher on that
account's directory and clears the bar, as "New session" does. A reading that
arrives from a watcher "New session" has already replaced is dropped, or it would put the tile back
into the conversation it was asked to leave.

**The following is a collaborator, not more of the tile** (`ViewModels/ConversationFollower`, the shape
`ContextWindowFollower` already takes): the watcher's lifetime, the adoption window and the claim test are
one machine with one reason to change, and the tile already launches an agent, captures a session id and
writes it into the layout. What stays with the tile is what only it can answer — which id it holds,
whether the agent may be resumed on a followed one, and the save that makes it survive a restart.
