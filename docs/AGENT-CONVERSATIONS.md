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

## Sub-agents: busy is not the same as a turn

A sub-agent run in the foreground was always drawn: its tool call is open for as long as it works, the
turn is open with it, and the spinner turns. **One run in the background was not**, and that is what this
section is about. Measured 2026-09-24 against Claude Code 2.1.281, driven by a recording script with a
background `Agent` call:

- The `Agent` call is answered `Async agent launched successfully` at once, the agent replies, and the
  turn's `result` arrives — while the sub-agent has its whole job ahead of it.
- Everything the sub-agent says comes with `parent_tool_use_id` set and is not drawn (the inside of one
  task, t3code's rule too). What *is* written about it is a `system` line per task: `task_started`
  (`task_id`, `tool_use_id`, `description`, `is_backgrounded`, `task_type: local_agent`), `task_progress`
  (`description` is the progress line, `last_tool_name`), `task_updated` (`patch.status`: `completed`,
  `failed`, `killed`) and `task_notification` (`status`, `summary` — the result). A shell the sub-agent
  runs is a task too, `local_bash` with `owned_by_subagent`, and is **not** a sub-agent; nor is a shell the
  agent leaves running in the background, which can run for the rest of the session.
- When the sub-agent finishes, **the agent wakes by itself**: a fresh `system/init`, an assistant message
  and a `result` of its own, with no message from anybody.
- `interrupt` sent with no turn open stops every background sub-agent (`task_updated` "killed", then
  `task_notification` "stopped"), and no `result` follows.
- A sub-agent's permission request is an ordinary `can_use_tool` carrying `agent_id` — the task id.

What was wrong, all of it silent: the spinner, the clock and Stop went away with the first `result`; the
agent's own answer later arrived with no turn open, so it was drawn with no spinner and no way to stop it,
and its `result` either closed nothing or closed the turn the user had opened meanwhile; ending that turn
answered a background sub-agent's pending permission with *cancel*; and a restart killed it without a word.

**The contract:** `SubAgentStarted` (id, title, launching call, background), `SubAgentProgressed`
(transient — the running session's news, not worth a row per tool) and `SubAgentEnded` (outcome, result);
`ApprovalRequested`/`QuestionsAsked` carry a `SubAgentId`. The reducer keeps every sub-agent in
`ConversationState.SubAgents` and mirrors each onto the row of the call that launched it. **`IsBusy` is
the question and `IsWorking` is not**: `IsWorking` is still "a turn is open", which is what decides Send
against Stop (a message sent while only sub-agents work is an ordinary message), and `IsBusy` adds any
sub-agent still working — the spinner, the clock, Escape, the tile's activity, the skill-change restart and
the switch confirmations follow it, and the host refuses Undo and a restart under it. A turn's end stops the
foreground sub-agents and leaves the background ones — and what they are asking — alone; the session's end
stops them all, which is also what a conversation reopened from the store starts with.

**A turn nobody sent a message for is still a turn.** Claude Code's session opens one on a top-level
`assistant` or `stream_event` line when none is open (`ClaudeStreamMapper.OpensTurn`) — not on
`system/init`, which a settings change may repeat with no turn behind it. codex's session does the same on
its own thread's `turn/started`.

**Per agent:**

- **codex** runs a sub-agent as a thread of its own. Measured 2026-09-24 against codex-cli 0.156.1 with the
  default `multi_agent` (v1; `multi_agent_v2` is off by default), and matching t3code's capture of v2: the
  parent's thread announces it with a `subAgentActivity` item (`kind: started`, `agentThreadId`,
  `agentPath`) and says `kind: completed` when it is done; in between the child has its own
  `turn/started`/`turn/completed` under its own `threadId`, and the parent's turn ends long before. There is
  no `thread/started` for the child and no `collabAgentToolCall` item in v1. A `subAgentActivity` naming
  *our* thread is a child reporting back (v2) and must not be registered, or our own thread would be
  filtered as another's. **codex does not wake when its sub-agent finishes**, unlike Claude Code — three
  minutes of silence after the child's `completed`; the parent hears of it at its next turn. Stop
  interrupts the children's turns before the parent's; a request from a child thread carries the child's
  `threadId` and outlives our turn.
- **opencode** runs a sub-agent as a child session, and its `task` call stays running until the child is
  done, so the turn stays open by itself. Measured 2026-09-24 against 1.18.18: the child is announced by
  `session.created` with a `parentID`, and asks by `permission.asked` under its **own** `sessionID` —
  which was filtered as another session's, so the child waited for ever under a `task` row saying
  "running". A session is now ours if its `parentID` chain reaches ours (noted from `session.created`/
  `session.updated`, or asked of `GET session/{id}` for one opened before the stream saw it), and
  `permission/{id}/reply` answers it. **The child does not work under this conversation's rules**: it is
  created with its own (`external_directory` allow, `task` deny) and then follows the user's config, so on
  bypass it still asks where that config says ask. A `PATCH` of the child's `permission` the moment it is
  announced is accepted — the rules are appended — and changes nothing; it was measured and taken back out.
  What this can guarantee is that the question reaches the tile.
- **ACP, pi and agy** have no sub-agent events; nothing changed for them.

On screen: the waiting row stays up while anything is busy — with a Stop of its own when no turn is open,
because the composer's slot is Send again — and says what the sub-agent is doing, or how many are working.
The row of the call that launched one turns an arc and says `working…`, with the progress, or later the
first line of the result, under it; the folded group's line names it, turn or no turn.

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

**Once the conversation has something in it, the session is settled and the work is not.** The resume token
belongs to the CLI that issued it and the stored events are that agent's, so no other agent can *continue*
the session — t3code's rule (`ProviderCommandReactor` refuses `thread.turn.start` across drivers) and ours.
What that used to mean here was a refusal: another agent was offered dimmed, with a sentence saying to start
a new conversation. It was right about the mechanism and wrong about the user, who was left typing the state
of the work again into a fresh conversation. Picking another agent now **hands the work over** (see below);
another instance of the *same* agent — another account, another model — is taken as before, and restarts the
session on the same conversation. "Something in it" is read from the store as well as from the screen: a
restored tile is empty until its start has read the store, and the stored answer is what a switch is judged
against when the screen has none. A stored record alone binds nobody — a host writes one as its session
starts — so it is a stored user message that counts. "Last used" is written whenever a message is sent, not
only when the chooser moves.

**Nothing is thrown away by a chooser.** A handover keeps every event of the stretch that is ending, and
**Delete this conversation** is still the one gesture that forgets.

## Both sides of a message are drawn by one control

**A message of the user's is rendered with the same `GoalMarkdownView` as the agent's reply.** It used to
be a `SelectableTextBlock`, and that pair is what "my message is cut off mid-phrase" came down to: the
user's bubble is sized to its own text and so carries no slack, and at a fractional desktop scale (125%,
150%) a width lost to layout rounding drops the last word onto a second line the row is not tall enough to
show. The agent's replies are drawn across the full width, have slack, and were never cut at any width —
which is the measurement that settled it, after a repair aimed at the layout itself failed to change
anything in the running application.

