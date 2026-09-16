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
a closed tile's conversation stays, and comes back if a tile with that id is an Agent tile again.

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
conversation starts nothing and says so; "New conversation" is the one gesture that forgets.

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

## Images

Pasted (Ctrl+V when the clipboard holds no text, Alt+V always), dropped on the composer, or picked with the
paperclip. Every image is re-encoded as PNG and scaled to at most 1568 px on its long edge
(`ComposerImages`), refused over 5 MB and beyond ten per message, shown as a thumbnail with a remove button
and, once sent, in the user's message. Every session already knew its agent's shape — verified live: Claude
Code, codex, opencode and pi each answered "Red" for a red square. agy's stream input takes text only, and
the message goes without the image and says so.

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

- Importing conversations started outside mTiles (t3code reads Claude's and codex's transcripts).
- Pruning old conversations and checkpoint refs.
- The web view. What it needs is already here: `AgentEvent`/`AgentCommand` as JSON, the reducer, the store,
  and `AgentConversationHost.ExecuteAsync` as the one entry point for commands.
