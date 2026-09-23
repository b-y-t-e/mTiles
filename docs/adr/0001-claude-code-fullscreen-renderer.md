# ADR 0001 — Claude Code runs on its fullscreen renderer in every tile

- **Status:** accepted
- **Date:** 2026-09-19
- **Where:** `src/mTiles/Program.cs` (`SetDefaultEnv("CLAUDE_CODE_NO_FLICKER", "1")`)

## Context

Claude Code has two renderers, and which one a tile gets is decided by environment variables that
mTiles sets once, at startup, for every terminal it spawns.

- **Classic.** Draws on the terminal's main screen. The conversation goes into the terminal's own
  scrollback, so the tile's scrollbar, drag-selection and select-while-scrolling all work on it.
- **Fullscreen** (`/tui fullscreen`). Draws on the alternate screen, as vim or less do, and scrolls
  the conversation itself. The terminal holds no history for it.

The classic renderer does not survive a tile being narrowed. Measured with a recorded PTY session:

- On **narrowing**, it erases the visible lines and prints the entire conversation again, hard-wrapped
  at the new width, without clearing the scrollback (it sends no `ESC[3J`). The history then holds the
  old copy with the new, narrow copy below it.
- On **widening**, it repaints only the visible screen. Whatever it printed at the narrow width stays
  in the history at that width.
- It breaks lines itself (cursor positioning and explicit newlines), so they are not soft wraps and no
  terminal can rejoin them.

This is Claude Code's behaviour, not the terminal control's: Windows Terminal with the same variable
shows the same history. mTiles resizes tiles constantly — every split narrows one — so under the
classic renderer the history of an agent tile was routinely unreadable.

## History

- **2026-07-06** — mTiles forced the classic renderer (`CLAUDE_CODE_DISABLE_ALTERNATE_SCREEN=1`) and
  switched Claude Code's mouse capture off (`CLAUDE_CODE_DISABLE_MOUSE=1`), to keep native scrollback
  and drag-selection in tiles.
- **2026-09-12** — both variables were dropped after the resize artifacts were traced to the classic
  renderer. Claude Code went back to its default, which is the fullscreen renderer.
- **2026-09-19** — this decision: the fullscreen renderer is forced rather than left as a default.

## Decision

Set `CLAUDE_CODE_NO_FLICKER=1` for every terminal mTiles spawns, and set neither
`CLAUDE_CODE_DISABLE_ALTERNATE_SCREEN` nor `CLAUDE_CODE_DISABLE_MOUSE`.

Forcing it rather than relying on the default matters: Claude Code turns fullscreen off by itself on a
machine where a fullscreen start once crashed ("sticky auto-disable") and over SSH on Windows.
`CLAUDE_CODE_NO_FLICKER=1` is the switch its own messages name as the override for both. A value the
user sets before launching mTiles still wins.

`CLAUDE_CODE_DISABLE_MOUSE` stays unset because in fullscreen the mouse wheel is how the conversation is
scrolled. Selecting text still works with Shift held, which overrides an application's mouse grab.

## Consequences

- **No resize artifacts.** A narrow-then-widen leaves a clean screen and nothing broken behind it.
- **No scrollbar thumb in agent tiles.** The alternate screen has no terminal history, so the tile's
  scrollbar has nothing to show. Windows Terminal shows none either; this is not a bug in the control.
  The conversation is scrolled with the wheel, and Claude Code offers its own "Jump to bottom".
- **Selection needs Shift** while Claude Code owns the mouse.

## How to check it again

`Terminal.Avalonia` has a repro app, `samples/Terminal.Avalonia.ClaudeResizeDemo`. It runs
`claude --continue` in a clean environment, narrows and widens the terminal, and reads the buffer back:

```bash
Terminal.Avalonia.ClaudeResizeDemo.exe --auto               # classic renderer
Terminal.Avalonia.ClaudeResizeDemo.exe --auto --fullscreen  # fullscreen renderer
```

Result on 2026-09-19, same conversation:

| Renderer | Verdict | History |
|---|---|---|
| Classic | BROKEN: 14 screen lines duplicated in history, 304 rows broken at the narrow width | 1793 rows |
| Fullscreen | OK | none, alternate screen on |

Revisit this decision if Claude Code's classic renderer starts clearing the scrollback before it
reprints, or reprints on widening too.