What it costs is the literal reading of what was typed: backticks, `*` and a leading `#` are rendered
rather than shown. **The Goal tile deliberately does the opposite** for its own transcript (its markdown
template says so in as many words — "nor is your own text, where an asterisk is one you typed"), so the
two tiles disagree on this one point on purpose rather than by drift.

## What is folded, and what the round of questions is allowed to take

**Everything the agent did folds itself away, and so does the list of files a turn changed.** A work
group is folded — the live turn's own included — and a checkpoint's file list starts folded: the line
above it already says how many files changed and by how much, and on a turn of thirty paths the
unfolded list stands between the reply and whatever was said next. Opening either is one press and what
the user opens by hand stays as they left it.

**A folded group says what the agent is doing now** (`WorkGroupItemViewModel.Headline`, told which turn
is live by `FollowTurn`): while the turn runs, the line carries the last tool started and not finished;
the moment nothing is running it falls back to the tally ("3 commands · 2 edits"). Unfolding the live
turn's work instead was the earlier rule and is the worse one — thirty rows appearing under the reader
push the reply the turn is working towards off the screen, and then fold themselves when the turn ends,
so the one thing somebody was reading moves twice for reasons they did not ask for. The line is only
the fold's: an open group draws the running tool as a row of its own, and saying it again above is two
marks for one fact.

**A round of questions is the one block drawn edge to edge** (`Border.ask.wide`, no accent rail). Every
answer it offers is a full-width row carrying a sentence or three, so the rail and the side margins that
mark a short block as an aside were being paid for out of the text's own room. What says whose words
these are is the title above it.

