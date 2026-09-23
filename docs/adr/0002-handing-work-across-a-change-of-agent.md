# ADR 0002 — Picking another agent hands the work over instead of being refused

- **Status:** accepted
- **Date:** 2026-09-21
- **Where:** `src/mTiles.AgentSessions/Conversation/ConversationHandover.cs`,
  `src/mTiles.AgentSessions/Hosting/HandoverWriter.cs`,
  `AgentConversationTileViewModel.ConfirmHandoverAsync` / `CommitSwitchAsync` /
  `OverridesSurvivingSwitch`, `AgentInstanceChooser.OptionFor`
- **Supersedes** the refusal described in [`AGENT-CONVERSATIONS.md`](../AGENT-CONVERSATIONS.md) →
  *Which agent holds the conversation*, and carries out [`ROADMAP.md`](../ROADMAP.md) §6 points 1, 3, 4 and 6.

## Context

A conversation was locked to its agent from the first message. The reason was sound and is unchanged: a
resume token belongs to the CLI that issued it, so Claude Code cannot continue a codex thread and codex
cannot continue a Claude session. The chooser therefore offered another agent dimmed, with a sentence
saying to start a new conversation.

What that sentence asked for was expensive in a way nothing on screen admitted. The user who switches
agent mid-task is nearly always the user whose first agent got stuck — and the answer offered them was an
empty conversation and the state of the work to type out again, while the transcript of it sat one tile
away and the half-finished edits sat in the working tree.

The refusal was right about the *session* and wrong about the *work*. The transcript is this application's
own (`conversations.db`), the changed files are on disk, and both survive a change of agent perfectly well.

## Decision

Picking another agent on a started conversation hands the work over.

1. **The brief is a fold over what we already recorded** (`ConversationHandover`, pure): the first message
   verbatim, the answered rounds of questions, the plan with its statuses, the files each turn changed
   (minus any turn that was undone) and where it stopped. Nothing is asked of any CLI, which is what makes
   it available when the outgoing agent has crashed. It is fitted to a character budget given from outside,
   dropping the middle of the work oldest-first, and it **says how much it dropped**.
2. **The seam is written between the two hosts** (`HandoverWriter`): the stored record moves onto the new
   agent, `HandoverRecorded(From, To, Brief)` is appended, and **the resume token is cleared**.
3. **The brief is sent, not recorded as a message** (`SendMessage(Recorded: false)`). The timeline carries
   it folded on the handover entry.
4. **The permission mode and the effort travel; the model does not.**
5. **It asks first**, naming the loss before the gain, and **no dialog to ask in is a no**.

## Consequences

**What this buys.** The work moves without the transcript being abandoned, and it moves from an agent that
is no longer running — which is the case it exists for. Undo across the seam keeps working for free, since
`ITurnCheckpoints` is keyed by the conversation and knows nothing of agents.

**Clearing the token is the half that would cost a conversation.** Handed on, `codex resume <unknown>`
opens an interactive picker a launch waits on for ever, and `agy --conversation <unknown>` warns, starts a
*new* conversation and exits 0 — so the tile could not tell a resumed session from a lost one. It is
cleared in two places on purpose: the record, which a launch reads, and the reducer's state, which a viewer
reads.

**bypass travels, and that is the uncomfortable part.** Carrying the mode was chosen over dropping it
because dropping it silently put somebody working in bypass back on the tool's own asking — a change of
permissions nobody was told about, in the direction where the symptom is an agent that stops and waits.
Carried, the largest single grant in this application passes from an agent the user granted it to onto one
they did not. Three things stand between that and a surprise: the confirmation says it in a sentence of its
own, `AiProcessRunner.Fit` rounds it down to whatever the arriving agent actually supports, and the mode is
visible in the composer's own picker afterwards. This was weighed and decided by the user on 2026-09-21; the
alternative — dropping to the instance's default and saying so — remains the obvious reversal if a bypass
run ever arrives unwanted through this door.

**The transcript still breaks at the seam.** One conversation in several segments (ROADMAP §6 point 5) is
not built, so the handover is the line the conversation restarts from rather than a rule drawn through it.

**What is deliberately not done.** Nothing reaches into `~/.claude`, a rollout file or any CLI's own store
to replay one vendor's transcript into another's model. What is handed over is what this application
recorded and what is on disk. Copying a session file between account directories — which would make a
*same-agent* change of login lossless — is a separate question and is still unbuilt.
