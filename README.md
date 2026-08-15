![Windows](https://img.shields.io/badge/Windows-0078D4?style=flat&logo=windows&logoColor=white)
![Linux](https://img.shields.io/badge/Linux-FCC624?style=flat&logo=linux&logoColor=black)

# mTiles

Terminal manager built for AI-assisted development. Workspaces, split tiles, database bridge for LLM agents, git — in one window.

![mTiles](assets/screen1.png)

## What makes it different

Unlike Warp, Wave, or WezTerm — mTiles doesn't try to be an AI itself. It manages the environment your AI tools run in.

**Database bridge for LLM agents** — lets Claude Code, OpenCode, or any agent query SQL Server / PostgreSQL directly via a local HTTP server. No credentials are ever exposed to the agent.

**SQL Guard** — write protection enabled by default. INSERT/UPDATE/DELETE require explicit per-database unlock. DROP/TRUNCATE/ALTER always blocked. If the agent attempts a write in read-only mode, a real-time confirmation dialog appears. Keyword scanning strips comments to prevent bypass.

**AI tool profiles** — named shell profiles tied to specific AI binaries (Claude Code, OpenCode, Codex, Pi Agent). Auto-detection of 18+ CLI tools, startup/fallback scripts, and a launch chain that falls back to the next command when one fails and brings the tool back when it exits. Profiles appear only when the tool is installed.

**ThemeBridge** — terminal ANSI palette drives the entire UI. Change the color theme and every surface updates — not just the terminal background. 17 themes (Catppuccin, Tokyo Night, Gruvbox, Rosé Pine, One Dark, Solarized, and more), dark and light.

**Git tile** — staging, diff (unified + side-by-side), commit with message suggestions, stash, push/fetch, tags, undo last commit, unpushed commit markers. No need for a separate Git GUI.

**Workspaces** — each workspace is a directory with its own tile layout, database config, and git branch display. Switch instantly — terminals stay alive (cached views, no respawn).

**Split tiles** — recursive binary tree. Split any tile horizontally or vertically, nest arbitrarily. Each tile can be a terminal, note, todo, git, or database.

## Running

```
git clone https://github.com/b-y-t-e/mTiles.git
cd mTiles
dotnet run --project src/mTiles
```

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

## Tech

.NET 10, Avalonia 12, CommunityToolkit.Mvvm, AvaloniaEdit, and **Terminal.Avalonia** — our own terminal control (VT engine, ConPTY/forkpty, rendering), written for this app and published on NuGet.

## Roadmap

Not promises with dates — the things known to be missing or wrong, roughly in the order they bother us.

**Known limitations**

- **Restarting a shell can stall the UI** for as long as the child takes to die (up to ~2 s). The terminal control kills the old session on the UI thread; fixing it belongs in `Terminal.Avalonia`, not here.
- **Selection inside a full-screen TUI needs Shift held.** mc, vim and opencode grab the mouse, and the terminal control deliberately refuses a one-way override that would leave those apps unable to receive clicks. It is the xterm convention, but a habit to learn.
- **`cmd /c` is only approximately supported** in launch chains: it does not parse its command line by the rules the PTY backend quotes with, and it runs only the first line of a multi-line command. Use PowerShell or a POSIX shell for anything non-trivial.
- **Text wins over images on paste.** When the clipboard holds both (a browser copy, a screenshot tool), Ctrl+V pastes the text and the agent never sees the image. Alt+V still hands it over.
- **Only some agents resume their conversation after a restart.** A tile keeps its `TileId` for ever and the default profiles hand it to the tool as the session id, so closing mTiles and opening it again drops you back into the same conversation — but only for tools that let the *caller* choose the id: **Claude Code** (`--session-id` to create, `--resume` to reattach) and **pi** (`--session-id` for both). Everything else generates its own id and never tells the shell what it was, so the seeded profiles for **OpenCode** and **Codex** fall back to starting a fresh conversation. The tile comes back; what was said in it does not.

**Planned**

- **Collapsible workspace panel** — collapse to a narrow icon strip with workspace initials rather than hiding outright: click to switch, drag the splitter past its minimum to collapse. Reclaims the panel's width without losing quick switching.
- **Activity in the workspace panel** — the list on the left says nothing about what is happening inside a workspace you are not looking at, so a build finishing or an agent waiting for an answer goes unnoticed until you switch to it. The panel should show, per workspace, that its terminals are working: which ones are running something and which are sitting at an idle prompt. Open questions: what counts as "working" when the signal available is a live PTY rather than a process tree, and how it is shown — a dot, a count of busy tiles, or something that also distinguishes "needs you" from "busy".
- **Goal tile — refactor and rework.** The feature works but has not been revisited since it was built, and three things are outstanding. **Attachments:** a goal can only be described in words today, so a screenshot of the bug or the mockup being aimed at cannot be handed to the tool. **A pass over what it actually does:** the phase machine, the prompts and the five-iteration implement/review loop deserve a review against how the tile is really used, rather than more features bolted on. **Its UI:** the type is too small and inconsistent with the rest of the app — the view leans on the smallest token (`FontXs`) for most of the chat log and mixes it with `FontSm` without a rule.
- **Session resume for OpenCode — researched, and it does work.** Measured against **opencode 1.18.11** on Windows; every claim below was run, not read out of `--help`.

  What does *not* work: `--session <id>` only ever **continues** a session, so an id we invent is refused (`Error: Session not found`, exit **1** after ~2 s — which the launch chain already reads correctly as "next command"). And there is nothing to observe at startup either: the TUI creates **no session at all** until the first message is sent, so "start it and pick up the newest id afterwards" has nothing to pick up, quite apart from being a race between two tiles.

  What does work is the reverse of what was assumed — the id can be **chosen** after all, through `opencode import`, which takes a small JSON document and keeps the `id` in it verbatim (only the `ses` prefix is enforced, so `ses_${tileId}` is a legal id and no per-tile bookkeeping is needed). Measured properties, all of them load-bearing: the file's `projectID` and `directory` are **ignored** — the session lands in the project of the *current working directory*, which is the tile's workspace; importing an id that already exists is **non-destructive** (title untouched, existing messages kept), so it is a create-if-missing rather than an overwrite; and it costs ~1.1 s and no model call. A minimal document is `{"info":{"id","slug","projectID","directory","title","version","time":{"created","updated"}},"messages":[]}`. The session is a row in the shared sqlite database (`opencode db path`), so a second process sees it immediately.

  That gives OpenCode the same two-step shape the Claude Code profile already uses — resume first, create on failure:

  ```
  Startup:  opencode --session ses_${tileId}
  Fallback: <write the JSON, opencode import it> ; opencode --session ses_${tileId}
  ```

  The open question is no longer *whether* but *where the JSON is written*. A PowerShell one-liner in the fallback script does work end to end (verified), but it is unreadable and shell-specific. The intended shape is for mTiles to write the document itself and expose the import as the profile's create step, which also settles what a *deleted* session does: the resume exits 1 quickly, the chain moves to the fallback, and the fallback recreates the id — the tile keeps its identity even after the conversation behind it has been thrown away. Worth checking before it ships: whether the same import round-trip is stable across opencode versions, since the document is its export format and not a documented API.

  **Codex** is the same problem with a different CLI (`codex resume <id>`) and comes after OpenCode. Whatever comes out of it should generalise: a small per-tool description of how to create a session, how to resume one, and how the id is chosen or learned — not one branch per tool spread through the launch code.

- **Dictation from another device** — an idea, not yet a design. Over a remote desktop session the microphone is next to *you* and mTiles is on the far machine, so dictation is unusable exactly when it would help most. The shape being considered: the app serves a small local page (URL plus a QR code) that a phone or a browser on the near machine opens, records there, and sends the audio back for the same on-device recognition. Open questions: how the page reaches the app across the remote-desktop boundary, and what it does to the promise that audio never leaves the machine.

## License

MIT