**The question and its heading are selectable; the answers it offers are not.** An offered answer is a
button, and a `SelectableTextBlock` inside one takes the press for itself — which would leave the round
readable and unanswerable. So what those rows say travels by the copy button beside the question
instead, descriptions included (`QuestionViewModel.CopyText`), and that button stays on screen for a
round being asked rather than appearing under the pointer.

**Stop is held for half a second after a send** (`AgentConversationTileViewModel.StopButtonHold`). Send
and Stop are one slot — the button becomes the other the moment the turn begins — so a double click is a
turn started and stopped before the agent has said a word, with nothing on screen explaining what
happened. The window is the double-click one and no longer: stopping is the thing somebody wants
*urgently*, and a guard long enough to be felt is worse than the accident it prevents. Escape is gated by
the same answer, since it reaches the same command.

## Handing the work to another agent

**What moves is the work, never the session.** The transcript is this application's and the working tree is
on disk; both survive a change of agent. The CLI's memory of them does not, and no flag of anybody's is
guessed for one — nothing here reaches into `~/.claude` or a rollout file to replay one vendor's transcript
into another's model.

- **The brief is a fold, not a turn** (`ConversationHandover`, pure, in `mTiles.AgentSessions`). What was
  asked for — the first message verbatim, because intent is the one thing no summary may paraphrase — what
  was decided in answered `QuestionsAsked` rounds, the plan with its statuses, which files changed
  (a turn that was undone is left out: its edits are not in the tree), and where it stopped. Nothing is
  asked of any CLI, which is what makes it work when the outgoing agent has crashed — the usual reason
  somebody switches. A summary written by the outgoing agent is an *addition* still to come
  ([`ROADMAP.md`](ROADMAP.md) §6), never the thing this depends on.
- **The budget comes from outside and what it drops is said.** Characters, not tokens; this assembly has no
  tokenizer and every agent counts differently. The middle of the work goes first, oldest message first, and
  the brief says how many were left out — a brief that quietly loses the middle reads as a complete account
  of a smaller task.
- **Somebody's own words are quoted.** A message can carry a `#` at the start of a line, and the brief is
  read as Markdown by whatever it is handed to: unquoted, their heading becomes one of our sections.
- **The seam is written between the two hosts** (`HandoverWriter`, called with the old host disposed and the
  new one not yet built — which is also what lets it work when the outgoing session is dead). It moves the
  record onto the new agent, **clears the resume token**, and appends `HandoverRecorded(From, To, Brief)`.
  Clearing the token is the load-bearing half: `codex resume <unknown>` opens an interactive picker a launch
  waits on for ever, and `agy --conversation <unknown>` warns, starts a *new* conversation and exits 0 — so
  the tile could not tell a resumed session from a lost one. The reducer clears it too, because the record is
  what a launch reads and the state is what a viewer reads; the outgoing agent's plan goes with it, or it
  would be drawn as the arriving agent's to-do list before that agent has said anything.
- **The brief is sent, not recorded as a message** (`SendMessage(Recorded: false)`, its one caller). The
  timeline already carries it, folded, on the handover entry; written a second time as something the user
  said, a page of Markdown they never typed would stand above the new agent's first answer as their own
  words. A start that never reached a live session keeps the brief owed, so the next start delivers it — and what
  says it is owed is the conversation itself (`ConversationHandover.BriefOwedIn`: the seam with nothing but
  notices after it), never a field on the tile, so the debt survives the tile being closed, the application
  being shut down and a send that threw. The brief is folded off the store where no host is alive, which is
  the state a tile refused its start, substituted onto another agent or restored from a layout arrives in.
- **The mode and the effort travel; the model does not.** Mode and effort are this application's own
  canonical scale and mean the same thing wherever they land, and every launch already narrows them to the
  arriving agent's own lists through `AiProcessRunner.Fit`. Dropped instead, a switch quietly put somebody
  working in `bypass` back on the tool's own asking — a change of permissions nobody was told about. What
  travels is the mode and effort the tile was **actually running** — its own pick, or the outgoing
  instance's own answer where the picker was never touched — and never the overrides that produced them: an
  override is what a tile runs *differently* from its instance, so carried as overrides a tile nobody had
  touched handed over nothing and the arriving agent started on *its* instance's defaults, which on a row
  configured for bypass is a CLI editing without asking under a dialog that said nothing of it. Bypass
  therefore travels too, and the confirmation says so in its own sentence: it is a grant given for one agent
  arriving at another, so it is read rather than inherited. The model is spelled for the provider behind the
  account that is leaving, so it never travels. **They travel with the work and not with the tile**: opening
  somebody else's conversation moves the tile onto another agent too, and asks nothing, so it starts on that
  conversation's own mode rather than on this one's — carried there, a bypass granted to one agent for one
  piece of work would be given to every agent whose conversation the user merely looked at.
