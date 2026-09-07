# README rewrite plan

## Context

The README opened with a one-line tagline and jumped straight into feature bullets, with no paragraph
stating *why* mTiles exists. The actual reason: working with several AI coding agents at once, in a real
production repo, means losing track of which one is stuck, which crashed, which is waiting for input,
and what each is costing you — mTiles exists to make that workflow fast, comfortable and low-stress.

Two bullets undersold the tiles that matter most in daily use:

- **Goal tile** — in real use it has run unattended for 10-20 hours and landed a task essentially
  correctly. The old bullet described the mechanism (clarify → plan → implement → review → loop) but not
  the payoff.
- **Usage tile** — accurate bullet, but no visual, unlike the phone-dictation section which has one.

The feature list also needed reordering by actual importance (most important tile/feature first), kept
tight — hoppscotch / Tabby / Bruno / zoxide as the tone: short confident "why", terse bold-led bullets,
no padding. Closing line under a horizontal rule at the very end of the file:
`"The light shines in the darkness, and the darkness has not overcome it" John 1:5`.

## Reference style (hoppscotch / Tabby / Bruno / zoxide READMEs)

Banner image → badges → one short paragraph stating what the tool is and who it's for (no marketing
fluff, no repeated claims) → feature list as tight bold-led bullets, each 1-2 lines → install/run
instructions → license. No bullet longer than ~3 lines.

## Changes made to `README.md`

1. New opening paragraph after the badges, before/absorbing the existing tagline — 3-5 sentences: the
   problem (juggling several agents on a real repo, losing track of state and cost) and what mTiles does
   about it (reopen mid-conversation, crashed agent comes back on its own, database/phone/budget handled
   without leaving the keyboard).

2. Reordered `## What it does`, most important first:
   1. Goal tile (promoted to first — finishes a task unattended)
   2. Reopen and carry on (session resume)
   3. Five agents / accounts
   4. Crash recovery / launch chain
   5. Usage tile (screenshot placeholder pending re-upload)
   6. Databases without handing over the password
   7. Dictation + phone (merged into one flowing section)
   8. Other tiles (Git, Note, Todo, Terminal)
   9. Workspaces and split tiles
   10. Theming

3. Rewrote the Goal tile bullet to lead with the outcome ("give it a goal, walk away" / hours-long
   unattended runs), kept the git-safety sentence (working-tree photograph, ref outside history).

4. Rewrote the Usage tile bullet to name the actual accounts shown (Claude Max/Pro, Codex, Antigravity's
   split windows, an OpenRouter key's spend). **Screenshot embed pending** — the original attached image
   was no longer reachable in this session's cache; embed `assets/usage-tile.png` (same treatment as
   `phone-dictation.png`) once the user re-shares it.

5. Left the crash-recovery, accounts, database and dictation bullets as-is content-wise; only reordered.

6. Added closing horizontal rule + quote at the very end of the file, after `## License`.

## Files touched

- `README.md` — restructured as above.
- `assets/usage-tile.png` — **not yet added**; needs the screenshot re-attached in a later turn.

## Verification

- Read the rewritten `README.md` back: opening paragraph readable in ~15s, feature order matches the
  priority list, Goal/Usage bullets carry the upgraded content, John 1:5 line is the last thing in the
  file under a `---` rule.
- No code/build changes — proofreading only.
