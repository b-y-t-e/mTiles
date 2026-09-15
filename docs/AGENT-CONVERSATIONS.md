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

## Not built yet

- Images in the composer (the contract and every session carry them; the view has no paste yet).
- Changing model or mode inside a running conversation (restart the agent after editing the instance).
- Importing conversations started outside mTiles (t3code reads Claude's and codex's transcripts).
- The web view. What it needs is already here: `AgentEvent`/`AgentCommand` as JSON, the reducer, the store,
  and `AgentConversationHost.ExecuteAsync` as the one entry point for commands.