- **A seam that cannot be written is not a handover.** The record and the event are one move, so an append
  that fails puts the row back (`HandoverWriter`) and the tile stays exactly where it was, with the sentence
  on its own bar. Everything on this path runs under the tile's catch-and-log, and left to it the picker
  showed the agent arriving while the tile went on running the agent leaving, with nothing said anywhere.
- **It asks first, naming the loss before the gain**, and the refusal that remains is `RefusalFor`'s: an
  agent this machine cannot run at all has nothing to hand the work to.
- **The checkpoints carry on unbroken**, because they are ours: `ITurnCheckpoints` is keyed by the
  conversation and knows nothing of agents. *Undo changes* on a turn the previous agent made therefore works
  after a handover, deliberately — the working tree is the shared state, and a gesture that works is worth
  more than a symmetry.

## Which account a stretch of a conversation ran as

**The agent is only half the identity, and the other half is where the CLI keeps its sessions.** A resume
token lives in the account's own directory (`CLAUDE_CONFIG_DIR`, `CODEX_HOME`, `PI_CODING_AGENT_DIR`, a
sign-in's `agents/<agentId>/<signInId>/`), so the same agent on a second subscription is handed a token
naming a session that is not there: it starts cold. The transcript is ours and is drawn whatever happens,
which is exactly what made the loss invisible — an unbroken column of messages over a model that remembers
none of it, and a notice arriving once the new session had already begun.

Four things follow, and each is one of the four places that used to be silent:

- **`SessionConfigured` carries a `SessionAccount`** — agent id, instance id, the instance's name and the
  sign-in id — **stamped by the host, never reported by the session**. A session says what its CLI told it;
  only the host knows the row in Settings it was launched from, which is the thing that decides where the
  token lives. `AgentConversationHost.StartAsync` takes it and `Stamp` fills it in, so it is one place
  rather than one per agent. Null on every event written before this existed, and read as *not said*.
- **The reducer carries it forward and marks every entry with it.** `ConversationState.Account` is the last
  one said, and `TimelineEntry.Account` is what the conversation was running as when that line happened —
  stamped in `Append`, the one place a timeline entry is made, so it is a rule rather than something each
  branch has to remember. Nothing is migrated: an old conversation reads back with nulls.
- **Opening a conversation puts the tile back on it** (`AdoptStoredSession`). The stored record names only
  the agent, so a tile opening a conversation from the list took whichever instance of that agent came
  first — on a machine with two subscriptions, a coin toss, and the losing side resumes nothing. The model,
  the mode and the effort come back with it. **Only when a conversation is opened, and only what the instance would not answer by
  itself**: adopted at every start, a session's report of the instance's own values was pinned into the layout
  as an override nobody chose — freezing a model resolved from `__first_loaded__` and cutting the tile off from
  every later change in Settings. **The model is only ever one somebody picked** (`SessionModelChosen`,
  written by the host when a change of model is taken, and `ConversationState.ChosenModel`, dropped when the
  account moves): what a session reports running is mostly the CLI's resolution of an empty field or an
  alias into a full id, and restored it would freeze that day's default. **The model goes through `IAiAgent.InstanceModel` and never
  back as it was reported**: what a session lists is spelled that CLI's way — opencode and pi qualify it
  with their registry's provider name — so kept as it stands it would be qualified a second time at the next
  launch, into `openrouter/openrouter/auto`. That is the same round trip a model picked in the strip already
  takes, so it is that helper and not a second rule. An override the user already chose is never overruled.
  A switch the user has just confirmed is never adopted away: the host replayed after it still names the
  stretch before it, so the start that the switch caused skips the adoption, and a model spelled for an
  account that is not the one about to run is left behind with it — the rule `OverridesSurvivingSwitch`
  already follows.
- **The seam is drawn.** The first entry of a new stretch carries a rule with the account's name on it
  (`TimelineItemViewModel.Seam`, written by `MarkSeams`), so everything above it stays legible as somebody
  else's work. On the *item* rather than as an item of its own, because `TimelineSync` matches view models
  to records by position — the reducer only appends — and a separator inserted between them would shift
  every index after it. Two stretches are the same account **by id and never by name**: a renamed instance
  is the same account, and two rows seeded with one provider's display name are two identically spelled ones.

**And another login of the same agent is a handover** (`MovesTheLogin`, feeding `ApplySwitchAsync`'s
`handingOver`), exactly as another agent is: the resume token lives in the login's own directory, so the
arriving session could resume nothing. The brief is written and sent, the token cleared, and the question is
`ConfirmHandoverAsync`'s — no dialog to ask in is a no. `ConfirmLeavingTheAccountAsync`, the warning that
used to be the whole answer, is asked now only before anything has been said, and only where the login actually moves — another model or another key on the same account resumes perfectly well.

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

## Compacting the context

The context bar under the composer — the one that says `634.9k / 1M tokens` — carries a **Compact** button
at its right-hand end, where the running agent has a route for it. Pressing it asks the agent to summarise
what has been said so far and carry on from the summary. **Nothing this application holds is touched**:
the transcript is ours, the conversation's events are unchanged, and what shrinks is only what the *CLI* is
still carrying.

**It asks first, and the question opens on Yes.** What that guards is not loss but cost and surprise:
compaction is a model call on somebody's own budget that then changes what the agent remembers for the
rest of the conversation, and the control sits a few pixels from the composer everybody types in. So the
question is a pause rather than an obstacle — Enter takes it — and it is the one confirmation in this
application where the cautious answer is not the one under the keyboard
(`MessageDialog.ConfirmAsync(defaultsToYes: true)`, opt-in per call because every other question here
confirms something that pressing it again will not undo). It is also the one whose **unwired answer is
yes**: `AgentConversationTileViewModel.ConfirmExpectingYes` is a delegate of its own rather than a flag on
`ConfirmAction`, precisely so that the rule the rest of the application keeps — a question about throwing
something away goes unanswered as *no* — cannot be reached for by a later caller who only wanted the
convenient default. The answer is re-checked after the dialog closes: a turn can have started while it was
open, and both agents that run this as a turn of their own refuse it then.

**Where it is drawn is a decision.** It is on the bar and not among the composer's pickers because those
say what the *next message* runs as — settings — while this is an act, and it is the only act there is
about the figure beside it. It is quieter than anything in the composer (no ground until the pointer is on
it, no outline, the strip's own monospace face): the composer's one accent belongs to Send, and spending it
twice leaves neither as the memorable one. Once the window is **80% gone** — `ModelContextWindow`'s own
margin, so the screen has no second opinion about when a window is nearly full — it takes `WarnText`, and
the sentence saying why is in its tooltip, the rule `TileAction.Urgency` set for the tile header: a
coloured control that does not say what the colour means is a mark to be guessed at.

**Three of the six can do it, each by a route of its own, measured 2026-09-20**:

| Agent | How | What comes back |
|---|---|---|
| Claude Code | `/compact` as an ordinary text message on the stream-json stdin | `system/status compacting`, a fresh `system/init`, `compact_boundary` with `compact_metadata.trigger` of `manual`, `result` — a turn like any other |
| codex | `thread/compact/start` with `{threadId}` (0.154.0; with no parameters it answers ``Invalid request: missing field `threadId` ``) | `{}` at once, then a whole turn of its own: `turn/started`, an item of type `contextCompaction`, `turn/completed` |
| opencode | `POST session/{id}/summarize` with `{providerID, modelID}`, both required | the bare `true`, then busy → a user message and its parts → `session.compacted` → idle |
| pi, agy, Grok | — | nothing: neither CLI was on the machine this was measured on, and ACP has no compaction in the protocol |

Four things about that are load-bearing:

- **`ICompactingSession` is a separate interface**, the same division `IProcessBackedSession` makes, because
  it is not true of all of them and because what it costs to be wrong about is a button that is there and
  does nothing. An agent whose author has measured no route gets no control rather than one that fails.
- **It is not a `SendMessage` carrying a slash command.** Only one of the three takes it as a message at
  all, and the host's `SendAsync` writes a `UserMessageAdded` — so `/compact` would stand in the transcript
  as something the user said, on a tile where two of the three agents would never have produced it. The
  command is `CompactContext`, and a session with no route is told out loud rather than ignored.
- **Whether the control is offered is stamped by the host**, not reported by the session
  (`SessionOptionsReported.CanCompact`, in `AgentConversationHost.Stamp`, beside `SessionConfigured.Account`
  and for the same reason): the answer is whether the object the host is holding implements the interface,
  and a session saying it separately is a second copy of one fact that can disagree with the method
  actually called. It goes down with the session, because the options survive the session that reported
  them and a button over a stopped agent can only answer that nothing is running.
- **codex and opencode are asked to compact only between turns.** Both queue or refuse otherwise, and
  codex's queue counts *messages*, which this is not. The turn is opened by the session in both — codex's
  own `turn/started` only records its id, and opencode's is the busy/idle rule — so the tile says Working
  for however long the compaction takes, which is a model call and is not quick. Claude Code needs none of
  that: it is a message, so `SendAsync`'s bookkeeping is already right, and the summary it injects
  afterwards arrives as a `user` line, which the mapper reads for tool results only.

## Images and files

Pasted (Ctrl+V or Alt+V — see `ComposerPaste` below), dropped on the composer, or picked with the
paperclip. **Files copied in a file manager** (Explorer, Dolphin, Nautilus, Thunar) paste as attachments,
exactly as if they had been dropped, and never as the line of paths a Linux file manager puts beside them. Every image is re-encoded as PNG and scaled to at most 1568 px on its long edge
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
  the waiting row's spinner (`gutter-working`), `jump-to-bottom` and `strip-choice`. Each tile keeps only what is its own: the Goal tile's criteria and
  findings, the Agent tile's tool rows and diffs.
- **`@` file mentions**: the same `FileMentionsViewModel` + `FileMentionBehavior` on the composer and on
  every answer box, with Enter left to the suggestions while they are open.
- **Questions** are drawn as the Goal tile's round: a number column, full-width answer rows, a copy button
  per question, "Send answers". The one difference is on purpose: an agent takes its choices as choices (a
  label, several where allowed), so a row is toggled rather than copied into the answer box.
- **Pasting** is `ComposerPaste` for both, in one order: copied files first, then text, then an image.
  Ctrl+V (and Ctrl+Shift+V, Shift+Insert — every key the box pastes on) attaches the files, else pastes the text, else takes the image; Alt+V attaches the files, else the
  image, whatever text is on the clipboard. Ctrl+V is taken from the box in both composers, because they take
  files — left to the box it would paste the paths beside the attachments — and the box's own paste is called
  only once the clipboard is known to hold none (`ComposerPasteTests`).
  **A long text is not pasted into the box at all**: it is written as a note into `.mtiles/attachments/`
  and named where the caret is, as an `@` mention with a chip saying how many words it was
  (`PastedNote`, `ComposerFile.Label`). A composer is three lines tall, so a pasted review or stack
  trace otherwise fills it and pushes the sentence around it off the screen. **Ctrl+Shift+V is the way
  past it** and pastes whatever is on the clipboard as it stands — the one thing that tells the three
  paste keys apart. A note that could not be written is pasted into the box rather than lost.
- **The composer's keys and gestures** are `ComposerInput`: Enter sends, Shift+Enter breaks the line, the
  frame shows the box's focus and a click on it puts the caret in the box. **Enter has to be caught in the
  tunnel**: a multi-line `TextBox` handles Enter itself before a `KeyDown` wired in markup sees it, so both
  keys used to break the line (`ComposerEnterTests` presses real keys through a window to pin it).
- **Waiting**: `WaitingRow` (a braille spinner in the gutter, a stage and a clock) fed by `ElapsedClock` — the Goal
  tile's run, the Agent tile's turn.
- **A copy button per message** (`msg-copy` + `CopyButton`), and keeping the reader's place in the
  transcript by `TranscriptAnchor` — attached to the `ScrollViewer` and nothing else, so neither tile's
  view watches its messages to follow the end. What is at the top of the viewport is remembered when the
  reader moves and put back whenever the layout moves under them: a reader at the bottom follows the new
  messages (`TranscriptFollow`'s rule for what counts as the bottom), and one reading higher up keeps
  their line through a resize, a reflow or a markdown view settling at its final height. The reasoning is
  in [`GOAL.md`](GOAL.md) → *The transcript follows its end*.

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
